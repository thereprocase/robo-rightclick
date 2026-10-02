using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class HotkeyMatcherTests
{
    private const int V = 'V';

    private static HotkeyKeyEvent Down(int vk = V, bool ctrl = true, bool shift = true, bool alt = false, bool win = false, bool lowerIl = false) =>
        new(vk, IsKeyDown: true, ctrl, shift, alt, win, lowerIl);

    [Fact]
    public void The_exact_combination_matches()
    {
        Assert.True(HotkeyMatcher.Matches(HotkeySpec.Default, Down()));
        var noShift = HotkeySpec.Parse("Ctrl+Q").Spec!;
        Assert.True(HotkeyMatcher.Matches(noShift, Down('Q', shift: false)));
    }

    [Fact]
    public void An_extra_alt_or_windows_key_does_not_match()
    {
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down(alt: true)));
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down(win: true)));
    }

    [Fact]
    public void A_missing_or_extra_shift_or_a_missing_ctrl_does_not_match()
    {
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down(shift: false)));
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Parse("Ctrl+Q").Spec!, Down('Q', shift: true)));
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down(ctrl: false)));
    }

    [Fact]
    public void Another_key_does_not_match()
    {
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down('C')));
    }

    [Fact]
    public void A_key_up_never_matches()
    {
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down() with { IsKeyDown = false }));
    }

    [Fact]
    public void Input_injected_from_a_lower_integrity_level_never_matches()
    {
        Assert.False(HotkeyMatcher.Matches(HotkeySpec.Default, Down(lowerIl: true)));
    }

    [Fact]
    public void Copy_and_cut_are_recognized_without_extra_modifiers()
    {
        Assert.True(HotkeyMatcher.IsCopyOrCut(Down('C', shift: false)));
        Assert.True(HotkeyMatcher.IsCopyOrCut(Down('X', shift: false)));
        Assert.False(HotkeyMatcher.IsCopyOrCut(Down('C', shift: true)));
        Assert.False(HotkeyMatcher.IsCopyOrCut(Down('X', shift: false, alt: true)));
        Assert.False(HotkeyMatcher.IsCopyOrCut(Down('C', ctrl: false, shift: false)));
        Assert.False(HotkeyMatcher.IsCopyOrCut(Down('V', shift: false)));
        Assert.False(HotkeyMatcher.IsCopyOrCut(Down('C', shift: false) with { IsKeyDown = false }));
    }
}

public class HotkeyLatchTests
{
    private static readonly LatchAction Pass = LatchAction.Pass;
    private static readonly LatchAction Swallow = LatchAction.Swallow;
    private static readonly LatchAction Trigger = LatchAction.TakeAndTrigger;

    [Fact]
    public void A_press_triggers_once_and_its_repeats_are_taken_without_triggering()
    {
        var latch = new HotkeyLatch();
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, gatePasses: true, time: 1000));
        Assert.True(latch.Swallowing);
        // Typematic repeats: first after the repeat delay, then every ~33 ms.
        Assert.Equal(Swallow, latch.Next(LatchInput.KeyDown, true, 1500));
        for (uint t = 1533; t < 3000; t += 33)
        {
            Assert.Equal(Swallow, latch.Next(LatchInput.KeyDown, true, t));
        }
    }

    [Fact]
    public void The_key_up_is_taken_and_returns_to_idle()
    {
        var latch = new HotkeyLatch();
        latch.Next(LatchInput.KeyDown, true, 10);
        Assert.Equal(Swallow, latch.Next(LatchInput.KeyUp, gatePasses: false, time: 90));
        Assert.False(latch.Swallowing);
        Assert.Equal(Pass, latch.Next(LatchInput.KeyUp, false, 95));
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, true, 120));
    }

    [Fact]
    public void A_double_tap_triggers_twice_because_each_tap_has_its_key_up()
    {
        var latch = new HotkeyLatch();
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, true, 0));
        Assert.Equal(Swallow, latch.Next(LatchInput.KeyUp, false, 60));
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, true, 120));
    }

    [Fact]
    public void A_key_down_that_fails_the_gate_while_swallowing_resets_and_passes()
    {
        var latch = new HotkeyLatch();
        latch.Next(LatchInput.KeyDown, true, 0);
        // The key-up was lost (session locked mid-press); the user now types a plain V
        // somewhere else. It must reach that window.
        Assert.Equal(Pass, latch.Next(LatchInput.KeyDown, gatePasses: false, time: 200));
        Assert.False(latch.Swallowing);
        Assert.Equal(Pass, latch.Next(LatchInput.KeyUp, false, 250));
        Assert.Equal(Pass, latch.Next(LatchInput.KeyDown, false, 300));
    }

    [Fact]
    public void A_gap_over_the_repeat_window_is_a_new_press()
    {
        var latch = new HotkeyLatch();
        latch.Next(LatchInput.KeyDown, true, 0);
        Assert.Equal(Swallow, latch.Next(LatchInput.KeyDown, true, HotkeyLatch.RepeatWindowMs));
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, true, (2 * HotkeyLatch.RepeatWindowMs) + 1));
    }

    [Fact]
    public void The_repeat_window_survives_the_tick_wrapping()
    {
        var latch = new HotkeyLatch();
        latch.Next(LatchInput.KeyDown, true, uint.MaxValue - 10);
        Assert.Equal(Swallow, latch.Next(LatchInput.KeyDown, true, 20));
        Assert.Equal(Trigger, latch.Next(LatchInput.KeyDown, true, 20 + HotkeyLatch.RepeatWindowMs + 1));
    }

    [Fact]
    public void Other_messages_and_an_idle_key_up_pass()
    {
        var latch = new HotkeyLatch();
        Assert.Equal(Pass, latch.Next(LatchInput.Other, true, 0));
        Assert.Equal(Pass, latch.Next(LatchInput.KeyUp, true, 0));
        Assert.Equal(Pass, latch.Next(LatchInput.KeyDown, false, 0));
    }

    /// <summary>
    /// Random event streams. Whatever the order: a trigger is always a take; a key-down that
    /// fails the gate is never taken; and there is at most one trigger per press, where a
    /// press ends at a key-up, a gate failure, or a gap over the repeat window.
    /// </summary>
    [Fact]
    public void Property_trigger_implies_take_and_a_failing_key_down_always_passes()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var latch = new HotkeyLatch();
            uint time = (uint)random.Next();
            var pressOpen = false;
            uint lastDown = 0;
            for (var step = 0; step < 400; step++)
            {
                time = unchecked(time + (uint)random.Next(0, 1600));
                var input = (LatchInput)random.Next(0, 3);
                var gate = random.Next(0, 4) != 0;
                var action = latch.Next(input, gate, time);

                Assert.True(!action.Trigger || action.Take, $"seed {seed} step {step}: trigger without take");
                if (input == LatchInput.KeyDown && !gate)
                {
                    Assert.Equal(Pass, action);
                    pressOpen = false;
                }
                if (input == LatchInput.KeyDown && gate)
                {
                    var newPress = !pressOpen || unchecked(time - lastDown) > HotkeyLatch.RepeatWindowMs;
                    Assert.Equal(newPress, action.Trigger);
                    Assert.True(action.Take);
                    pressOpen = true;
                    lastDown = time;
                }
                if (input == LatchInput.KeyUp)
                {
                    Assert.False(action.Trigger);
                    Assert.Equal(pressOpen, action.Take);
                    pressOpen = false;
                }
                if (input == LatchInput.Other)
                {
                    Assert.Equal(Pass, action);
                }
            }
        }
    }
}

public class HotkeyGateTests
{
    private static readonly WindowClass[] ExplorerChain =
    [
        WindowClass.ShellView, WindowClass.Other /* CtrlNotifySink */, WindowClass.DirectUi /* frame */,
        WindowClass.Other /* DUIViewWndClassName */, WindowClass.ExplorerTab, WindowClass.ExplorerFrame,
    ];

    private static readonly WindowClass[] DesktopChain = [WindowClass.ShellView, WindowClass.Progman];

    private static GateDecision Explorer(
        WindowClass focus = WindowClass.DirectUi, WindowClass[]? ancestors = null, bool rootIsForeground = true, bool caret = false, bool menu = false) =>
        HotkeyGate.Decide(WindowClass.ExplorerFrame, focus, ancestors ?? ExplorerChain, rootIsForeground, caret, menu);

    [Fact]
    public void The_explorer_item_list_takes_the_key_and_names_its_tab()
    {
        var decision = Explorer();
        Assert.Equal(GateResult.Explorer, decision.Result);
        Assert.Equal(4, decision.TabAncestor);
    }

    [Fact]
    public void Explorer_without_tabs_takes_the_key_with_no_tab()
    {
        WindowClass[] noTab = [WindowClass.ShellView, WindowClass.DirectUi, WindowClass.ExplorerFrame];
        Assert.Equal(new GateDecision(GateResult.Explorer, -1), Explorer(ancestors: noTab));
    }

    [Theory]
    [InlineData(WindowClass.Progman)]
    [InlineData(WindowClass.WorkerW)]
    public void The_desktop_icon_list_takes_the_key(WindowClass desktopWindow)
    {
        WindowClass[] chain = [WindowClass.ShellView, desktopWindow];
        var decision = HotkeyGate.Decide(desktopWindow, WindowClass.ListView, chain, true, false, false);
        Assert.Equal(GateResult.Desktop, decision.Result);
    }

    [Fact]
    public void Text_fields_pass()
    {
        // The rename box is an Edit child of the item list; the Windows 11 address bar and
        // search box are XAML islands. Neither is a DirectUIHWND under the shell view.
        WindowClass[] renameChain = [WindowClass.DirectUi, .. ExplorerChain];
        Assert.Equal(GateDecision.Pass, Explorer(focus: WindowClass.Other, ancestors: renameChain));
        WindowClass[] xamlChain = [WindowClass.Other, WindowClass.Other, WindowClass.ExplorerTab, WindowClass.ExplorerFrame];
        Assert.Equal(GateDecision.Pass, Explorer(focus: WindowClass.Other, ancestors: xamlChain));
    }

    [Fact]
    public void A_caret_passes_even_in_the_item_list()
    {
        Assert.Equal(GateDecision.Pass, Explorer(caret: true));
    }

    [Fact]
    public void Menu_mode_passes()
    {
        Assert.Equal(GateDecision.Pass, Explorer(menu: true));
    }

    // NamespaceTreeControl, CtrlNotifySink, the frame's DirectUIHWND, DUIViewWndClassName, tab, window.
    private static readonly WindowClass[] NavigationPaneChain =
        [WindowClass.Other, WindowClass.Other, WindowClass.DirectUi, WindowClass.Other, WindowClass.ExplorerTab, WindowClass.ExplorerFrame];

    [Fact]
    public void The_navigation_pane_passes()
    {
        // SysTreeView32: neither the item list's class nor under a shell view.
        Assert.Equal(GateDecision.Pass, Explorer(focus: WindowClass.Other, ancestors: NavigationPaneChain));
    }

    [Fact]
    public void A_list_class_outside_the_shell_view_passes()
    {
        // Each guard alone must hold: here only the "directly under SHELLDLL_DefView" rule does.
        Assert.Equal(GateDecision.Pass, Explorer(focus: WindowClass.DirectUi, ancestors: NavigationPaneChain));
    }

    [Fact]
    public void A_control_that_is_not_the_item_list_passes_even_under_the_shell_view()
    {
        // And here only the focus-class rule does.
        Assert.Equal(GateDecision.Pass, Explorer(focus: WindowClass.Other));
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(WindowClass.Progman, WindowClass.Other, DesktopChain, true, false, false));
    }

    [Fact]
    public void A_frame_surface_above_the_shell_view_passes()
    {
        // The window frame's own DirectUIHWND (command bar, details pane) sits above the shell view.
        WindowClass[] frameChain = [WindowClass.Other, WindowClass.ExplorerTab, WindowClass.ExplorerFrame];
        Assert.Equal(GateDecision.Pass, Explorer(ancestors: frameChain));
    }

    [Fact]
    public void Focus_in_another_top_level_window_passes()
    {
        Assert.Equal(GateDecision.Pass, Explorer(rootIsForeground: false));
    }

    [Theory]
    [InlineData(WindowClass.Other)] // #32770 Open/Save dialogs, other apps, this app's windows
    [InlineData(WindowClass.ExplorerTab)]
    [InlineData(WindowClass.DirectUi)]
    public void Other_foreground_windows_pass(WindowClass foreground)
    {
        // An Open/Save dialog hosts the same shell view and item list; it must still pass.
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(foreground, WindowClass.DirectUi, ExplorerChain, true, false, false));
    }

    [Fact]
    public void A_worker_window_without_a_shell_view_passes()
    {
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(WindowClass.WorkerW, WindowClass.Other, [WindowClass.WorkerW], true, false, false));
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(WindowClass.WorkerW, WindowClass.ListView, [WindowClass.Other, WindowClass.WorkerW], true, false, false));
    }

    [Fact]
    public void The_list_kinds_do_not_cross_over()
    {
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(WindowClass.ExplorerFrame, WindowClass.ListView, ExplorerChain, true, false, false));
        Assert.Equal(GateDecision.Pass, HotkeyGate.Decide(WindowClass.Progman, WindowClass.DirectUi, DesktopChain, true, false, false));
    }

    [Fact]
    public void No_ancestors_or_too_many_pass()
    {
        Assert.Equal(GateDecision.Pass, Explorer(ancestors: []));
        var deep = Enumerable.Repeat(WindowClass.Other, HotkeyGate.MaxAncestors).Prepend(WindowClass.ShellView).ToArray();
        Assert.Equal(GateDecision.Pass, Explorer(ancestors: deep));
    }

    [Fact]
    public void Class_names_are_classified_exactly()
    {
        Assert.Equal(WindowClass.ExplorerFrame, WindowClasses.Classify("CabinetWClass"));
        Assert.Equal(WindowClass.ExplorerTab, WindowClasses.Classify("ShellTabWindowClass"));
        Assert.Equal(WindowClass.ShellView, WindowClasses.Classify("SHELLDLL_DefView"));
        Assert.Equal(WindowClass.DirectUi, WindowClasses.Classify("DirectUIHWND"));
        Assert.Equal(WindowClass.ListView, WindowClasses.Classify("SysListView32"));
        Assert.Equal(WindowClass.Progman, WindowClasses.Classify("Progman"));
        Assert.Equal(WindowClass.WorkerW, WindowClasses.Classify("WorkerW"));
        foreach (var other in new[] { "Edit", "SysTreeView32", "#32770", "cabinetwclass", "CabinetWClass ", "", "Windows.UI.Input.InputSite.WindowClass" })
        {
            Assert.Equal(WindowClass.Other, WindowClasses.Classify(other));
        }
        Assert.True(WindowClasses.IsExplorerOrDesktop(WindowClass.ExplorerFrame));
        Assert.True(WindowClasses.IsExplorerOrDesktop(WindowClass.WorkerW));
        Assert.False(WindowClasses.IsExplorerOrDesktop(WindowClass.Other));
    }
}
