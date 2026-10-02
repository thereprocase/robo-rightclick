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

    /// <summary>Creates the message-only owner window (NativeWindow, parent HWND_MESSAGE).</summary>
    public ClipboardService()
    {
    }

    /// <summary>
    /// Empties the clipboard and writes every entry of
    /// <see cref="ClipboardPayload.ForFiles"/> (CF_HDROP, Preferred DropEffect, and in
    /// ephemeral mode the history exclusions). Returns the refusal on failure, null on success.
    /// </summary>
    public Task<VerbRefusal?> WriteFilesAsync(IReadOnlyList<string> paths, TransferVerb verb, LoggingMode mode) =>
        throw new NotImplementedException();

    /// <summary>
    /// Inside one OpenClipboard: CF_HDROP (Core decoder; DragQueryFileW for the ANSI form,
    /// with the same byte and path limits), then Preferred DropEffect, then the sequence
    /// number, so the number belongs to the data read. GlobalSize over
    /// <see cref="ClipboardPayload.MaxDropFilesBytes"/> is refused before copying. Works for
    /// Explorer's own Ctrl+C / Ctrl+X: OLE renders those formats onto the Win32 clipboard.
    /// No CF_HDROP but other content: <see cref="VerbRefusal.ClipboardNotFiles"/> when a shell
    /// IDList format is present (virtual items), else <see cref="VerbRefusal.ClipboardEmpty"/>.
    /// </summary>
    public Task<ClipboardReadResult> ReadFilesAsync() => throw new NotImplementedException();

    /// <summary>
    /// Explorer clears the clipboard after a cut is pasted. This opens the clipboard, compares
    /// the sequence number with what the paste read, and only then empties it, so a newer copy
    /// by the user or another app is never discarded. Returns whether it cleared.
    /// </summary>
    public bool ClearIfUnchanged(uint sequenceNumber) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
