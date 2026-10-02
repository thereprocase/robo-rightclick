using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class VerbRulesTests
{
    // Robo-Copy / Robo-Cut selection

    [Fact]
    public void SelectionOfPlainPathsIsAccepted() =>
        Assert.Null(VerbRules.SelectionRefusal([@"C:\a\b.txt", @"\\srv\share\dir"], 0));

    [Fact]
    public void EmptySelectionIsRefused() =>
        Assert.Equal(VerbRefusal.SelectionNotFiles, VerbRules.SelectionRefusal([], 0));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void AnySkippedItemRefusesTheWholeSelection(int skipped) =>
        Assert.Equal(VerbRefusal.SelectionNotFiles, VerbRules.SelectionRefusal([@"C:\a\b.txt"], skipped));

    [Fact]
    public void OnlySkippedItemsAreRefused() =>
        Assert.Equal(VerbRefusal.SelectionNotFiles, VerbRules.SelectionRefusal([], 2));

    // Every one of these is a hostile or malformed path from an untrusted COM client.
    [Theory]
    [InlineData("")]
    [InlineData(@"relative\file.txt")]
    [InlineData(@"\\?\C:\a")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\*.txt")]
    [InlineData(@"C:\a\b.txt:stream")]
    [InlineData(@"C:\a\NUL")]
    [InlineData("C:\\a\\\"quoted\"")]
    public void HostilePathRefusesTheSelection(string hostile)
    {
        Assert.Equal(VerbRefusal.SelectionNotFiles, VerbRules.SelectionRefusal([hostile], 0));
        // One bad path among good ones still refuses everything: nothing is written.
        Assert.Equal(VerbRefusal.SelectionNotFiles, VerbRules.SelectionRefusal([@"C:\ok.txt", hostile, @"C:\ok2.txt"], 0));
    }

    // Robo-Paste destination

    [Theory]
    [InlineData(@"D:\dst")]
    [InlineData(@"D:\dst\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\srv\share\dst")]
    [InlineData(@"\\srv\share\")]
    public void OnePlainFolderIsAccepted(string folder) =>
        Assert.Null(VerbRules.PasteDestinationRefusal([folder], 0));

    [Fact]
    public void SeveralDestinationsAreRefused() =>
        Assert.Equal(VerbRefusal.NotOneDestination, VerbRules.PasteDestinationRefusal([@"D:\a", @"D:\b"], 0));

    [Fact]
    public void NothingSelectedIsNotAFileSystemDestination() =>
        Assert.Equal(VerbRefusal.DestinationNotFileSystem, VerbRules.PasteDestinationRefusal([], 0));

    [Fact]
    public void SkippedItemIsNotAFileSystemDestination() =>
        Assert.Equal(VerbRefusal.DestinationNotFileSystem, VerbRules.PasteDestinationRefusal([], 1));

    [Fact]
    public void SelectedFolderPlusSkippedItemIsRefused() =>
        Assert.Equal(VerbRefusal.DestinationNotFileSystem, VerbRules.PasteDestinationRefusal([@"D:\dst"], 1));

    [Theory]
    [InlineData("")]
    [InlineData(@"dst")]
    [InlineData(@"\\?\D:\dst")]
    [InlineData(@"D:\dst\..\x")]
    [InlineData(@"D:\dst\*")]
    [InlineData(@"D:\dst:stream")]
    [InlineData(@"D:")]
    public void HostileDestinationIsRefused(string hostile) =>
        Assert.Equal(VerbRefusal.DestinationNotFileSystem, VerbRules.PasteDestinationRefusal([hostile], 0));

    // Same-folder move

    [Theory]
    [InlineData(@"D:\dir\a.txt", @"D:\dir")]
    [InlineData(@"D:\dir\a.txt", @"D:\dir\")]
    [InlineData(@"D:\dir\a.txt", @"d:\DIR")]
    [InlineData(@"D:\dir\sub\", @"D:\dir")]
    [InlineData(@"D:\a.txt", @"D:\")]
    [InlineData(@"\\srv\share\a.txt", @"\\srv\share\")]
    [InlineData(@"\\srv\share\d\a.txt", @"\\SRV\Share\d\")]
    public void MoveIntoItsOwnFolderIsANoOp(string source, string destination) =>
        Assert.True(VerbRules.IsSameFolderMove([source], destination, TransferVerb.Move));

    [Fact]
    public void EverySourceMustShareTheDestination()
    {
        Assert.True(VerbRules.IsSameFolderMove([@"D:\dir\a", @"D:\DIR\b\"], @"D:\dir\", TransferVerb.Move));
        Assert.False(VerbRules.IsSameFolderMove([@"D:\dir\a", @"D:\other\b"], @"D:\dir", TransferVerb.Move));
    }

    [Fact]
    public void CopyIntoItsOwnFolderIsNotANoOp() =>
        Assert.False(VerbRules.IsSameFolderMove([@"D:\dir\a.txt"], @"D:\dir", TransferVerb.Copy));

    [Theory]
    [InlineData(@"D:\dir\a.txt", @"D:\dir\sub")]
    [InlineData(@"D:\dir\a.txt", @"D:\")]
    [InlineData(@"D:\dir\a.txt", @"E:\dir")]
    [InlineData(@"D:\dir\a.txt", @"D:\dir2")]
    [InlineData(@"D:\dir\a.txt", @"D:\di")]
    [InlineData(@"\\srv\share\a.txt", @"\\srv\other\")]
    public void MoveElsewhereIsNotANoOp(string source, string destination) =>
        Assert.False(VerbRules.IsSameFolderMove([source], destination, TransferVerb.Move));

    [Fact]
    public void DriveRootSourceHasNoParentAndNeverMatches()
    {
        Assert.False(VerbRules.IsSameFolderMove([@"D:\"], @"D:\", TransferVerb.Move));
        Assert.False(VerbRules.IsSameFolderMove([@"D:\"], string.Empty, TransferVerb.Move));
    }

    [Fact]
    public void NoSourcesIsNotANoOp() =>
        Assert.False(VerbRules.IsSameFolderMove([], @"D:\dir", TransferVerb.Move));

    // Clipboard classification

    private static DropFilesResult Ok(params string[] paths) => new(paths, DropFilesStatus.Ok);

    [Fact]
    public void FilesOnTheClipboardAreAccepted() =>
        Assert.Null(VerbRules.ClassifyClipboard(true, Ok(@"C:\a"), false));

    [Fact]
    public void HdropWinsOverAShellIdList() =>
        Assert.Null(VerbRules.ClassifyClipboard(true, Ok(@"C:\a"), true));

    [Fact]
    public void NoHdropAndNoIdListIsEmpty() =>
        Assert.Equal(VerbRefusal.ClipboardEmpty, VerbRules.ClassifyClipboard(false, null, false));

    [Fact]
    public void ShellIdListWithoutHdropIsNotFiles() =>
        Assert.Equal(VerbRefusal.ClipboardNotFiles, VerbRules.ClassifyClipboard(false, null, true));

    [Fact]
    public void HdropWithZeroPathsIsEmpty()
    {
        Assert.Equal(VerbRefusal.ClipboardEmpty, VerbRules.ClassifyClipboard(true, DropFilesResult.Empty, false));
        Assert.Equal(VerbRefusal.ClipboardEmpty, VerbRules.ClassifyClipboard(true, DropFilesResult.Empty, true));
    }

    [Fact]
    public void UnreadableHdropIsEmpty() =>
        Assert.Equal(VerbRefusal.ClipboardEmpty, VerbRules.ClassifyClipboard(true, null, false));

    [Fact]
    public void OversizeHdropIsTooLarge()
    {
        Assert.Equal(VerbRefusal.ClipboardTooLarge, VerbRules.ClassifyClipboard(true, DropFilesResult.TooLarge, false));
        // Paths alongside a TooLarge status must never be pasted.
        Assert.Equal(
            VerbRefusal.ClipboardTooLarge,
            VerbRules.ClassifyClipboard(true, new DropFilesResult([@"C:\a"], DropFilesStatus.TooLarge), false));
    }

    [Fact]
    public void ClassifiesTheRealDecoder()
    {
        var bytes = ClipboardPayload.EncodeDropFiles([@"C:\a", @"C:\b"]);
        Assert.Null(VerbRules.ClassifyClipboard(true, ClipboardPayload.DecodeDropFiles(bytes), false));
        Assert.Equal(
            VerbRefusal.ClipboardTooLarge,
            VerbRules.ClassifyClipboard(true, ClipboardPayload.DecodeDropFiles(new byte[ClipboardPayload.MaxDropFilesBytes + 1]), false));
    }

    // Retry delays

    [Fact]
    public void OneSecondBudgetStartsAtTenMillisecondsAndDoubles()
    {
        var delays = VerbRules.RetryDelays(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(10), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(20), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(40), delays[2]);
        Assert.Equal(TimeSpan.FromMilliseconds(80), delays[3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(3_600_000)]
    public void DelaysNeverExceedAndFillTheBudget(int budgetMs)
    {
        var budget = TimeSpan.FromMilliseconds(budgetMs);
        var delays = VerbRules.RetryDelays(budget);
        Assert.All(delays, d => Assert.True(d > TimeSpan.Zero));
        Assert.True(TimeSpan.FromTicks(delays.Sum(d => d.Ticks)) <= budget);
        Assert.Equal(budget, TimeSpan.FromTicks(delays.Sum(d => d.Ticks)));
    }

    [Fact]
    public void EachDelayIsDoubleTheLastUntilTheFinalOneIsShortened()
    {
        var delays = VerbRules.RetryDelays(ClipboardBudget);
        for (var i = 1; i < delays.Count - 1; i++)
        {
            Assert.Equal(delays[i - 1] * 2, delays[i]);
        }
        Assert.True(delays[^1] <= delays[^2] * 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NoBudgetMeansNoRetries(int budgetMs) =>
        Assert.Empty(VerbRules.RetryDelays(TimeSpan.FromMilliseconds(budgetMs)));

    [Fact]
    public void HugeBudgetTerminatesWithoutOverflow()
    {
        var delays = VerbRules.RetryDelays(TimeSpan.MaxValue);
        Assert.InRange(delays.Count, 1, 100);
        Assert.All(delays, d => Assert.True(d > TimeSpan.Zero));
    }

    // The host's budget lives in an internal host type; keep the documented value here.
    private static readonly TimeSpan ClipboardBudget = TimeSpan.FromSeconds(1);
}
