using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class StepLedgerTests
{
    private static PlannedFile File(string source, string destination, long size = 10) =>
        new(source, destination, new FileFacts(size, FakeDisk.BaseTime));

    private static ExecutionStep Robocopy(string source, string destination, params PlannedFile[] files) =>
        new(new RobocopyStep(source, destination, [], Recursive: true, Move: true), ConflictPolicy.Ask, files);

    private static ErrorReported Error(string path, int code = 5, string operation = "Copying File") =>
        new(code, operation, path, "Access is denied.");

    private static ExecutionPlan PlanOf(params ExecutionStep[] steps) => new(steps, [], [], [], []);

    private static readonly PlannedFile A = File(@"C:\src\T\a.txt", @"D:\dst\T\a.txt", size: 100);
    private static readonly PlannedFile B = File(@"C:\src\T\sub\b.txt", @"D:\dst\T\sub\b.txt", size: 20);
    private static readonly PlannedFile C = File(@"C:\src\T\sub\c.txt", @"D:\dst\T\sub\c.txt", size: 3);
    private static readonly PlannedFile Sibling = File(@"C:\src\T\subway\d.txt", @"D:\dst\T\subway\d.txt", size: 1);

    private static StepLedger TreeLedger() =>
        new(PlanOf(Robocopy(@"C:\src\T", @"D:\dst\T", A, B, C, Sibling)));

    [Fact]
    public void A_reported_file_completes_once()
    {
        var ledger = TreeLedger();
        ledger.StepStarted(0);

        var first = ledger.Apply(0, new FileReported(100, A.SourcePath));
        var again = ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        Assert.Equal(A, first.Completed);
        Assert.Null(again.Completed);
        Assert.Equal(100, ledger.CompletedBytesOfFinishedSteps);
        Assert.Equal([A.SourcePath], ledger.CompletedSources);
        Assert.False(ledger.PathsUnreliable);
    }

    [Theory]
    [InlineData(@"c:\SRC\t\A.TXT")]
    [InlineData(@"\\?\C:\src\T\a.txt")]
    [InlineData(@"C:\.\src\T\a.txt")]
    [InlineData(@"C:/src/T/a.txt")]
    public void Reported_paths_match_after_normalizing(string reported)
    {
        var ledger = TreeLedger();

        Assert.Equal(A, ledger.Apply(0, new FileReported(100, reported)).Completed);
        Assert.False(ledger.PathsUnreliable);
    }

    [Fact]
    public void A_root_source_reported_as_dot_matches()
    {
        var file = File(@"C:\a.txt", @"D:\dst\a.txt");
        var ledger = new StepLedger(PlanOf(new ExecutionStep(
            new RobocopyStep(@"C:\", @"D:\dst", ["a.txt"], Recursive: false, Move: false), ConflictPolicy.Ask, [file])));

        // RobocopyArgs.Quote passes a root as "C:\.", which robocopy may echo.
        Assert.Equal(file, ledger.Apply(0, new FileReported(10, @"C:\.\a.txt")).Completed);
    }

    [Fact]
    public void A_file_line_matching_nothing_in_the_current_step_makes_paths_unreliable()
    {
        var other = File(@"C:\elsewhere\x.txt", @"D:\dst\x.txt");
        var ledger = new StepLedger(PlanOf(Robocopy(@"C:\src\T", @"D:\dst\T", A), Robocopy(@"C:\elsewhere", @"D:\dst", other)));

        // Planned, but in another step: still a mismatch for this one.
        var update = ledger.Apply(0, new FileReported(10, other.SourcePath));

        Assert.Equal(new LedgerUpdate(null, null, null), update);
        Assert.True(ledger.PathsUnreliable);
    }

    /// <summary>Safety property (d): once paths are unreliable, "Try again" lists no files.</summary>
    [Fact]
    public void Unreliable_paths_empty_retryable()
    {
        var ledger = TreeLedger();
        ledger.Apply(0, Error(A.SourcePath));
        Assert.Single(ledger.Retryable);

        ledger.Apply(0, new FileReported(1, @"C:\src\T\not-planned.txt"));
        ledger.StepFinished(0, new RobocopyExitCode(16), killedByCancel: false);

        Assert.True(ledger.PathsUnreliable);
        Assert.Empty(ledger.Retryable);
    }

    /// <summary>Safety property (b): under /MT an ERROR may follow its file's line; the file is then not done.</summary>
    [Fact]
    public void An_error_after_completion_uncompletes_the_file()
    {
        var ledger = TreeLedger();
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, new FileReported(20, B.SourcePath));

        var error = Error(@"C:\SRC\T\A.TXT");
        var update = ledger.Apply(0, error);
        ledger.StepFinished(0, new RobocopyExitCode(9), killedByCancel: false);

        Assert.Equal(new LedgerUpdate(null, A, error), update);
        Assert.DoesNotContain(A.SourcePath, ledger.CompletedSources, WinPath.Comparer);
        Assert.Equal(20, ledger.CompletedBytesOfFinishedSteps);
        Assert.Contains((0, A), ledger.Retryable);
    }

    [Fact]
    public void A_file_line_after_its_error_does_not_complete_it()
    {
        var ledger = TreeLedger();

        var error = ledger.Apply(0, Error(A.SourcePath));
        var late = ledger.Apply(0, new FileReported(100, A.SourcePath));

        Assert.Null(error.Uncompleted);
        Assert.Null(late.Completed);
        Assert.Empty(ledger.CompletedSources);
    }

    [Fact]
    public void An_error_naming_a_destination_file_fails_that_file()
    {
        var ledger = TreeLedger();
        ledger.Apply(0, new FileReported(20, B.SourcePath));

        var update = ledger.Apply(0, Error(B.DestinationPath, operation: "Changing File Attributes"));

        Assert.Equal(B, update.Uncompleted);
    }

    [Theory]
    [InlineData(@"C:\src\T\sub", "Scanning Source Directory")]
    [InlineData(@"D:\dst\T\sub", "Creating Destination Directory")]
    [InlineData(@"d:\DST\t\SUB\", "Accessing Destination Directory")]
    public void A_folder_error_fails_every_unfinished_file_below_it_on_either_side(string folder, string operation)
    {
        var ledger = TreeLedger();
        ledger.Apply(0, new FileReported(20, B.SourcePath));

        var error = Error(folder, code: 112, operation: operation);
        var update = ledger.Apply(0, error);
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);

        // B had completed and stays completed; C failed; "subway" only shares a prefix.
        Assert.Equal(new LedgerUpdate(null, null, error), update);
        Assert.Equal([(0, C)], ledger.Retryable);
        Assert.Contains(B.SourcePath, ledger.CompletedSources, WinPath.Comparer);
        Assert.Equal([A, Sibling], ledger.SkippedLateArrivals);
    }

    [Fact]
    public void A_folder_error_on_a_drive_root_covers_the_whole_drive()
    {
        var ledger = TreeLedger();

        ledger.Apply(0, Error(@"D:\", code: 3, operation: "Accessing Destination Directory"));

        Assert.Equal(4, ledger.Retryable.Count);
    }

    [Fact]
    public void Folder_errors_find_their_files_in_large_non_ascii_trees()
    {
        var files = new List<PlannedFile>();
        for (var d = 0; d < 300; d++)
        {
            for (var f = 0; f < 20; f++)
            {
                var name = d % 2 == 0 ? $@"Ördner{d}\Datei{f}.txt" : $@"ordner{d}\file{f}.txt";
                files.Add(File(@"C:\src\T\" + name, @"D:\dst\T\" + name));
            }
        }
        var ledger = new StepLedger(PlanOf(Robocopy(@"C:\src\T", @"D:\dst\T", [.. files])));

        // Different case from the plan, and only every third folder.
        for (var d = 0; d < 300; d += 3)
        {
            ledger.Apply(0, Error(d % 2 == 0 ? $@"D:\DST\T\ÖRDNER{d}" : $@"D:\DST\T\ORDNER{d}", 112, "Creating Destination Directory"));
        }

        Assert.Equal(100 * 20, ledger.Retryable.Count);
        Assert.All(ledger.Retryable, r =>
        {
            var folder = WinPath.GetFileName(WinPath.GetParent(r.File.SourcePath));
            Assert.Equal(0, int.Parse(folder.TrimStart('Ö', 'O', 'r', 'd', 'n', 'e', 'o')) % 3);
        });
    }

    /// <summary>Safety property (c): a file robocopy skipped because its name appeared is reported, never retried.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(15)]
    public void Late_arrivals_are_never_retryable(int exitCode)
    {
        var ledger = TreeLedger();
        ledger.StepStarted(0);
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(0, Error(B.SourcePath));

        ledger.StepFinished(0, new RobocopyExitCode(exitCode), killedByCancel: false);

        Assert.Equal([C, Sibling], ledger.SkippedLateArrivals);
        Assert.Equal([(0, B)], ledger.Retryable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(25)]
    public void Files_left_by_a_run_that_died_failed_or_never_ran_are_retryable(int? exitCode)
    {
        var ledger = TreeLedger();
        ledger.Apply(0, new FileReported(100, A.SourcePath));

        ledger.StepFinished(0, exitCode is { } code ? new RobocopyExitCode(code) : null, killedByCancel: false);

        Assert.Equal([(0, B), (0, C), (0, Sibling)], ledger.Retryable);
        Assert.Empty(ledger.SkippedLateArrivals);
    }

    [Fact]
    public void Files_in_flight_at_cancel_go_to_cleanup_and_never_to_retry()
    {
        var ledger = TreeLedger();
        ledger.StepStarted(0);
        ledger.Apply(0, new FileReported(100, A.SourcePath));

        ledger.StepFinished(0, new RobocopyExitCode(-1), killedByCancel: true);

        Assert.Empty(ledger.Retryable);
        Assert.Empty(ledger.SkippedLateArrivals);
        Assert.Equal([A, B, C, Sibling], ledger.StartedRobocopyFiles);
        Assert.Equal([A.SourcePath], ledger.CompletedSources);
    }

    [Fact]
    public void Only_robocopy_steps_that_started_are_cleanup_candidates()
    {
        var copy = File(@"C:\src\x.txt", @"C:\src\x - Copy.txt");
        var ledger = new StepLedger(PlanOf(
            Robocopy(@"C:\src\T", @"D:\dst\T", A),
            new ExecutionStep(new DuplicateFileStep(copy.SourcePath, copy.DestinationPath), ConflictPolicy.Ask, [copy]),
            Robocopy(@"C:\src\U", @"D:\dst\U", B)));

        ledger.StepStarted(0);
        ledger.StepStarted(1);

        Assert.Equal([A], ledger.StartedRobocopyFiles);
    }

    [Fact]
    public void Output_from_a_run_counts_as_the_run_having_started()
    {
        var ledger = TreeLedger();

        ledger.Apply(0, new OtherOutput("ERROR: RETRY LIMIT EXCEEDED."));

        Assert.Equal(4, ledger.StartedRobocopyFiles.Count);
    }

    [Fact]
    public void Completed_bytes_count_only_finished_steps()
    {
        var ledger = new StepLedger(PlanOf(Robocopy(@"C:\src\T", @"D:\dst\T", A), Robocopy(@"C:\src\T", @"D:\dst\T", B)));
        ledger.Apply(0, new FileReported(100, A.SourcePath));
        ledger.Apply(1, new FileReported(20, B.SourcePath));

        Assert.Equal(0, ledger.CompletedBytesOfFinishedSteps);
        ledger.StepFinished(0, new RobocopyExitCode(1), killedByCancel: false);
        Assert.Equal(100, ledger.CompletedBytesOfFinishedSteps);
        ledger.StepFinished(1, new RobocopyExitCode(1), killedByCancel: false);
        Assert.Equal(120, ledger.CompletedBytesOfFinishedSteps);
    }

    [Fact]
    public void In_process_steps_are_failed_whole_and_never_retried_file_by_file()
    {
        var copy = File(@"C:\src\x.txt", @"C:\src\x - Copy.txt");
        var keep = File(@"C:\src\y.txt", @"D:\dst\y (2).txt");
        var renamed = File(@"C:\src\z.txt", @"C:\other\z.txt");
        var ledger = new StepLedger(PlanOf(
            new ExecutionStep(new DuplicateFileStep(copy.SourcePath, copy.DestinationPath), ConflictPolicy.Ask, [copy]),
            new ExecutionStep(new KeepBothStep(keep.SourcePath, keep.DestinationPath, Move: false), ConflictPolicy.Ask, [keep]),
            new ExecutionStep(new RenameStep(@"C:\src\folder", @"C:\other\folder"), ConflictPolicy.Ask, []),
            new ExecutionStep(new RenameStep(renamed.SourcePath, renamed.DestinationPath), ConflictPolicy.Ask, []),
            new ExecutionStep(new RenameStep(@"C:\src\w", @"C:\other\w"), ConflictPolicy.Ask, [])));

        ledger.InProcessCompleted(0);
        ledger.StepFinished(0, null, killedByCancel: false);
        ledger.StepFinished(1, null, killedByCancel: false);
        ledger.StepFinished(2, null, killedByCancel: false);
        ledger.InProcessReplannedAsMove(3);
        ledger.StepFinished(3, null, killedByCancel: false);
        ledger.StepFinished(4, null, killedByCancel: true);

        Assert.Equal([1, 2], ledger.FailedInProcessSteps);
        Assert.Empty(ledger.Retryable);
        Assert.Equal([copy.SourcePath], ledger.CompletedSources);
        Assert.Equal(10, ledger.CompletedBytesOfFinishedSteps);
    }

    [Fact]
    public void A_refused_keep_both_move_is_neither_failed_nor_retried()
    {
        // A cross-volume keep-both cut cannot be moved under the new name; it is a refusal,
        // and repeating it would only be refused again.
        var keep = File(@"C:\src\y.txt", @"D:\dst\y (2).txt");
        var ledger = new StepLedger(PlanOf(
            new ExecutionStep(new KeepBothStep(keep.SourcePath, keep.DestinationPath, Move: true), ConflictPolicy.Ask, [keep])));

        ledger.InProcessRefused(0);
        ledger.StepFinished(0, null, killedByCancel: false);

        Assert.Empty(ledger.FailedInProcessSteps);
        Assert.Empty(ledger.Retryable);
        Assert.Empty(ledger.CompletedSources);
    }

    [Fact]
    public void A_step_index_outside_the_plan_is_refused()
    {
        var ledger = TreeLedger();

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Apply(1, new OtherOutput("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.StepFinished(-1, null, false));
    }
}
