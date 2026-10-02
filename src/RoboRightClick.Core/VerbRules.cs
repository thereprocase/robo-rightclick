namespace RoboRightClick.Core;

/// <summary>
/// The decisions the verb dispatcher makes that need no Windows: which clicks are refused,
/// which paste is Explorer's no-op, how a clipboard read is classified and how long the
/// clipboard open is retried. Pure string and number logic: the dispatcher runs on the UI
/// thread, which also serves every COM call, so none of it may touch the file system.
/// </summary>
public static class VerbRules
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Robo-Copy / Robo-Cut. The selection is untrusted (any same-user COM client can send
    /// one), so each path goes through <see cref="PathPolicy"/> before it can be written to
    /// the clipboard, where another program would read it back as a file list. Items with no
    /// file-system path refuse the whole click: copying "the rest" would put a different set
    /// on the clipboard than the one the user selected.
    /// </summary>
    public static VerbRefusal? SelectionRefusal(IReadOnlyList<string> paths, int skippedItems)
    {
        if (paths.Count == 0 || skippedItems > 0)
        {
            return VerbRefusal.SelectionNotFiles;
        }
        foreach (var path in paths)
        {
            if (!PathPolicy.IsAcceptable(path))
            {
                return VerbRefusal.SelectionNotFiles;
            }
        }
        return null;
    }

    /// <summary>
    /// Robo-Paste: exactly one selected item, with a plain drive or network path. The
    /// destination is checked after <see cref="ShellVerbs.PasteDestination"/> trims it, which
    /// is the form the job receives.
    /// </summary>
    public static VerbRefusal? PasteDestinationRefusal(IReadOnlyList<string> selection, int skippedItems)
    {
        var (folder, _) = ShellVerbs.PasteDestination(selection);
        if (folder is null)
        {
            return selection.Count > 1 ? VerbRefusal.NotOneDestination : VerbRefusal.DestinationNotFileSystem;
        }
        if (skippedItems > 0 || !PathPolicy.IsAcceptable(folder))
        {
            return VerbRefusal.DestinationNotFileSystem;
        }
        return null;
    }

    /// <summary>
    /// Explorer's no-op: moving items into the folder they are already in. Pure string
    /// comparison (trailing separators and case ignored): the dispatcher may not stat
    /// anything. A source with no parent (a drive root) never matches.
    /// </summary>
    public static bool IsSameFolderMove(IReadOnlyList<string> sources, string destination, TransferVerb verb)
    {
        if (verb != TransferVerb.Move || sources.Count == 0 || destination.Length == 0)
        {
            return false;
        }
        foreach (var source in sources)
        {
            var parent = WinPath.GetParent(source);
            if (parent.Length == 0 || !WinPath.AreSame(parent, destination))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// What a clipboard read found. <paramref name="decoded"/> is the CF_HDROP result after
    /// the host has resolved any ANSI form, or null when the data could not be read.
    /// A shell IDList without CF_HDROP means virtual items (zip contents, attachments).
    /// Returns null when the paths can be pasted.
    /// </summary>
    public static VerbRefusal? ClassifyClipboard(bool hasHdrop, DropFilesResult? decoded, bool hasShellIdList)
    {
        if (!hasHdrop)
        {
            return hasShellIdList ? VerbRefusal.ClipboardNotFiles : VerbRefusal.ClipboardEmpty;
        }
        if (decoded is null)
        {
            return VerbRefusal.ClipboardEmpty;
        }
        if (decoded.Status == DropFilesStatus.TooLarge)
        {
            return VerbRefusal.ClipboardTooLarge;
        }
        return decoded.Paths.Count == 0 ? VerbRefusal.ClipboardEmpty : null;
    }

    /// <summary>
    /// The waits between OpenClipboard attempts: 10 ms, doubling, the last one shortened so
    /// the total is exactly <paramref name="budget"/>. Never more in total, so a busy
    /// clipboard costs the UI context at most the budget. A zero or negative budget gives
    /// no retries.
    /// </summary>
    public static IReadOnlyList<TimeSpan> RetryDelays(TimeSpan budget)
    {
        var delays = new List<TimeSpan>();
        var remaining = budget;
        var next = FirstRetryDelay;
        while (remaining > TimeSpan.Zero)
        {
            var delay = next < remaining ? next : remaining;
            delays.Add(delay);
            remaining -= delay;
            next = next > TimeSpan.MaxValue / 2 ? TimeSpan.MaxValue : next * 2;
        }
        return delays;
    }
}
