namespace RoboRightClick.Core;

/// <summary>
/// Everything the hook captured at the moment of one hotkey press. Window handles are plain
/// numbers here; the host resolves them. Nothing about which keys were pressed is kept.
/// </summary>
/// <param name="Foreground">The foreground window at the press.</param>
/// <param name="Tab">The ShellTabWindowClass window above the focused item list, or 0.</param>
/// <param name="Desktop">The press was in the desktop's icon list.</param>
/// <param name="Time">KBDLLHOOKSTRUCT.time of the key-down (GetTickCount milliseconds).</param>
/// <param name="ClipboardSequence">GetClipboardSequenceNumber at the press.</param>
/// <param name="CopyCutNoted">A Ctrl+C or Ctrl+X went to an Explorer view since the hook was set up.</param>
/// <param name="CopyCutTime">Tick of the most recent one.</param>
/// <param name="CopyCutSequence">GetClipboardSequenceNumber when it was pressed, before Explorer handled it.</param>
public readonly record struct HotkeyPress(
    nint Foreground,
    nint Tab,
    bool Desktop,
    uint Time,
    uint ClipboardSequence,
    bool CopyCutNoted,
    uint CopyCutTime,
    uint CopyCutSequence);

/// <summary>One entry of the ShellWindows collection, as the locator read it.</summary>
/// <param name="BrowserWindow">IShellBrowser::GetWindow, or 0 when the entry could not be read.</param>
/// <param name="TopLevel">GetAncestor(BrowserWindow, GA_ROOT), or 0.</param>
public readonly record struct ShellWindowEntry(nint BrowserWindow, nint TopLevel);

/// <summary>Which open File Explorer tab a hotkey press meant.</summary>
public static class TabMatch
{
    /// <summary>
    /// The index of the entry to paste into, or null to refuse
    /// (<see cref="VerbRefusal.FolderNotIdentified"/>). With a tab captured at the press,
    /// exactly one entry must be that tab: zero means it closed or is not a shell browser,
    /// two means the collection is inconsistent, and in neither case is another tab of the
    /// same window a stand-in. Without a captured tab (a File Explorer without tabs), the one
    /// entry whose top-level window is the captured foreground window is used, and only when
    /// every entry reported its window: an unreadable entry could be the real match.
    /// </summary>
    public static int? Choose(nint capturedTab, nint capturedForeground, IReadOnlyList<ShellWindowEntry> entries)
    {
        if (capturedTab != 0)
        {
            return SingleIndex(entries, e => e.BrowserWindow == capturedTab);
        }
        if (capturedForeground == 0 || entries.Any(e => e.BrowserWindow == 0 || e.TopLevel == 0))
        {
            return null;
        }
        return SingleIndex(entries, e => e.TopLevel == capturedForeground);
    }

    private static int? SingleIndex(IReadOnlyList<ShellWindowEntry> entries, Func<ShellWindowEntry, bool> match)
    {
        int? found = null;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!match(entries[i]))
            {
                continue;
            }
            if (found is not null)
            {
                return null;
            }
            found = i;
        }
        return found;
    }
}

/// <summary>
/// Whether the folder open in a File Explorer tab can take a Robo-Paste. The same folders as
/// the right-click: a real folder in the file system. Libraries, This PC, Home, the Recycle
/// Bin, search results and Control Panel have no file-system path; a zip file shown as a
/// folder is a stream (SFGAO_STREAM) and is refused here rather than failing later as a job.
/// An accepted folder still goes through <see cref="VerbRules.PasteDestinationRefusal"/>
/// and <see cref="PathPolicy"/> in the dispatcher, like any right-click.
/// </summary>
public static class PasteFolderRule
{
    public const uint SFGAO_STREAM = 0x00400000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;

    /// <summary>The attributes the host asks IShellItem::GetAttributes for.</summary>
    public const uint AttributeMask = SFGAO_STREAM | SFGAO_FOLDER | SFGAO_FILESYSTEM;

    /// <param name="attributes">The folder item's SFGAO attributes, masked with <see cref="AttributeMask"/>.</param>
    /// <param name="fileSystemPath">GetDisplayName(SIGDN_FILESYSPATH), or null when it failed.</param>
    public static (string? Folder, VerbRefusal? Refusal) Check(uint attributes, string? fileSystemPath)
    {
        var isFileSystemFolder = (attributes & SFGAO_FILESYSTEM) != 0 && (attributes & SFGAO_FOLDER) != 0;
        if (!isFileSystemFolder || (attributes & SFGAO_STREAM) != 0 || string.IsNullOrEmpty(fileSystemPath))
        {
            return (null, VerbRefusal.DestinationNotFileSystem);
        }
        return (fileSystemPath, null);
    }
}

/// <summary>
/// The locator's time budget, measured from the key-down's own tick so that time spent
/// before the worker picked the press up counts too. Ticks are 32-bit milliseconds that
/// wrap every 49.7 days; differences are taken modulo 2^32.
/// </summary>
public static class HotkeyDeadline
{
    /// <summary>After this, the press is abandoned with <see cref="VerbRefusal.ExplorerNotResponding"/>.</summary>
    public const uint BudgetMs = 1500;

    public static uint Elapsed(uint eventTime, uint now) => unchecked(now - eventTime);

    /// <summary>
    /// True at or after the budget. A tick that lies "in the future" wraps to a huge elapsed
    /// time and counts as expired: an inconsistent clock refuses rather than waits.
    /// </summary>
    public static bool Expired(uint eventTime, uint now, uint budgetMs = BudgetMs) => Elapsed(eventTime, now) >= budgetMs;

    /// <summary>Milliseconds left, 0 when expired.</summary>
    public static uint Remaining(uint eventTime, uint now, uint budgetMs = BudgetMs) =>
        Expired(eventTime, now, budgetMs) ? 0 : budgetMs - Elapsed(eventTime, now);
}

public enum ClipboardGuardAction
{
    Proceed,

    /// <summary>Check the sequence number again shortly.</summary>
    Wait,

    /// <summary>Give up: <see cref="VerbRefusal.ClipboardNotReady"/>.</summary>
    Refuse,
}

/// <summary>
/// Closes the race between a fast Ctrl+X and the hotkey (docs/decisions/0001-paste-hotkey.md).
/// The hook sees Ctrl+X before Explorer does; when Explorer is busy, its clipboard write can
/// land after Robo-Paste has already read the clipboard, which would paste the previous
/// clipboard and, for a previous cut, move the wrong files. So when a copy or cut went to an
/// Explorer view shortly before the hotkey, the paste waits until the clipboard sequence
/// number has moved past the one seen at that key, or refuses.
/// </summary>
public static class ClipboardGuard
{
    /// <summary>A copy or cut this long before the hotkey (or less) makes the paste wait for it.</summary>
    public const uint RecentMs = 2000;

    /// <summary>The longest wait, counted from the hotkey's own tick.</summary>
    public const uint WaitMs = 1000;

    /// <param name="currentSequence">GetClipboardSequenceNumber now.</param>
    /// <param name="now">The tick now, same clock as the press.</param>
    public static ClipboardGuardAction Decide(in HotkeyPress press, uint currentSequence, uint now)
    {
        if (!press.CopyCutNoted || HotkeyDeadline.Elapsed(press.CopyCutTime, press.Time) > RecentMs)
        {
            return ClipboardGuardAction.Proceed;
        }
        if (press.ClipboardSequence != press.CopyCutSequence || currentSequence != press.CopyCutSequence)
        {
            return ClipboardGuardAction.Proceed;
        }
        return HotkeyDeadline.Expired(press.Time, now, WaitMs) ? ClipboardGuardAction.Refuse : ClipboardGuardAction.Wait;
    }
}
