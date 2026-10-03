using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// What the ledger says a run did not finish decides the job's end state and its "may be
/// incomplete" list, per step: never the exit code or the job's state alone.
/// </summary>
public class UnfinishedFileTests
{
    private static PlannedFile File(string name, long size = 10) =>
        new($@"C:\src\T\{name}", $@"D:\dst\T\{name}", new FileFacts(size, FakeDisk.BaseTime));

    private static readonly PlannedFile A = File("a.txt");
    private static readonly PlannedFile B = File("b.txt");
    private static readonly PlannedFile C = File("c.bin", 4_000);

    private static StepLedger Tree(ConflictPolicy policy, params PlannedFile[] files) =>
        new(new ExecutionPlan(
            [new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], Recursive: true, Move: false), policy, files)],
            [], [], [], []));

    private static StepOutcome Outcome(int exitCode, params PlannedFile[] completed) =>
        new(completed.Select(f => f.SourcePath).ToList(), [], new RobocopyExitCode(exitCode), null);

    [Fact]
    public void A_file_robocopy_never_mentioned_in_a_normal_run_keeps_the_job_from_ending_done()
    {
        // Robocopy exits 1 having copied a.txt and said nothing about b.txt, whose source is
        // still there and whose destination is not: it was not copied.
        var ledger = Tree(ConflictPolicy.Ask, A, B);
        ledger.StepStarted(0);
        ledger.Apply(0, new FileReported(10, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        var failed = ledger.ResolveUnreported(0, sourceExists: _ => true, destinationExists: _ => false);

        Assert.Equal([B], failed);
        Assert.Equal(1, ledger.FailedRobocopyFileCount);
        Assert.Single(ledger.Retryable);
        Assert.Equal(
            JobState.DoneWithErrors,
            JobOutcome.FinalState([Outcome(1, A)], canceled: false, planIssues: 0, unfinishedFiles: ledger.FailedRobocopyFileCount));
    }

    [Fact]
    public void A_silent_skip_under_replace_is_a_failure_too()
    {
        var ledger = Tree(ConflictPolicy.Replace, A, B);
        ledger.Apply(0, new FileReported(10, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        // Replace's flags skip nothing, so a destination that is there does not explain the silence.
        Assert.Equal([B], ledger.ResolveUnreported(0, _ => true, _ => true));
        Assert.Equal(1, ledger.FailedRobocopyFileCount);
    }

    [Fact]
    public void A_late_arrival_or_a_vanished_source_is_not_an_unfinished_file()
    {
        var ledger = Tree(ConflictPolicy.Ask, A, B, C);
        ledger.Apply(0, new FileReported(10, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(0), killedByCancel: false);

        var failed = ledger.ResolveUnreported(0, sourceExists: f => f != B, destinationExists: f => f == C);

        Assert.Empty(failed);
        Assert.Equal(0, ledger.FailedRobocopyFileCount);
        Assert.Equal(JobState.Done, JobOutcome.FinalState([Outcome(0, A)], false, 0, ledger.FailedRobocopyFileCount));
    }

    [Fact]
    public void The_error_listed_for_an_unreported_failure_names_the_file_and_carries_no_path_in_its_message()
    {
        var error = StepLedger.UnreportedFailure(B);

        Assert.Equal(B.SourcePath, error.Path);
        Assert.True(PathHeuristic.IsPathFree(error.Message));
    }

    [Fact]
    public void A_run_that_died_after_other_files_completed_names_its_in_flight_file()
    {
        // 5,000 files in real life; here a.txt completed, c.bin was being written (pre-allocated
        // at full length) and b.txt was never reached when the share dropped (exit 16).
        var ledger = Tree(ConflictPolicy.Ask, A, B, C);
        ledger.Apply(0, new FileReported(10, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);

        Assert.Equal([B, C], ledger.FailedAfterRun(0));
        ledger.RecordAbsentAfterRun(0, destinationAbsent: f => f == B);

        Assert.Equal([C], ledger.SuspectedPartials(_ => false));
        Assert.Equal(JobState.DoneWithErrors, JobOutcome.FinalState([Outcome(16, A)], false, 0, ledger.FailedRobocopyFileCount));
    }

    [Fact]
    public void A_run_that_failed_on_the_destination_folder_lists_nothing_as_possibly_incomplete()
    {
        // ERROR 5 at "Creating Destination Directory", then exit 16: nothing was written.
        var ledger = Tree(ConflictPolicy.Ask, A, B, C);
        ledger.Apply(0, new ErrorReported(5, "Creating Destination Directory", @"D:\dst\T\", "Access is denied."));
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);

        // Without the look after the run, every file counts as possibly written.
        Assert.Equal([A, B, C], ledger.SuspectedPartials(_ => false));

        ledger.RecordAbsentAfterRun(0, destinationAbsent: _ => true);

        Assert.Empty(ledger.SuspectedPartials(_ => false));
        Assert.Equal(3, ledger.FailedRobocopyFileCount);
    }

    [Fact]
    public void A_step_robocopy_never_ran_needs_no_look_and_lists_nothing()
    {
        var ledger = Tree(ConflictPolicy.Ask, A, B);
        ledger.StepStarted(0);
        ledger.StepFinished(0, exitCode: null, killedByCancel: false);

        Assert.Empty(ledger.FailedAfterRun(0));
        Assert.Empty(ledger.SuspectedPartials(_ => false));
        Assert.Equal(2, ledger.FailedRobocopyFileCount);
    }
}
