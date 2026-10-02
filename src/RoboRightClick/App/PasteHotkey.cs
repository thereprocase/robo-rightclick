using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoboRightClick.Core;
using RoboRightClick.Verbs;
using static RoboRightClick.App.KeyboardHookNative;

namespace RoboRightClick.App;

/// <summary>
/// The Robo-Paste hotkey's hook thread (docs/decisions/0001-paste-hotkey.md). A dedicated
/// thread with its own message loop owns two hooks:
/// <list type="bullet">
/// <item>a foreground WinEvent hook (out of context, delivered through this loop), installed
/// while a hotkey is configured;</item>
/// <item>a low-level keyboard hook, installed only while File Explorer or the desktop is the
/// foreground window and removed as soon as anything else is. On every return to Explorer an
/// installed hook older than 10 s is reinstalled, which also recovers one that Windows removed
/// silently after a callback timeout.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The keyboard callback decides with Core's <see cref="HotkeyMatcher"/>,
/// <see cref="HotkeyGate"/> and <see cref="HotkeyLatch"/>, using only calls that send no window
/// message, and allocates nothing. Any exception or doubt passes the key on. When a press
/// triggers, it writes the foreground window, the tab, the desktop flag, the key's tick and two
/// clipboard sequence numbers into one preallocated slot and signals <see cref="Pressed"/>;
/// <see cref="Verbs.ExplorerFolderLocator"/> does the rest on its own thread. A press while the
/// slot is in use is taken and ignored.</para>
/// <para>For the clipboard guard the callback also notes when a Ctrl+C or Ctrl+X went to an
/// Explorer view: one tick and one clipboard sequence number, never which key. Those keys are
/// never taken. Nothing in this class logs, writes a file or reaches a job sink, in either
/// logging mode; scripts/test.sh checks that.</para>
/// <para>One instance per process: the callbacks are static function pointers.</para>
/// </remarks>
internal sealed unsafe class PasteHotkey : IDisposable
{
    private const uint WM_APPLY_SPEC = WM_APP + 1;
    private const long ReinstallIntervalMs = 10_000;

    /// <summary>Longer than any class name the gate compares; a longer name is never one of them.</summary>
    private const int ClassNameChars = 64;

    private static PasteHotkey? s_current;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly AutoResetEvent _pressed = new(initialState: false);

    // Owned by the hook thread.
    private uint _threadId;
    private nint _keyboardHook;
    private nint _foregroundHook;
    private long _keyboardHookInstalledAt;
    private bool _enabled;
    private HotkeySpec? _activeSpec;
    private int _virtualKey;
    private bool _shift;
    private readonly HotkeyLatch _latch = new();
    private readonly nint[] _ancestorHandles = new nint[HotkeyGate.MaxAncestors];
    private readonly WindowClass[] _ancestorClasses = new WindowClass[HotkeyGate.MaxAncestors];
    private bool _copyCutNoted;
    private uint _copyCutTime;
    private uint _copyCutSequence;
    private bool _hasCaptured;
    private uint _lastCaptureTime;

    // The handoff slot: written by the hook thread only after taking _slotBusy, read by the
    // locator only after Pressed fired, released by the locator.
    private int _slotBusy;
    private HotkeyPress _slot;

    private volatile SpecUpdate? _pendingSpec;
    private volatile bool _foregroundHookFailed;
    private volatile bool _keyboardHookFailed;
    private bool _disposed;

    private sealed record SpecUpdate(HotkeySpec? Spec);

    public PasteHotkey()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Paste hotkey",
            // The callback must answer within Windows' low-level hook timeout even when the
            // machine is busy.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>Signaled once per triggering press; read it with <see cref="TakePress"/>, then <see cref="ReleasePress"/>.</summary>
    public WaitHandle Pressed => _pressed;

    /// <summary>Windows refused a hook the configured hotkey needs, so it does nothing now.</summary>
    public bool HookFailed => _foregroundHookFailed || _keyboardHookFailed;

    /// <summary>Starts the hook thread with <paramref name="spec"/> (null: no hooks at all).</summary>
    public void Start(HotkeySpec? spec)
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one paste hotkey may exist per process.");
        }
        _pendingSpec = new SpecUpdate(spec);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)))
        {
            _foregroundHookFailed = true;
        }
    }

    /// <summary>A settings change: hands the new, immutable spec to the hook thread.</summary>
    public void Apply(HotkeySpec? spec)
    {
        _pendingSpec = new SpecUpdate(spec);
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_APPLY_SPEC, 0, 0);
        }
    }

    /// <summary>The press captured at the last signal of <see cref="Pressed"/>.</summary>
    public HotkeyPress TakePress() => _slot;

    /// <summary>The locator is done with the press; the next one may be captured.</summary>
    public void ReleasePress() => Volatile.Write(ref _slotBusy, 0);

    /// <summary>
    /// Ends the loop with WM_QUIT; the hooks are removed on their own thread as it exits.
    /// Waits up to a second. If the process dies instead, Windows removes its hooks with it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var exited = true;
        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            exited = _thread.Join(TimeSpan.FromSeconds(1));
        }
        Interlocked.CompareExchange(ref s_current, null, this);
        if (exited)
        {
            // A thread that did not exit may still be inside a callback that signals the event.
            _pressed.Dispose();
            _ready.Dispose();
        }
    }

    private void Run()
    {
        MSG msg;
        try
        {
            // The first message call creates this thread's queue, so PostThreadMessage works.
            PeekMessage(&msg, 0, 0, 0, PM_NOREMOVE);
            _threadId = GetCurrentThreadId();
            ApplyPendingSpec();
            _ready.Set();

            // GetMessage returns 0 for WM_QUIT and -1 on failure; both end the loop. Hook
            // callbacks run inside it.
            while (GetMessage(&msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message == WM_APPLY_SPEC)
                {
                    ApplyPendingSpec();
                    continue;
                }
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
        }
        catch (Exception)
        {
            // Nothing here may take the tray down; without the loop the hotkey simply does nothing.
            _foregroundHookFailed = true;
        }
        finally
        {
            _enabled = false;
            RemoveKeyboardHook();
            RemoveForegroundHook();
            _ready.Set();
        }
    }

    private void ApplyPendingSpec()
    {
        if (Interlocked.Exchange(ref _pendingSpec, null) is not { } update)
        {
            return;
        }
        // Every settings reload posts the spec, whatever changed. Only a different spec may
        // reset the latch: a reset in the middle of a held press would paste a second time.
        // A foreground hook that Windows refused is tried again on any reload.
        if (Equals(update.Spec, _activeSpec) && (update.Spec is null || _foregroundHook != 0))
        {
            return;
        }
        _activeSpec = update.Spec;
        _latch.Reset();
        _copyCutNoted = false;
        _hasCaptured = false;
        if (update.Spec is not { } spec)
        {
            _enabled = false;
            _virtualKey = 0;
            RemoveKeyboardHook();
            RemoveForegroundHook();
            _foregroundHookFailed = false;
            _keyboardHookFailed = false;
            return;
        }

        _virtualKey = spec.VirtualKey;
        _shift = spec.Shift;
        _enabled = true;
        if (_foregroundHook == 0)
        {
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&OnWinEvent,
                0, 0, WINEVENT_OUTOFCONTEXT);
        }
        _foregroundHookFailed = _foregroundHook == 0;
        OnForegroundChanged();
    }

    /// <summary>Installs the keyboard hook while File Explorer or the desktop is in front, removes it otherwise.</summary>
    private void OnForegroundChanged()
    {
        if (!_enabled)
        {
            return;
        }
        if (WindowClasses.IsExplorerOrDesktop(ClassOf(GetForegroundWindow())))
        {
            InstallKeyboardHook();
        }
        else
        {
            RemoveKeyboardHook();
        }
    }

    private void InstallKeyboardHook()
    {
        var now = Environment.TickCount64;
        if (_keyboardHook != 0)
        {
            if (now - _keyboardHookInstalledAt < ReinstallIntervalMs)
            {
                return;
            }
            // Windows removes a hook whose callback timed out without telling it; a hook that
            // has been in place a while is replaced, which recovers that case.
            RemoveKeyboardHook();
        }
        _keyboardHook = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&OnKeyboard,
            GetModuleHandle(null),
            0);
        _keyboardHookInstalledAt = now;
        _keyboardHookFailed = _keyboardHook == 0;
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private void RemoveForegroundHook()
    {
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            s_current?.OnForegroundChanged();
        }
        catch (Exception)
        {
            // An exception must not cross into user32; the next foreground change retries.
        }
    }

    /// <summary>
    /// WH_KEYBOARD_LL. Nonzero takes the key; anything else, including every failure, passes it on.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnKeyboard(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode == HC_ACTION && lParam != 0 && s_current is { } self && self.HandleKey((int)wParam, (KBDLLHOOKSTRUCT*)lParam))
            {
                return 1;
            }
        }
        catch (Exception)
        {
            // In doubt, the key passes.
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>True takes the key. Allocation-free; runs on the hook thread.</summary>
    private bool HandleKey(int message, KBDLLHOOKSTRUCT* info)
    {
        if (!_enabled)
        {
            return false;
        }
        var virtualKey = (int)info->vkCode;

        // Noted before the hotkey is looked at: Ctrl+X is a cut even when the hotkey is Ctrl+Shift+X.
        if (message == WM_KEYDOWN && virtualKey is HotkeyMatcher.VirtualKeyC or HotkeyMatcher.VirtualKeyX
            && HotkeyMatcher.IsCopyOrCut(ReadEvent(virtualKey, isKeyDown: true, info->flags))
            && EvaluateContext(out _).Result != GateResult.Pass)
        {
            _copyCutNoted = true;
            _copyCutTime = info->time;
            _copyCutSequence = ClipboardNative.GetClipboardSequenceNumber();
        }

        if (virtualKey != _virtualKey)
        {
            return false;
        }

        var input = message switch
        {
            WM_KEYDOWN or WM_SYSKEYDOWN => LatchInput.KeyDown,
            WM_KEYUP or WM_SYSKEYUP => LatchInput.KeyUp,
            _ => LatchInput.Other,
        };
        var gate = GateDecision.Pass;
        nint foreground = 0;
        if (input == LatchInput.KeyDown
            && HotkeyMatcher.Matches(_virtualKey, _shift, ReadEvent(virtualKey, message == WM_KEYDOWN, info->flags)))
        {
            gate = EvaluateContext(out foreground);
        }

        var action = _latch.Next(input, gate.Result != GateResult.Pass, info->time);
        if (action.Trigger)
        {
            Capture(gate, foreground, info->time);
        }
        return action.Take;
    }

    private static HotkeyKeyEvent ReadEvent(int virtualKey, bool isKeyDown, uint flags) => new(
        virtualKey,
        isKeyDown,
        CtrlDown: IsDown(VK_CONTROL),
        ShiftDown: IsDown(VK_SHIFT),
        AltDown: IsDown(VK_MENU) || (flags & LLKHF_ALTDOWN) != 0,
        WinDown: IsDown(VK_LWIN) || IsDown(VK_RWIN),
        LowerIntegrityInjected: (flags & LLKHF_LOWER_IL_INJECTED) != 0);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Where keyboard focus is, from the foreground window's GUI thread. The focus window's
    /// parents are walked up to its top-level window, at most <see cref="HotkeyGate.MaxAncestors"/>
    /// steps; their handles stay in <see cref="_ancestorHandles"/> for the tab lookup.
    /// </summary>
    private GateDecision EvaluateContext(out nint foreground)
    {
        foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return GateDecision.Pass;
        }
        var thread = GetWindowThreadProcessId(foreground, 0);
        if (thread == 0)
        {
            return GateDecision.Pass;
        }
        GUITHREADINFO info = default;
        info.cbSize = (uint)sizeof(GUITHREADINFO);
        if (!GetGUIThreadInfo(thread, &info) || info.hwndFocus == 0)
        {
            return GateDecision.Pass;
        }

        var caret = info.hwndCaret != 0 || (info.flags & GUI_CARETBLINKING) != 0;
        var menuMode = (info.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)) != 0;
        var root = GetAncestor(info.hwndFocus, GA_ROOT);

        var count = 0;
        var reachedRoot = false;
        var current = info.hwndFocus;
        while (count < HotkeyGate.MaxAncestors)
        {
            var parent = GetAncestor(current, GA_PARENT);
            if (parent == 0)
            {
                break;
            }
            _ancestorHandles[count] = parent;
            _ancestorClasses[count] = ClassOf(parent);
            count++;
            if (parent == root)
            {
                reachedRoot = true;
                break;
            }
            current = parent;
        }

        return HotkeyGate.Decide(
            ClassOf(foreground),
            ClassOf(info.hwndFocus),
            _ancestorClasses.AsSpan(0, count),
            focusRootIsForeground: reachedRoot && root == foreground,
            caret,
            menuMode);
    }

    /// <summary>
    /// Hands one press to the locator, unless it is a double tap's second press
    /// (<see cref="HotkeyRepeatGuard"/>) or a press is still in flight. Either way the key
    /// stays taken.
    /// </summary>
    private void Capture(GateDecision gate, nint foreground, uint time)
    {
        if (!HotkeyRepeatGuard.Accept(_hasCaptured, _lastCaptureTime, time)
            || Interlocked.CompareExchange(ref _slotBusy, 1, 0) != 0)
        {
            return;
        }
        _hasCaptured = true;
        _lastCaptureTime = time;
        var tab = gate.Result == GateResult.Explorer && gate.TabAncestor >= 0 ? _ancestorHandles[gate.TabAncestor] : 0;
        _slot = new HotkeyPress(
            foreground,
            tab,
            Desktop: gate.Result == GateResult.Desktop,
            time,
            ClipboardSequence: ClipboardNative.GetClipboardSequenceNumber(),
            _copyCutNoted,
            _copyCutTime,
            _copyCutSequence);
        _pressed.Set();
    }

    private static WindowClass ClassOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return WindowClass.Other;
        }
        var buffer = stackalloc char[ClassNameChars];
        var length = GetClassName(hwnd, buffer, ClassNameChars);
        return length <= 0 ? WindowClass.Other : WindowClasses.Classify(new ReadOnlySpan<char>(buffer, length));
    }
}
