namespace RoboRightClick.Core;

/// <summary>
/// The window classes the hotkey gate tells apart. Anything else is <see cref="Other"/>, and
/// an unknown class never takes the key.
/// </summary>
public enum WindowClass
{
    Other,

    /// <summary>"CabinetWClass": a File Explorer window.</summary>
    ExplorerFrame,

    /// <summary>"ShellTabWindowClass": one tab of a File Explorer window.</summary>
    ExplorerTab,

    /// <summary>"SHELLDLL_DefView": the shell view that hosts a folder's item list.</summary>
    ShellView,

    /// <summary>"DirectUIHWND": File Explorer's item list (and other DirectUI surfaces).</summary>
    DirectUi,

    /// <summary>"SysListView32": the desktop's icon list.</summary>
    ListView,

    /// <summary>"Progman": the desktop window.</summary>
    Progman,

    /// <summary>"WorkerW": the desktop's worker window (it hosts the icons after Show desktop).</summary>
    WorkerW,
}

/// <summary>Window class names as Windows reports them (GetClassNameW), classified without allocating.</summary>
public static class WindowClasses
{
    public const string ExplorerFrameName = "CabinetWClass";
    public const string ExplorerTabName = "ShellTabWindowClass";
    public const string ShellViewName = "SHELLDLL_DefView";
    public const string DirectUiName = "DirectUIHWND";
    public const string ListViewName = "SysListView32";
    public const string ProgmanName = "Progman";
    public const string WorkerWName = "WorkerW";

    /// <summary>Exact, case-sensitive match: these are the names Windows itself registers.</summary>
    public static WindowClass Classify(ReadOnlySpan<char> name) =>
        name.SequenceEqual(ExplorerFrameName) ? WindowClass.ExplorerFrame
        : name.SequenceEqual(ExplorerTabName) ? WindowClass.ExplorerTab
        : name.SequenceEqual(ShellViewName) ? WindowClass.ShellView
        : name.SequenceEqual(DirectUiName) ? WindowClass.DirectUi
        : name.SequenceEqual(ListViewName) ? WindowClass.ListView
        : name.SequenceEqual(ProgmanName) ? WindowClass.Progman
        : name.SequenceEqual(WorkerWName) ? WindowClass.WorkerW
        : WindowClass.Other;

    /// <summary>A foreground window that means "File Explorer or the desktop is in front", so the keyboard hook is installed.</summary>
    public static bool IsExplorerOrDesktop(WindowClass foreground) =>
        foreground is WindowClass.ExplorerFrame or WindowClass.Progman or WindowClass.WorkerW;
}

/// <summary>
/// One keyboard event as the hook sees it, reduced to what the matcher needs. The
/// modifier states are read by the hook when the event arrives; <paramref name="AltDown"/>
/// is true when either VK_MENU or the event's LLKHF_ALTDOWN flag says so.
/// </summary>
/// <param name="IsKeyDown">WM_KEYDOWN. WM_SYSKEYDOWN is not a key-down here: it means Alt (or F10).</param>
/// <param name="LowerIntegrityInjected">LLKHF_LOWER_IL_INJECTED: injected by a process below the tray's integrity level.</param>
public readonly record struct HotkeyKeyEvent(
    int VirtualKey,
    bool IsKeyDown,
    bool CtrlDown,
    bool ShiftDown,
    bool AltDown,
    bool WinDown,
    bool LowerIntegrityInjected);

/// <summary>Whether one key event is the configured combination. Pure and allocation-free: the hook calls it.</summary>
public static class HotkeyMatcher
{
    public const int VirtualKeyC = 'C';
    public const int VirtualKeyX = 'X';

    /// <summary>
    /// A key-down of the configured key with Ctrl down, Shift down exactly when the
    /// combination has it, and neither Alt nor a Windows key down. Input injected from below
    /// the tray's integrity level never matches, as a low-integrity COM caller is refused
    /// (ComCallerSecurity): a sandboxed process must not be able to start a paste.
    /// </summary>
    public static bool Matches(int configuredVirtualKey, bool configuredShift, in HotkeyKeyEvent e) =>
        e.IsKeyDown
        && e.VirtualKey == configuredVirtualKey
        && e.CtrlDown
        && e.ShiftDown == configuredShift
        && !e.AltDown
        && !e.WinDown
        && !e.LowerIntegrityInjected;

    public static bool Matches(HotkeySpec spec, in HotkeyKeyEvent e) => Matches(spec.VirtualKey, spec.Shift, e);

    /// <summary>
    /// Ctrl+C or Ctrl+X (no Shift, Alt or Windows key): a copy or cut that Explorer is about
    /// to write to the clipboard. The hook only notes when one happened; it never takes it.
    /// </summary>
    public static bool IsCopyOrCut(in HotkeyKeyEvent e) =>
        e.IsKeyDown
        && e.VirtualKey is VirtualKeyC or VirtualKeyX
        && e.CtrlDown
        && !e.ShiftDown
        && !e.AltDown
        && !e.WinDown;
}

/// <summary>What the latch does with one event of the configured key.</summary>
/// <param name="Take">The hook returns nonzero: no other window sees the event.</param>
/// <param name="Trigger">Start one Robo-Paste. Never true unless <paramref name="Take"/> is.</param>
public readonly record struct LatchAction(bool Take, bool Trigger)
{
    public static readonly LatchAction Pass = new(false, false);
    public static readonly LatchAction Swallow = new(true, false);
    public static readonly LatchAction TakeAndTrigger = new(true, true);
}

/// <summary>Kinds of event the latch is fed, for the configured key only.</summary>
public enum LatchInput
{
    KeyDown,
    KeyUp,

    /// <summary>Any other message for the key (none is expected; it passes untouched).</summary>
    Other,
}

/// <summary>
/// One physical press, one paste. The low-level hook has no repeat flag, so a held
/// combination arrives as a stream of key-downs; without this each would start a paste.
/// </summary>
/// <remarks>
/// <para>Idle: a key-down that passes the gate is taken and triggers; the latch then swallows.
/// Swallowing: a key-down that passes the gate within <see cref="RepeatWindowMs"/> of the
/// previous one is a repeat (taken, no trigger); a longer gap is a new press (a key-up was
/// lost: a locked session, a hook that was off for a while), so it triggers. The key-up is
/// taken and returns to Idle.</para>
/// <para>A key-down that fails the gate while swallowing resets the latch and is then judged
/// afresh, which in Idle means it passes. That is the guarantee that a lost key-up can never
/// keep eating the key: only a press that is itself the hotkey, in a place the hotkey works,
/// can be taken.</para>
/// <para>Only the configured key ever reaches the latch; modifiers are never taken. Not
/// thread-safe: the hook thread owns it. Allocation-free.</para>
/// </remarks>
public sealed class HotkeyLatch
{
    /// <summary>
    /// Longer than Windows' longest keyboard repeat delay (1 s, the "long" end of the
    /// Keyboard control panel), so a held key's first repeat is still a repeat.
    /// </summary>
    public const uint RepeatWindowMs = 1100;

    private uint _lastDownTime;

    public bool Swallowing { get; private set; }

    /// <param name="gatePasses">
    /// The event is a key-down that <see cref="HotkeyMatcher.Matches(int, bool, in HotkeyKeyEvent)"/>
    /// and <see cref="HotkeyGate.Decide"/> both accept. Ignored for key-ups.
    /// </param>
    /// <param name="time">The hook event's time stamp (GetTickCount milliseconds; wraps every 49.7 days).</param>
    public LatchAction Next(LatchInput input, bool gatePasses, uint time)
    {
        switch (input)
        {
            case LatchInput.KeyDown when Swallowing:
                if (!gatePasses)
                {
                    Reset();
                    return Next(input, gatePasses, time);
                }
                var gap = unchecked(time - _lastDownTime);
                _lastDownTime = time;
                return gap > RepeatWindowMs ? LatchAction.TakeAndTrigger : LatchAction.Swallow;

            case LatchInput.KeyDown:
                if (!gatePasses)
                {
                    return LatchAction.Pass;
                }
                Swallowing = true;
                _lastDownTime = time;
                return LatchAction.TakeAndTrigger;

            case LatchInput.KeyUp when Swallowing:
                Reset();
                return LatchAction.Swallow;

            default:
                return LatchAction.Pass;
        }
    }

    public void Reset()
    {
        Swallowing = false;
        _lastDownTime = 0;
    }
}

/// <summary>Where the gate says a hotkey press belongs.</summary>
public enum GateResult
{
    /// <summary>The key goes to the focused window untouched.</summary>
    Pass,

    /// <summary>The item list of a File Explorer window.</summary>
    Explorer,

    /// <summary>The desktop's icon list.</summary>
    Desktop,
}

/// <param name="TabAncestor">
/// For <see cref="GateResult.Explorer"/>: the index, in the ancestor list given to
/// <see cref="HotkeyGate.Decide"/>, of the tab window that holds the focused list, or -1
/// when there is none (File Explorer without tabs).
/// </param>
public readonly record struct GateDecision(GateResult Result, int TabAncestor)
{
    public static readonly GateDecision Pass = new(GateResult.Pass, -1);
}

/// <summary>
/// Whether a press of the combination belongs to Robo-Paste or to the focused control. An
/// allow-list: the key is taken only when keyboard focus is in a folder's item list (File
/// Explorer) or the desktop's icon list. Everything else passes, which includes the rename
/// box, the address bar and search box (XAML on Windows 11, not Edit controls), the
/// navigation pane, the command bar, Home and Gallery, Open and Save dialogs (#32770), other
/// apps, and this app's own windows.
/// </summary>
public static class HotkeyGate
{
    /// <summary>The hook walks at most this many parents up from the focus window.</summary>
    public const int MaxAncestors = 16;

    /// <param name="foreground">Class of GetForegroundWindow().</param>
    /// <param name="focus">Class of GUITHREADINFO.hwndFocus of the foreground thread.</param>
    /// <param name="ancestors">
    /// Classes of the focus window's parents, nearest first (GetAncestor GA_PARENT), at most
    /// <see cref="MaxAncestors"/>.
    /// </param>
    /// <param name="focusRootIsForeground">
    /// The focus window's top-level window (GA_ROOT) is the foreground window, and the walk
    /// reached it within <see cref="MaxAncestors"/>.
    /// </param>
    /// <param name="caret">A caret exists (hwndCaret or GUI_CARETBLINKING): text is being edited.</param>
    /// <param name="menuMode">A menu is open, or a window is being moved or sized.</param>
    public static GateDecision Decide(
        WindowClass foreground,
        WindowClass focus,
        ReadOnlySpan<WindowClass> ancestors,
        bool focusRootIsForeground,
        bool caret,
        bool menuMode)
    {
        if (caret || menuMode || !focusRootIsForeground || ancestors.Length == 0 || ancestors.Length > MaxAncestors)
        {
            return GateDecision.Pass;
        }

        // The item list is a direct child of SHELLDLL_DefView, in a File Explorer window and on
        // the desktop alike. Requiring the direct parent (not any ancestor) keeps out the other
        // DirectUIHWND surfaces of the window frame, which sit above the shell view.
        if (ancestors[0] != WindowClass.ShellView)
        {
            return GateDecision.Pass;
        }

        switch (foreground)
        {
            case WindowClass.ExplorerFrame when focus == WindowClass.DirectUi:
                return new GateDecision(GateResult.Explorer, ancestors.IndexOf(WindowClass.ExplorerTab));
            case WindowClass.Progman or WindowClass.WorkerW when focus == WindowClass.ListView:
                return new GateDecision(GateResult.Desktop, -1);
            default:
                return GateDecision.Pass;
        }
    }
}

/// <summary>What the tray's hotkey line says.</summary>
public enum HotkeyStatus
{
    /// <summary>"pasteHotkey" is "": the user turned it off.</summary>
    Off,

    /// <summary>"pasteHotkey" could not be used, so it is off; Settings names the problem.</summary>
    Invalid,

    /// <summary>Configured, but Windows refused a hook, so it does nothing.</summary>
    Failed,

    /// <summary>Configured; the hook is in place whenever File Explorer or the desktop is in front.</summary>
    Active,
}

public static class HotkeyStatusRules
{
    /// <param name="spec">The loaded setting (null: off or invalid).</param>
    /// <param name="loadProblems">The settings load problems; one naming "pasteHotkey" means invalid.</param>
    /// <param name="hookFailed">The hook thread could not install a hook it needed.</param>
    public static HotkeyStatus Derive(HotkeySpec? spec, IReadOnlyList<string> loadProblems, bool hookFailed)
    {
        if (spec is null)
        {
            return loadProblems.Any(SettingsSerializer.IsPasteHotkeyProblem) ? HotkeyStatus.Invalid : HotkeyStatus.Off;
        }
        return hookFailed ? HotkeyStatus.Failed : HotkeyStatus.Active;
    }
}
