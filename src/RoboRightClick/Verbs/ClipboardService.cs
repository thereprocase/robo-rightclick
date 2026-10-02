using RoboRightClick.Core;

namespace RoboRightClick.Verbs;

/// <param name="SequenceNumber">GetClipboardSequenceNumber when read; a cut-paste clears the clipboard only if it is unchanged.</param>
internal sealed record ClipboardFiles(IReadOnlyList<string> Paths, DropEffect? PreferredEffect, uint SequenceNumber);

/// <summary>
/// The real Windows clipboard, through raw Win32 calls with a message-only owner window.
/// Formats and bytes come from <see cref="ClipboardPayload"/>; this class only moves them
/// in and out of HGLOBALs. UI thread only: the owner window lives there.
/// </summary>
/// <remarks>
/// OpenClipboard(NULL) would make EmptyClipboard set a null owner, after which
/// SetClipboardData fails, hence the owner window. OpenClipboard fails while another
/// process holds the clipboard; retry a few times over ~100 ms before giving up.
/// </remarks>
internal sealed class ClipboardService : IDisposable
{
    /// <summary>Creates the message-only owner window (NativeWindow, parent HWND_MESSAGE).</summary>
    public ClipboardService()
    {
    }

    /// <summary>
    /// Empties the clipboard and writes every entry of
    /// <see cref="ClipboardPayload.ForFiles"/> (CF_HDROP, Preferred DropEffect, and in
    /// ephemeral mode the history exclusions). Returns the sequence number after the write.
    /// </summary>
    public uint WriteFiles(IReadOnlyList<string> paths, TransferVerb verb, LoggingMode mode) =>
        throw new NotImplementedException();

    /// <summary>
    /// CF_HDROP (Core decoder; DragQueryFileW for ANSI) plus Preferred DropEffect, or null
    /// when the clipboard holds no files. Works for Explorer's own Ctrl+C / Ctrl+X: OLE
    /// renders those formats onto the Win32 clipboard.
    /// </summary>
    public ClipboardFiles? ReadFiles() => throw new NotImplementedException();

    /// <summary>
    /// Explorer clears the clipboard after a cut is pasted. This clears it only when the
    /// sequence number still matches what the paste read, so a newer copy by the user or
    /// another app is never discarded. Returns whether it cleared.
    /// </summary>
    public bool ClearIfUnchanged(uint sequenceNumber) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
