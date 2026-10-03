using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class TabMatchTests
{
    // Two windows: window 100 with tabs 11 and 12, window 200 with tab 21.
    private static readonly ShellWindowEntry[] Entries = [new(11, 100), new(12, 100), new(21, 200)];

    [Fact]
    public void The_captured_tab_is_chosen()
    {
        Assert.Equal(1, TabMatch.Choose(capturedTab: 12, capturedForeground: 100, Entries));
        Assert.Equal(2, TabMatch.Choose(21, 200, Entries));
    }

    [Fact]
    public void A_tab_that_is_not_listed_is_refused_and_never_replaced_by_a_sibling()
    {
        // Tab 13 of window 100 closed (or its entry could not be read): tabs 11 and 12 of the
        // same window are not a stand-in, even though the window matches.
        Assert.Null(TabMatch.Choose(13, 100, Entries));
        Assert.Null(TabMatch.Choose(13, 100, [new(11, 100)]));
    }

    [Fact]
    public void A_tab_listed_twice_is_refused()
    {
        Assert.Null(TabMatch.Choose(11, 100, [new(11, 100), new(11, 100)]));
    }

    [Fact]
    public void Without_a_tab_the_one_matching_window_is_used()
    {
        Assert.Equal(1, TabMatch.Choose(0, 200, [new(11, 100), new(21, 200)]));
    }

    [Fact]
    public void Without_a_tab_two_entries_in_the_window_are_refused()
    {
        Assert.Null(TabMatch.Choose(0, 100, Entries));
    }

    [Fact]
    public void Without_a_tab_an_unreadable_entry_refuses_the_fallback()
    {
        Assert.Null(TabMatch.Choose(0, 200, [new(11, 100), new(0, 0), new(21, 200)]));
        Assert.Null(TabMatch.Choose(0, 200, [new(11, 100), new(22, 0), new(21, 200)]));
    }

    [Fact]
    public void Without_a_tab_or_a_window_nothing_is_chosen()
    {
        Assert.Null(TabMatch.Choose(0, 0, Entries));
        Assert.Null(TabMatch.Choose(0, 300, Entries));
        Assert.Null(TabMatch.Choose(11, 100, []));
    }
}

public class HotkeyDeadlineTests
{
    [Fact]
    public void The_exact_boundary_is_expired()
    {
        Assert.False(HotkeyDeadline.Expired(1000, 1000 + HotkeyDeadline.BudgetMs - 1));
        Assert.True(HotkeyDeadline.Expired(1000, 1000 + HotkeyDeadline.BudgetMs));
        Assert.Equal(1u, HotkeyDeadline.Remaining(1000, 1000 + HotkeyDeadline.BudgetMs - 1));
        Assert.Equal(0u, HotkeyDeadline.Remaining(1000, 1000 + HotkeyDeadline.BudgetMs));
        Assert.Equal(HotkeyDeadline.BudgetMs, HotkeyDeadline.Remaining(1000, 1000));
    }

    [Fact]
    public void The_tick_wrapping_near_the_maximum_is_handled()
    {
        var press = uint.MaxValue - 100;
        Assert.Equal(201u, HotkeyDeadline.Elapsed(press, 100));
        Assert.False(HotkeyDeadline.Expired(press, 100));
        Assert.Equal(HotkeyDeadline.BudgetMs - 201, HotkeyDeadline.Remaining(press, 100));
        Assert.True(HotkeyDeadline.Expired(press, HotkeyDeadline.BudgetMs - 101));
        Assert.False(HotkeyDeadline.Expired(press, HotkeyDeadline.BudgetMs - 102));
    }

    [Fact]
    public void A_press_time_in_the_future_counts_as_expired()
    {
        Assert.True(HotkeyDeadline.Expired(5000, 4999));
        Assert.Equal(0u, HotkeyDeadline.Remaining(5000, 4999));
    }
}

public class ClipboardGuardTests
{
    private static HotkeyPress Press(bool noted = true, uint copyCutTime = 9_900, uint copyCutSeq = 7, uint pressSeq = 7, uint time = 10_000) =>
        new(Foreground: 1, Tab: 2, Desktop: false, Time: time, ClipboardSequence: pressSeq,
            CopyCutNoted: noted, CopyCutTime: copyCutTime, CopyCutSequence: copyCutSeq);

    [Fact]
    public void No_recent_copy_or_cut_proceeds()
    {
        Assert.Equal(ClipboardGuardAction.Proceed, ClipboardGuard.Decide(Press(noted: false), currentSequence: 7, now: 10_000));
        Assert.Equal(ClipboardGuardAction.Proceed, ClipboardGuard.Decide(Press(copyCutTime: 10_000 - ClipboardGuard.RecentMs - 1), 7, 10_000));
    }

    [Fact]
    public void A_recent_cut_with_the_sequence_unchanged_waits_then_refuses()
    {
        var press = Press(copyCutTime: 10_000 - ClipboardGuard.RecentMs);
        Assert.Equal(ClipboardGuardAction.Wait, ClipboardGuard.Decide(press, 7, 10_000));
        Assert.Equal(ClipboardGuardAction.Wait, ClipboardGuard.Decide(press, 7, 10_000 + ClipboardGuard.WaitMs - 1));
        Assert.Equal(ClipboardGuardAction.Refuse, ClipboardGuard.Decide(press, 7, 10_000 + ClipboardGuard.WaitMs));
    }

    [Fact]
    public void A_changed_sequence_proceeds()
    {
        Assert.Equal(ClipboardGuardAction.Proceed, ClipboardGuard.Decide(Press(), currentSequence: 8, now: 10_050));
        // Already moved by the time of the hotkey itself.
        Assert.Equal(ClipboardGuardAction.Proceed, ClipboardGuard.Decide(Press(pressSeq: 8), currentSequence: 8, now: 10_050));
    }

    [Fact]
    public void The_windows_survive_the_tick_wrapping()
    {
        var press = Press(copyCutTime: uint.MaxValue - 50, time: 50);
        Assert.Equal(ClipboardGuardAction.Wait, ClipboardGuard.Decide(press, 7, 60));
        Assert.Equal(ClipboardGuardAction.Refuse, ClipboardGuard.Decide(press, 7, 50 + ClipboardGuard.WaitMs));
    }
}

public class PasteFolderRuleTests
{
    private const uint Folder = PasteFolderRule.SFGAO_FOLDER;
    private const uint FileSystem = PasteFolderRule.SFGAO_FILESYSTEM;
    private const uint Stream = PasteFolderRule.SFGAO_STREAM;

    [Fact]
    public void A_file_system_folder_is_accepted()
    {
        Assert.Equal((@"D:\work", null), PasteFolderRule.Check(Folder | FileSystem, @"D:\work"));
    }

    [Fact]
    public void A_zip_root_is_refused()
    {
        // A zip shown as a folder is a file-system stream with a path; it would fail as a job.
        Assert.Equal((null, VerbRefusal.DestinationNotFileSystem), PasteFolderRule.Check(Folder | FileSystem | Stream, @"D:\a.zip"));
    }

    [Theory]
    [InlineData(Folder, @"C:\Users\u\Documents")] // a library: no SFGAO_FILESYSTEM
    [InlineData(Folder, null)] // This PC, Recycle Bin, Control Panel, search results
    [InlineData(Folder | FileSystem, null)] // no path came back
    [InlineData(Folder | FileSystem, "")]
    [InlineData(FileSystem, @"D:\file.txt")] // not a folder
    [InlineData(0u, null)]
    public void Folders_without_a_file_system_path_are_refused(uint attributes, string? path)
    {
        Assert.Equal((null, VerbRefusal.DestinationNotFileSystem), PasteFolderRule.Check(attributes, path));
    }

    [Theory]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"relative\folder")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    public void An_accepted_path_still_goes_through_the_paste_destination_check(string path)
    {
        var (folder, refusal) = PasteFolderRule.Check(Folder | FileSystem, path);
        Assert.Null(refusal);
        Assert.Equal(VerbRefusal.DestinationNotFileSystem, VerbRules.PasteDestinationRefusal([folder!], 0));
    }

    [Fact]
    public void A_normal_accepted_path_passes_the_paste_destination_check()
    {
        var (folder, _) = PasteFolderRule.Check(Folder | FileSystem, @"\\server\share\dir");
        Assert.Null(VerbRules.PasteDestinationRefusal([folder!], 0));
    }
}
