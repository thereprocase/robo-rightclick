using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// What the ledger knows about each file robocopy did not finish, and what "Try again" may
/// do with it: overwrite only where this paste may have left a partial file, ask otherwise.
/// </summary>
public class RetryEvidenceTests
{
    private static PlannedFile File(string source, string destination, long size = 10) =>
        new(source, destination, new FileFacts(size, FakeDisk.BaseTime));

    private static readonly PlannedFile A = File(@"C:\src\T\a.txt", @"D:\dst\T\a.txt", size: 100);
    private static readonly PlannedFile B = File(@"C:\src\T\sub\b.txt", @"D:\dst\T\sub\b.txt", size: 20);
    private static readonly PlannedFile C = File(@"C:\src\T\sub\c.txt", @"D:\dst\T\sub\c.txt", size: 3);

    private static ExecutionPlan TreePlan(ConflictPolicy policy = ConflictPolicy.Ask, bool move = true) => new(
        [new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], Recursive: true, Move: move), policy, [A, B, C])],
        [], [], [], []);

    private static ErrorReported Error(string path, int code = 5, string operation = "Copying File") =>
        new(code, operation, path, "Access is denied.");

    private static Func<string, bool> Present(params PlannedFile[] files)
    {
        var set = new HashSet<string>(files.Select(f => WinPath.NormalizeForMatch(f.DestinationPath)), WinPath.Comparer);
        return set.Contains;
    }

    // ---------------------------------------------------------------- robocopy's own retries

    [Fact]
    public void With_retries_on_a_file_copied_after_its_error_is_completed_and_its_error_withdrawn()
    {
        var ledger = new StepLedger(TreePlan(), robocopyRetries: true);
        var error = Error(A.SourcePath, code: 32);

        var failed = ledger.Apply(0, error);
        // "Waiting 30 seconds... Retrying..." is plain output; the next attempt succeeds.
        ledger.Apply(0, new OtherOutput("Waiting 30 seconds... Retrying..."));
        var recovered = ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, new FileReported(20, B.SourcePath));
        ledger.Apply(0, new FileReported(3, C.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        Assert.Equal(error, failed.Error);
        Assert.Equal(A, recovered.Completed);
        Assert.Equal(A, recovered.Recovered);
        Assert.Empty(ledger.Retryable);
        Assert.Contains(A.SourcePath, ledger.CompletedSources, WinPath.Comparer);
        Assert.Equal(123, ledger.CompletedBytesOfFinishedSteps);
        Assert.Empty(ledger.StandingErrors(0, [error]));
    }

    [Fact]
    public void With_retries_off_a_file_line_after_its_error_never_completes_it()
    {
        var ledger = new StepLedger(TreePlan(), robocopyRetries: false);
        var error = Error(A.SourcePath);

        ledger.Apply(0, error);
        var late = ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(9), killedByCancel: false);

        Assert.Null(late.Completed);
        Assert.Null(late.Recovered);
        Assert.Equal([(0, A)], ledger.Retryable);
        Assert.Equal([error], ledger.StandingErrors(0, [error]));
    }

    [Fact]
    public void With_retries_on_a_folder_error_whose_files_all_completed_no_longer_stands()
    {
        // Retries are 3: "ERROR 64 ... Creating Destination Directory D:\dst\T\sub", a retry,
        // then both files under it copied. The paste fully succeeded.
        var ledger = new StepLedger(TreePlan(), robocopyRetries: true);
        var folderError = Error(@"D:\dst\T\sub", code: 64, operation: "Creating Destination Directory");

        ledger.Apply(0, folderError);
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, new FileReported(20, B.SourcePath));
        ledger.Apply(0, new FileReported(3, C.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        Assert.Empty(ledger.Retryable);
        Assert.Empty(ledger.StandingErrors(0, [folderError]));
        Assert.Equal([folderError], ledger.RecoveredFolderErrors(0, [folderError]));
    }

    [Fact]
    public void A_folder_error_stands_while_a_file_under_it_did_not_complete_or_when_nothing_is_under_it()
    {
        var ledger = new StepLedger(TreePlan(), robocopyRetries: true);
        var folderError = Error(@"D:\dst\T\sub", code: 64, operation: "Creating Destination Directory");
        var emptyFolderError = Error(@"D:\dst\T\empty", code: 5, operation: "Creating Destination Directory");

        ledger.Apply(0, folderError);
        ledger.Apply(0, emptyFolderError);
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, new FileReported(20, B.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(9), killedByCancel: false);

        Assert.Equal([folderError, emptyFolderError], ledger.StandingErrors(0, [folderError, emptyFolderError]));
        Assert.Empty(ledger.RecoveredFolderErrors(0, [folderError, emptyFolderError]));
    }

    [Fact]
    public void A_retry_that_fails_again_counts_one_error_per_file()
    {
        var ledger = new StepLedger(TreePlan(), robocopyRetries: true);

        Assert.NotNull(ledger.Apply(0, Error(A.SourcePath, code: 32)).Error);
        Assert.Null(ledger.Apply(0, Error(A.SourcePath, code: 32)).Error);
        Assert.Null(ledger.Apply(0, Error(A.SourcePath, code: 32)).Error);
    }

    // ---------------------------------------------------------------- unreported files

    [Fact]
    public void An_unreported_file_whose_source_vanished_is_not_called_a_late_arrival()
    {
        var ledger = new StepLedger(TreePlan());
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);
        Assert.Equal([B, C], ledger.Unreported(0));

        // B's source is gone (a build deleted it); C's destination appeared during the paste.
        ledger.ResolveUnreported(0, sourceExists: f => f != B, destinationExists: f => f == C);

        Assert.Equal([B], ledger.SourcesGone);
        Assert.Equal([C], ledger.SkippedLateArrivals);
        Assert.Empty(ledger.Retryable);
        Assert.Empty(ledger.Unreported(0));
    }

    [Fact]
    public void An_unreported_file_with_its_source_and_no_destination_failed()
    {
        var ledger = new StepLedger(TreePlan());
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, new FileReported(20, B.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        ledger.ResolveUnreported(0, sourceExists: _ => true, destinationExists: _ => false);

        Assert.Empty(ledger.SkippedLateArrivals);
        Assert.Equal([(0, C)], ledger.Retryable);
    }

    [Fact]
    public void Under_replace_an_unreported_file_is_never_a_late_arrival()
    {
        // Replace's flags overwrite whatever is there, so robocopy cannot have skipped a file
        // because its name appeared.
        var ledger = new StepLedger(TreePlan(ConflictPolicy.Replace));
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        Assert.Empty(ledger.SkippedLateArrivals);
        Assert.Equal([(0, B), (0, C)], ledger.Retryable);

        ledger.ResolveUnreported(0, sourceExists: f => f != C, destinationExists: _ => true);

        Assert.Empty(ledger.SkippedLateArrivals);
        Assert.Equal([C], ledger.SourcesGone);
        Assert.Equal([(0, B)], ledger.Retryable);
    }

    // ---------------------------------------------------------------- running totals

    [Fact]
    public void Completed_bytes_of_finished_steps_follow_every_change()
    {
        var plan = new ExecutionPlan(
            [
                new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], true, true), ConflictPolicy.Ask, [A]),
                new ExecutionStep(new RobocopyStep(@"C:\src\T\sub", @"D:\dst\T\sub", [], true, true), ConflictPolicy.Ask, [B, C]),
                new ExecutionStep(new DuplicateFileStep(@"C:\x.txt", @"C:\x - Copy.txt"), ConflictPolicy.Ask, [File(@"C:\x.txt", @"C:\x - Copy.txt", size: 7)]),
            ],
            [], [], [], []);
        var ledger = new StepLedger(plan);

        ledger.Apply(0, new FileReported(100, A.SourcePath));
        Assert.Equal(0, ledger.CompletedBytesOfFinishedSteps);
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);
        Assert.Equal(100, ledger.CompletedBytesOfFinishedSteps);

        ledger.Apply(1, new FileReported(20, B.SourcePath));
        ledger.Apply(1, new FileReported(3, C.SourcePath));
        ledger.Apply(1, Error(C.SourcePath));
        ledger.StepFinished(1, new RobocopyExitCode(9), killedByCancel: false);
        Assert.Equal(120, ledger.CompletedBytesOfFinishedSteps);

        ledger.InProcessCompleted(2);
        ledger.StepFinished(2, null, killedByCancel: false);
        Assert.Equal(127, ledger.CompletedBytesOfFinishedSteps);
    }

    // ---------------------------------------------------------------- what a retry may overwrite

    [Fact]
    public void Only_a_file_robocopy_reported_failing_whose_destination_was_free_may_be_overwritten()
    {
        var ledger = new StepLedger(TreePlan());
        // A: robocopy's own ERROR names it. B: its folder could not be created. C: never reported.
        ledger.Apply(0, Error(A.SourcePath, code: 112));
        ledger.Apply(0, Error(@"D:\dst\T\sub", code: 53, operation: "Creating Destination Directory"));
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);

        var candidates = ledger.RetryCandidates(Present());

        Assert.Equal(
            [new RetryCandidate(0, A, true), new RetryCandidate(0, B, false), new RetryCandidate(0, C, false)],
            candidates);
    }

    [Fact]
    public void A_late_arrival_in_a_run_that_died_is_never_overwritten_by_a_retry()
    {
        // The user saved report.xlsx into the destination during the run, robocopy skipped it
        // under the Skip flags, then the share dropped and robocopy exited 16.
        var ledger = new StepLedger(TreePlan());
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);

        Assert.All(ledger.RetryCandidates(Present()), c => Assert.False(c.MayOverwrite));
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask, false)]
    [InlineData(ConflictPolicy.Skip, false)]
    [InlineData(ConflictPolicy.Replace, true)]
    [InlineData(ConflictPolicy.KeepNewer, true)]
    public void A_reported_failure_over_a_file_that_was_there_before_may_be_overwritten_only_if_the_answer_was_to_overwrite(
        ConflictPolicy policy, bool mayOverwrite)
    {
        var ledger = new StepLedger(TreePlan(policy));
        ledger.Apply(0, Error(A.SourcePath, code: 112));
        ledger.StepFinished(0, new RobocopyExitCode(9), killedByCancel: false);

        var candidate = Assert.Single(ledger.RetryCandidates(Present(A)), c => c.File == A);

        Assert.Equal(mayOverwrite, candidate.MayOverwrite);
    }

    [Fact]
    public void The_retry_runs_unproven_files_under_skip_flags_and_asks_about_those_now_present()
    {
        var original = TreePlan();
        var retry = RetryPlanner.ForFailures(
            original,
            [new RetryCandidate(0, A, true), new RetryCandidate(0, B, false), new RetryCandidate(0, C, false)],
            [])!;

        Assert.Equal(
            [(ConflictPolicy.Replace, A.SourcePath), (ConflictPolicy.Ask, B.SourcePath), (ConflictPolicy.Ask, C.SourcePath)],
            retry.Steps.SelectMany(s => s.Files.Select(f => (s.Policy, f.SourcePath))).ToList());

        // Later, a newer b.txt was saved into the destination by hand; c.txt is still free.
        var disk = new FakeDisk().File(A.SourcePath).File(B.SourcePath).File(C.SourcePath)
            .File(A.DestinationPath, size: 100).File(B.DestinationPath, minute: 30);
        var rescan = RetryPlanner.Rescan(retry, disk, disk, null, CancellationToken.None);

        Assert.Equal([A.SourcePath], rescan.Fixed.Steps.SelectMany(s => s.Files).Select(f => f.SourcePath));
        Assert.Equal(B.DestinationPath, Assert.Single(rescan.Rest.Conflicts).DestinationPath);

        // Skip, as the prompt's default: b.txt is left alone, c.txt still runs under Skip flags.
        var decided = ExecutionPlanner.Apply(rescan.Rest, ConflictPolicy.Ask, new ConflictChoice.SkipAll(), _ => false);
        var combined = RetryPlanner.Combine(rescan.Fixed, decided);

        Assert.Equal(ConflictPolicy.Replace, combined.Steps[0].Policy);
        Assert.Equal([A.SourcePath], combined.Steps[0].Files.Select(f => f.SourcePath));
        Assert.DoesNotContain(combined.Steps.SelectMany(s => s.Files), f => f.SourcePath == B.SourcePath);
        Assert.All(combined.Steps.Where(s => s.Files.Any(f => f.SourcePath == C.SourcePath)), s => Assert.Equal(ConflictPolicy.Ask, s.Policy));
        Assert.Contains(B.DestinationPath, combined.PresentBeforeRun);
        Assert.Equal(B.DestinationPath, Assert.Single(combined.Kept).DestinationPath);
    }

    [Fact]
    public void A_retry_of_a_cut_whose_source_was_already_moved_reports_it_missing()
    {
        var retry = RetryPlanner.ForFailures(TreePlan(), [new RetryCandidate(0, B, false)], [])!;
        var disk = new FakeDisk().Dir(@"C:\src\T\sub").File(B.DestinationPath);

        var rescan = RetryPlanner.Rescan(retry, disk, disk, null, CancellationToken.None);

        var issue = Assert.Single(rescan.Rest.Issues);
        Assert.Equal(B.SourcePath, issue.Path);
        Assert.Equal(PastePlanner.MissingReason, issue.Reason);
        Assert.Empty(rescan.Rest.Conflicts);
    }

    [Fact]
    public void The_retry_count_is_files_of_robocopy_steps_plus_whole_in_process_steps()
    {
        var original = new ExecutionPlan(
            [
                new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], true, false), ConflictPolicy.Ask, [A, B, C]),
                new ExecutionStep(new RenameStep(@"C:\src\Folder", @"C:\dst\Folder"), ConflictPolicy.Ask, [File(@"C:\src\Folder\x", @"C:\dst\Folder\x"), File(@"C:\src\Folder\y", @"C:\dst\Folder\y")]),
            ],
            [], [], [], []);

        var retry = RetryPlanner.ForFailures(original, [new RetryCandidate(0, A, true), new RetryCandidate(0, C, false)], [1]);

        Assert.Equal(3, RetryPlanner.CountOf(retry));
        Assert.Equal(0, RetryPlanner.CountOf(null));
    }

    // ---------------------------------------------------------------- may be incomplete

    [Fact]
    public void Files_of_a_run_that_died_may_be_incomplete_but_not_those_of_a_run_that_never_started()
    {
        var plan = new ExecutionPlan(
            [
                new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], true, false), ConflictPolicy.Ask, [A]),
                new ExecutionStep(new RobocopyStep(@"C:\src\T\sub", @"D:\dst\T\sub", [], true, false), ConflictPolicy.Ask, [B, C]),
            ],
            [], [], [], []);
        var ledger = new StepLedger(plan);
        ledger.StepStarted(0);
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);
        ledger.StepStarted(1);
        // Robocopy could not be started at all: no exit code, no output.
        ledger.StepFinished(1, null, killedByCancel: false);

        Assert.Equal([A], ledger.MayBeIncomplete);
        Assert.Equal(3, ledger.Retryable.Count);
    }
}

/// <summary>What the error summary, the Jobs window and the toast say about retries and partial files.</summary>
public class RetrySummaryTextTests
{
    private static JobSnapshot Job(JobState state) => DisplayAndTrayTests.Job(state);

    [Theory]
    [InlineData(JobState.DoneWithErrors)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Canceled)]
    public void Try_again_is_offered_once(JobState state)
    {
        var job = Job(state) with { DamagedOnCancel = state == JobState.Canceled ? 1 : 0, RetryCount = 2 };
        Assert.NotNull(JobStateText.TryAgainLabel(job));

        var retried = job with { RetriedBy = Guid.NewGuid() };

        Assert.Null(JobStateText.TryAgainLabel(retried));
        Assert.Equal("RETRIED", JobStateText.Label(retried));
        Assert.Equal(StateTone.Neutral, JobStateText.Tone(retried));
        Assert.Equal("Tried again: see the newer job", JobStateText.For(retried));
        Assert.EndsWith("Tried again: see the newer job in the Jobs window.", JobStateText.SummaryHeading(retried));
    }

    [Fact]
    public void Try_again_labels_count_what_the_retry_would_repeat()
    {
        Assert.Equal("Try again (5)", JobStateText.TryAgainLabel(Job(JobState.DoneWithErrors) with { RetryCount = 5 }));
        Assert.Equal("Try again", JobStateText.TryAgainLabel(Job(JobState.Failed)));
        Assert.Equal("Finish copying them", JobStateText.TryAgainLabel(Job(JobState.Canceled) with { DamagedOnCancel = 3 }));
        Assert.Equal("Try again (1)", JobStateText.TryAgainLabel(Job(JobState.Canceled) with { MayBeIncomplete = 1, RetryCount = 1 }));
        Assert.Equal("Try again", JobStateText.TryAgainLabel(Job(JobState.DoneWithErrors) with { RetriesWholePaste = true }));
        Assert.Null(JobStateText.TryAgainLabel(Job(JobState.Done)));
    }

    [Fact]
    public void Errors_no_retry_can_repeat_offer_no_try_again()
    {
        // A link folder that could not be created: one error, nothing a retry plan would run.
        var job = Job(JobState.DoneWithErrors) with { ErrorCount = 1, RetryCount = 0 };

        Assert.Null(JobStateText.TryAgainLabel(job));
        Assert.Contains("Open Jobs for details.", ToastText.ForFinished(job, notifyOnComplete: false)!.Body);
        Assert.Contains("Open Jobs to try again.", ToastText.ForFinished(job with { RetryCount = 1 }, notifyOnComplete: false)!.Body);
    }

    [Fact]
    public void A_failed_paste_whose_robocopy_started_never_says_nothing_was_copied()
    {
        var failed = Job(JobState.Failed) with { MayBeIncomplete = 1, FailureReason = "The network path was not found (error 53)." };

        Assert.Equal("The paste into secret-dst stopped. Files at the destination may be incomplete.", JobStateText.SummaryHeading(failed));
        var toast = ToastText.ForFinished(failed, notifyOnComplete: false)!;
        Assert.DoesNotContain("Nothing was", toast.Body);
        Assert.Contains("may be incomplete", toast.Body);

        var neverRan = failed with { MayBeIncomplete = 0 };
        Assert.Equal("Nothing was copied to secret-dst.", JobStateText.SummaryHeading(neverRan));
        Assert.StartsWith("Nothing was copied to secret-dst.", ToastText.ForFinished(neverRan, notifyOnComplete: false)!.Body);
    }
}
