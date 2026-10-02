using System.Runtime.InteropServices;
using System.Windows.Forms;
using RoboRightClick.Core;

namespace RoboRightClick.Verbs;

/// <param name="SequenceNumber">GetClipboardSequenceNumber read inside the same OpenClipboard as the data; a cut-paste clears the clipboard only if it is unchanged.</param>
internal sealed record ClipboardFiles(IReadOnlyList<string> Paths, DropEffect? PreferredEffect, uint SequenceNumber);

/// <summary>A clipboard read: the files, or why there are none.</summary>
internal sealed record ClipboardReadResult(ClipboardFiles? Files, VerbRefusal? Refusal);

/// <summary>
/// The real Windows clipboard, through raw Win32 calls with a message-only owner window.
/// Formats and bytes come from <see cref="ClipboardPayload"/>; this class only moves them
/// in and out of HGLOBALs. UI thread only: the owner window lives there.
/// </summary>
/// <remarks>
/// OpenClipboard(NULL) would make EmptyClipboard set a null owner, after which
/// SetClipboardData fails, hence the owner window. OpenClipboard fails while another
/// process holds it (clipboard managers, RDP clipboard sync, Office): retry with backoff for
/// about one second using awaited delays on the UI context, so the message loop and COM
/// calls keep running meanwhile, then give up with <see cref="VerbRefusal.ClipboardBusy"/>.
/// </remarks>
internal sealed class ClipboardService : IDisposable
{
    public static readonly TimeSpan OpenRetryBudget = TimeSpan.FromSeconds(1);

    /// <summary>HWND_MESSAGE: a window that only receives messages, never shown, parent of nothing visible.</summary>
    private static readonly nint HwndMessage = -3;

    /// <summary>
    /// Formats that describe items without file-system paths. Present without CF_HDROP they
    /// mean "not files on a drive": the shell's IDList (zip contents, other shell folders),
    /// and the virtual-file descriptors that mail clients offer for attachments without any
    /// IDList, which would otherwise read as an empty clipboard.
    /// </summary>
    private static readonly string[] VirtualItemFormats = ["Shell IDList Array", "FileGroupDescriptorW", "FileGroupDescriptor"];

    private readonly NativeWindow _owner = new();
    private readonly Dictionary<string, uint> _formatIds = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the message-only owner window (NativeWindow, parent HWND_MESSAGE).</summary>
    public ClipboardService()
    {
        _owner.CreateHandle(new CreateParams { Parent = HwndMessage });
    }

    /// <summary>
    /// Empties the clipboard and writes every entry of
    /// <see cref="ClipboardPayload.ForFiles"/> (CF_HDROP, Preferred DropEffect, and in
    /// ephemeral mode the history exclusions). Returns the refusal on failure, null on success.
    /// </summary>
    public async Task<VerbRefusal?> WriteFilesAsync(IReadOnlyList<string> paths, TransferVerb verb, LoggingMode mode)
    {
        var entries = ClipboardPayload.ForFiles(paths, verb, mode);
        if (!await OpenWithRetryAsync())
        {
            return VerbRefusal.ClipboardBusy;
        }

        // No await from here to CloseClipboard: the clipboard is held only for this
        // synchronous stretch, so nothing else on this thread can run while it is open.
        try
        {
            if (!ClipboardNative.EmptyClipboard())
            {
                return VerbRefusal.ClipboardBusy;
            }
            foreach (var entry in entries)
            {
                if (!TrySetEntry(entry))
                {
                    // A half-written clipboard (a file list with no cut marker, or the
                    // reverse) is worse than an empty one.
                    ClipboardNative.EmptyClipboard();
                    return VerbRefusal.ClipboardBusy;
                }
            }
            return null;
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    /// <summary>
    /// Inside one OpenClipboard: CF_HDROP (Core decoder; for the ANSI form Core's bounded
    /// splitter and a CP_ACP conversion, with the same byte and path limits; see
    /// <see cref="DecodeAnsiDropFiles"/>), then Preferred DropEffect, then the sequence
    /// number, so the number belongs to the data read. GlobalSize over
    /// <see cref="ClipboardPayload.MaxDropFilesBytes"/> is refused before copying. Works for
    /// Explorer's own Ctrl+C / Ctrl+X: OLE renders those formats onto the Win32 clipboard.
    /// No CF_HDROP but other content: <see cref="VerbRefusal.ClipboardNotFiles"/> when a shell
    /// IDList or virtual-file descriptor is present (virtual items), else
    /// <see cref="VerbRefusal.ClipboardEmpty"/>.
    /// </summary>
    public async Task<ClipboardReadResult> ReadFilesAsync()
    {
        if (!await OpenWithRetryAsync())
        {
            return new ClipboardReadResult(null, VerbRefusal.ClipboardBusy);
        }
        try
        {
            return ReadOpenClipboard();
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    /// <summary>
    /// Explorer clears the clipboard after a cut is pasted. This opens the clipboard, compares
    /// the sequence number with what the paste read, and only then empties it, so a newer copy
    /// by the user or another app is never discarded. Returns whether it cleared.
    /// </summary>
    /// <remarks>
    /// One attempt, no retry: this is synchronous on the UI thread, and a clipboard that stays
    /// is the safe outcome (the sources were moved, so a second paste finds them missing and
    /// reports it).
    /// </remarks>
    public bool ClearIfUnchanged(uint sequenceNumber)
    {
        if (!TryOpen())
        {
            return false;
        }
        try
        {
            return ClipboardNative.GetClipboardSequenceNumber() == sequenceNumber
                && ClipboardNative.EmptyClipboard();
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _owner.DestroyHandle();
    }

    /// <summary>
    /// Another process holds the clipboard for a moment during its own copy or sync. Awaiting
    /// (not sleeping) keeps the UI message loop, and with it every COM call, running.
    /// </summary>
    private async Task<bool> OpenWithRetryAsync()
    {
        if (TryOpen())
        {
            return true;
        }
        foreach (var delay in VerbRules.RetryDelays(OpenRetryBudget))
        {
            await Task.Delay(delay);
            if (TryOpen())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Never opens with a null owner: once the owner window is gone (after
    /// <see cref="Dispose"/>, during shutdown) OpenClipboard(NULL) would still succeed, and a
    /// write would then empty the user's clipboard and fail every SetClipboardData.
    /// </summary>
    private bool TryOpen()
    {
        var owner = _owner.Handle;
        return !_disposed && owner != 0 && ClipboardNative.OpenClipboard(owner);
    }

    private uint FormatId(ClipboardFormat format) =>
        format.RegisteredName is { } name ? RegisteredFormatId(name) : format.StandardId;

    /// <summary>The registered id of <paramref name="name"/>, cached; 0 when registration failed.</summary>
    private uint RegisteredFormatId(string name)
    {
        if (!_formatIds.TryGetValue(name, out var id))
        {
            id = ClipboardNative.RegisterClipboardFormat(name);
            if (id != 0)
            {
                _formatIds[name] = id;
            }
        }
        return id;
    }

    private bool TrySetEntry(ClipboardEntry entry)
    {
        var format = FormatId(entry.Format);
        if (format == 0)
        {
            return false;
        }
        var memory = ClipboardNative.GlobalAlloc(ClipboardNative.GMEM_MOVEABLE, (nuint)entry.Data.Length);
        if (memory == 0)
        {
            return false;
        }

        var handedOver = false;
        try
        {
            var locked = ClipboardNative.GlobalLock(memory);
            if (locked == 0)
            {
                return false;
            }
            try
            {
                Marshal.Copy(entry.Data, 0, locked, entry.Data.Length);
            }
            finally
            {
                ClipboardNative.GlobalUnlock(memory);
            }
            // On success the system owns the memory and it must not be freed here.
            handedOver = ClipboardNative.SetClipboardData(format, memory) != 0;
            return handedOver;
        }
        finally
        {
            if (!handedOver)
            {
                ClipboardNative.GlobalFree(memory);
            }
        }
    }

    private ClipboardReadResult ReadOpenClipboard()
    {
        var hasHdrop = ClipboardNative.IsClipboardFormatAvailable(ClipboardPayload.CF_HDROP);
        var decoded = hasHdrop ? ReadDropFiles() : null;
        var hasVirtualItems = !hasHdrop && VirtualItemFormats.Any(IsAvailable);

        if (VerbRules.ClassifyClipboard(hasHdrop, decoded, hasVirtualItems) is { } refusal)
        {
            return new ClipboardReadResult(null, refusal);
        }

        var effect = ReadPreferredEffect();
        // Last, on purpose: rendering a delayed format (Explorer's own copy) can make its owner
        // write to the clipboard, which bumps the number. Read after the data, it identifies
        // exactly the contents this paste used.
        var sequence = ClipboardNative.GetClipboardSequenceNumber();
        return new ClipboardReadResult(new ClipboardFiles(decoded!.Paths, effect, sequence), null);
    }

    private bool IsAvailable(string registeredName)
    {
        var id = RegisteredFormatId(registeredName);
        return id != 0 && ClipboardNative.IsClipboardFormatAvailable(id);
    }

    private DropFilesResult? ReadDropFiles()
    {
        var handle = ClipboardNative.GetClipboardData(ClipboardPayload.CF_HDROP);
        if (handle == 0)
        {
            return null;
        }
        // Any process can put anything here: check the size before allocating a copy.
        var size = ClipboardNative.GlobalSize(handle);
        if (size > ClipboardPayload.MaxDropFilesBytes)
        {
            return DropFilesResult.TooLarge;
        }
        if (size == 0)
        {
            return null;
        }

        var bytes = CopyBytes(handle, (int)size);
        if (bytes is null)
        {
            return null;
        }
        var decoded = ClipboardPayload.DecodeDropFiles(bytes);
        return decoded.Status == DropFilesStatus.Ansi ? DecodeAnsiDropFiles(bytes) : decoded;
    }

    /// <summary>
    /// The legacy narrow DROPFILES depends on the active code page, which Core does not know.
    /// Core finds the names in the copied bytes with every offset bounded; each one is then
    /// converted with CP_ACP, as shell32's DragQueryFileW does. DragQueryFileW itself is not
    /// used: it trusts pFiles and the terminators, so a malformed block from any process
    /// would make it read past the end of the HGLOBAL, and one call per index rescans the
    /// list from the start, which for the path limit would hold the UI thread for minutes.
    /// Same limits as the wide decoder: too many names or any name over the path limit is
    /// refused, never truncated (a truncated list would paste a different set).
    /// </summary>
    private static unsafe DropFilesResult? DecodeAnsiDropFiles(byte[] block)
    {
        if (VerbRules.SplitAnsiDropFiles(block) is not { } split)
        {
            return null;
        }
        if (split.Status == DropFilesStatus.TooLarge)
        {
            return DropFilesResult.TooLarge;
        }

        var paths = new List<string>(split.Names.Count);
        fixed (byte* start = block)
        {
            foreach (var name in split.Names)
            {
                var (offset, byteCount) = name.GetOffsetAndLength(block.Length);
                var source = start + offset;
                var length = ClipboardNative.MultiByteToWideChar(
                    ClipboardNative.CP_ACP, 0, source, byteCount, null, 0);
                if (length <= 0)
                {
                    return null;
                }
                if (length > PathPolicy.MaxPathLength)
                {
                    return DropFilesResult.TooLarge;
                }
                var buffer = new char[length];
                fixed (char* target = buffer)
                {
                    if (ClipboardNative.MultiByteToWideChar(
                            ClipboardNative.CP_ACP, 0, source, byteCount, target, length) != length)
                    {
                        return null;
                    }
                }
                paths.Add(new string(buffer));
            }
        }
        return new DropFilesResult(paths, DropFilesStatus.Ok);
    }

    private DropEffect? ReadPreferredEffect()
    {
        if (!IsAvailable(ClipboardPayload.PreferredDropEffectFormat))
        {
            return null;
        }
        var handle = ClipboardNative.GetClipboardData(RegisteredFormatId(ClipboardPayload.PreferredDropEffectFormat));
        if (handle == 0 || ClipboardNative.GlobalSize(handle) < 4)
        {
            return null;
        }
        var bytes = CopyBytes(handle, 4);
        return bytes is null ? null : ClipboardPayload.DecodeDropEffect(bytes);
    }

    private static byte[]? CopyBytes(nint handle, int length)
    {
        var locked = ClipboardNative.GlobalLock(handle);
        if (locked == 0)
        {
            return null;
        }
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(locked, bytes, 0, length);
            return bytes;
        }
        finally
        {
            ClipboardNative.GlobalUnlock(handle);
        }
    }
}
