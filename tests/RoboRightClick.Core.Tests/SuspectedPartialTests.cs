using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// A file an earlier paste may have left half written is never settled by a configured Skip
/// or KeepNewer in its "Try again" child: the parent's knowledge reaches the child's decision,
/// the user is asked, and a file the user keeps is still reported.
/// </summary>
public class SuspectedPartialTests
{
    private static readonly PlannedFile Big = new(@"C:\src\big.iso", @"\\nas\share\big.iso", new FileFacts(4_000, FakeDisk.BaseTime));
    private static readonly PlannedFile Small = new(@"C:\src\small.txt", @"\\nas\share\small.txt", new FileFacts(10, FakeDisk.BaseTime));

    private static ExecutionPlan Parent(ConflictPolicy policy = ConflictPolicy.Ask) => new(
        [new ExecutionStep(new RobocopyStep(@"C:\src", @"\\nas\share", ["big.iso", "small.txt"], Recursive: false, Move: false), policy, [Big, Small])],
        [], [], [], []);

    /// <summary>The parent: robocopy died inside big.iso with no ERROR line (exit below 0).</summary>
    private static StepLedger DiedMidFile(ConflictPolicy policy = ConflictPolicy.Ask)
    {
        var ledger = new StepLedger(Parent(policy));
        ledger.StepStarted(0);
        ledger.Apply(0, new FileReported(10, Small.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(-1), killedByCancel: false);
        return ledger;
    }

    /// <summary>The child's scan: big.iso is at the destination, full length and newer than its source.</summary>
    private static RetryScan ChildScan(StepLedger parent, SuspectedPartials? suspected, out ScanResult marked)
    {
        var retry = RetryPlanner.ForFailures(Parent(), parent.RetryCandidates(_ => false), [])!;
        var disk = new FakeDisk().File(Big.SourcePath, size: 4_000).File(Big.DestinationPath, size: 4_000, minute: 30);
        var rescan = RetryPlanner.Rescan(retry, disk, disk, null, CancellationToken.None);
        marked = RetryPlanner.MarkSuspected(rescan.Rest, suspected);
        return rescan;
    }

    private static SuspectedPartials SuspectedOf(StepLedger ledger)
    {
        var set = new SuspectedPartials();
        foreach (var file in ledger.SuspectedPartials(_ => false))
        {
            set.Add(file.DestinationPath);
        }
        return set;
    }

    [Fact]
    public void The_in_flight_file_of_a_run_that_died_is_suspected_and_retried_without_overwriting_unasked()
    {
        var ledger = DiedMidFile();

        Assert.Equal([Big], ledger.SuspectedPartials(_ => false));
        Assert.False(Assert.Single(ledger.RetryCandidates(_ => false)).MayOverwrite);
    }

    [Fact]
    public void A_file_that_was_there_before_under_skip_flags_is_not_suspected()
    {
        // Robocopy never writes over a file that existed when its Skip-flag step started.
        var ledger = DiedMidFile();

        Assert.Empty(ledger.SuspectedPartials(path => WinPath.Comparer.Equals(path, WinPath.NormalizeForMatch(Big.DestinationPath))));
        Assert.Equal([Big], DiedMidFile(ConflictPolicy.Replace).SuspectedPartials(_ => true));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.KeepNewer)]
    public void A_configured_policy_never_settles_a_suspected_partial_unasked(ConflictPolicy configured)
    {
        var parent = DiedMidFile();
        ChildScan(parent, SuspectedOf(parent), out var scan);

        var conflict = Assert.Single(scan.Conflicts);
        Assert.True(conflict.SuspectedPartial);
        Assert.Equal([conflict], ExecutionPlanner.ConflictsToAsk(scan.Conflicts, configured));
        // Without the user's answer the plan cannot be made: nothing keeps it by default.
        Assert.Throws<InvalidOperationException>(() => ExecutionPlanner.Apply(scan, configured, choice: null, _ => false));

        var replaced = ExecutionPlanner.Apply(scan, configured, new ConflictChoice.ReplaceAll(), _ => false);
        var step = Assert.Single(replaced.Steps, s => s.Files.Contains(Big));
        Assert.Equal(ConflictPolicy.Replace, step.Policy);
        Assert.Empty(replaced.Kept);
    }

    [Fact]
    public void Without_the_parents_knowledge_a_configured_skip_would_keep_the_partial_silently()
    {
        // The failure this guards against: the child's scan alone sees a same-size, newer file.
        var parent = DiedMidFile();
        ChildScan(parent, suspected: null, out var scan);

        Assert.Empty(ExecutionPlanner.ConflictsToAsk(scan.Conflicts, ConflictPolicy.Skip));
        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Skip, choice: null, _ => false);
        Assert.DoesNotContain(plan.Steps.SelectMany(s => s.Files), f => f.SourcePath == Big.SourcePath);
        Assert.False(Assert.Single(plan.Kept).SuspectedPartial);
    }

    [Fact]
    public void A_suspected_partial_the_user_keeps_stays_marked_in_the_plan()
    {
        var parent = DiedMidFile();
        ChildScan(parent, SuspectedOf(parent), out var scan);

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Skip, new ConflictChoice.SkipAll(), _ => false);

        Assert.True(Assert.Single(plan.Kept).SuspectedPartial);
    }

    [Fact]
    public void Ordinary_conflicts_still_follow_the_configured_policy_and_keep_newer_flags_stand_in_only_when_they_agree()
    {
        var other = new FileConflict(@"C:\src\a.txt", @"D:\dst\a.txt", new FileFacts(5, FakeDisk.BaseTime), new FileFacts(5, FakeDisk.BaseTime.AddMinutes(9)));
        var suspect = new FileConflict(@"C:\src\b.bin", @"D:\dst\b.bin", new FileFacts(9, FakeDisk.BaseTime), new FileFacts(9, FakeDisk.BaseTime.AddMinutes(9)))
        {
            SuspectedPartial = true,
        };
        var step = new RobocopyStep(@"C:\src", @"D:\dst", ["a.txt", "b.bin"], Recursive: false, Move: false);
        var files = new[]
        {
            new PlannedFile(other.SourcePath, other.DestinationPath, other.Source),
            new PlannedFile(suspect.SourcePath, suspect.DestinationPath, suspect.Source),
        };
        var scan = new ScanResult(new PastePlan([step], [], []), [new StepScan(step, files, [])], [other, suspect], []);

        Assert.Equal([suspect], ExecutionPlanner.ConflictsToAsk(scan.Conflicts, ConflictPolicy.KeepNewer));
        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.KeepNewer, new ConflictChoice.ReplaceAll(), _ => false);

        // a.txt: the existing file is newer, so KeepNewer keeps it. b.bin: replaced on the
        // user's word, although the date would have kept it; robocopy's /XO must not decide it.
        Assert.Equal(other.DestinationPath, Assert.Single(plan.Kept).DestinationPath);
        Assert.All(plan.Steps, s => Assert.NotEqual(ConflictPolicy.KeepNewer, s.Policy));
        Assert.Equal(ConflictPolicy.Replace, Assert.Single(plan.Steps, s => s.Files.Any(f => f.SourcePath == suspect.SourcePath)).Policy);
    }

    [Fact]
    public void The_dialog_marks_suspected_files_first_and_says_why()
    {
        var parent = DiedMidFile();
        ChildScan(parent, SuspectedOf(parent), out var scan);
        var conflict = Assert.Single(scan.Conflicts);

        Assert.StartsWith(ConflictSelection.SuspectedPartialNote, ConflictSelection.Note(conflict));
        Assert.StartsWith("This file was left possibly incomplete", ConflictSelection.SuspectedNotice(scan.Conflicts));
        Assert.Null(ConflictSelection.SuspectedNotice([conflict with { SuspectedPartial = false }]));
    }

    [Fact]
    public void A_child_that_kept_a_suspected_file_is_not_a_clean_done()
    {
        var child = DisplayAndTrayTests.Job(JobState.Done) with { ParentId = Guid.NewGuid(), MayBeIncomplete = 1 };

        Assert.True(child.NeedsAttention);
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(child));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(child));
        Assert.Equal("Done; 1 file may be incomplete", JobStateText.For(child));
        Assert.Contains("may have left incomplete", JobStateText.SummaryHeading(child));
        var toast = ToastText.ForFinished(child, notifyOnComplete: false);
        Assert.NotNull(toast);
        Assert.Equal(ToastKind.Warning, toast.Kind);
    }
}
