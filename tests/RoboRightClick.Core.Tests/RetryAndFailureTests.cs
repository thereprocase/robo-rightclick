using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class RetryPlannerTests
{
    private static PlannedFile File(string source, string destination) =>
        new(source, destination, new FileFacts(10, FakeDisk.BaseTime));

    private static readonly PlannedFile A = File(@"C:\src\T\a.txt", @"D:\dst\T\a.txt");
    private static readonly PlannedFile B = File(@"C:\src\T\sub\b.txt", @"D:\dst\T\sub\b.txt");
    private static readonly PlannedFile C = File(@"C:\src\T\c.txt", @"D:\dst\T\c.txt");
    private static readonly PlannedFile Loose = File(@"C:\src\loose.txt", @"D:\dst\loose.txt");
    private static readonly PlannedFile Kept = File(@"C:\src\keep.txt", @"D:\dst\keep (2).txt");

    private static ExecutionPlan Original() => new(
        [
            new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], Recursive: true, Move: true), ConflictPolicy.Ask, [A, B, C]),
            new ExecutionStep(new RobocopyStep(@"C:\src", @"D:\dst", ["loose.txt"], Recursive: false, Move: true), ConflictPolicy.Ask, [Loose]),
            new ExecutionStep(new RenameStep(@"C:\src\dir", @"C:\dst\dir"), ConflictPolicy.Ask, []),
            new ExecutionStep(new KeepBothStep(Kept.SourcePath, Kept.DestinationPath, Move: false), ConflictPolicy.Ask, [Kept]),
        ],
        [@"D:\dst\T\a.txt", @"D:\dst\keep.txt"],
        [@"D:\dst\T\link"],
        [new FileConflict(@"C:\src\x", @"D:\dst\x", A.Source, A.Source)],
        [new PlanIssue(@"C:\", PastePlanner.RootReason)]);

    [Fact]
    public void Nothing_to_repeat_means_no_retry()
    {
        Assert.Null(RetryPlanner.ForFailures(Original(), [], []));
    }

    [Fact]
    public void Failed_files_rerun_as_named_files_grouped_by_folder_pair_with_replace()
    {
        var retry = RetryPlanner.ForFailures(Original(), [(0, A), (0, B), (1, Loose), (0, C)], [])!;

        Assert.Equal(3, retry.Steps.Count);
        var steps = retry.Steps.Select(s => (RobocopyStep)s.Step).ToList();
        Assert.Equal((@"C:\src\T", @"D:\dst\T"), (steps[0].SourceDirectory, steps[0].DestinationDirectory));
        Assert.Equal(["a.txt", "c.txt"], steps[0].FileNames);
        Assert.Equal([A, C], retry.Steps[0].Files);
        Assert.Equal((@"C:\src\T\sub", @"D:\dst\T\sub"), (steps[1].SourceDirectory, steps[1].DestinationDirectory));
        Assert.Equal(["b.txt"], steps[1].FileNames);
        Assert.Equal(["loose.txt"], steps[2].FileNames);
        Assert.All(steps, s => Assert.False(s.Recursive));
        Assert.All(steps, s => Assert.True(s.Move));
        Assert.All(retry.Steps, s => Assert.Equal(ConflictPolicy.Replace, s.Policy));
        Assert.Equal(Original().PresentBeforeRun, retry.PresentBeforeRun);
        Assert.Empty(retry.LinkFolders);
        Assert.Empty(retry.Kept);
        // Refusals are not retryable; the child job does not repeat them.
        Assert.Empty(retry.Issues);
    }

    [Fact]
    public void A_copy_retry_keeps_the_copy_verb()
    {
        var copy = new ExecutionPlan(
            [new ExecutionStep(new RobocopyStep(@"C:\src", @"D:\dst", ["loose.txt"], false, Move: false), ConflictPolicy.Skip, [Loose])],
            [], [], [], []);

        var retry = RetryPlanner.ForFailures(copy, [(0, Loose)], [])!;

        Assert.False(((RobocopyStep)Assert.Single(retry.Steps).Step).Move);
    }

    [Fact]
    public void Failed_in_process_steps_repeat_unchanged_after_the_robocopy_steps()
    {
        var original = Original();

        var retry = RetryPlanner.ForFailures(original, [(1, Loose)], [3, 2, 3])!;

        Assert.Equal(3, retry.Steps.Count);
        Assert.IsType<RobocopyStep>(retry.Steps[0].Step);
        Assert.Same(original.Steps[2], retry.Steps[1]);
        Assert.Same(original.Steps[3], retry.Steps[2]);
    }

    [Fact]
    public void A_job_whose_only_failure_was_in_process_still_gets_a_retry()
    {
        var retry = RetryPlanner.ForFailures(Original(), [], [3]);

        Assert.NotNull(retry);
        Assert.IsType<KeepBothStep>(Assert.Single(retry.Steps).Step);
    }

    [Fact]
    public void Thousands_of_retried_names_are_chunked_under_the_command_line_budget()
    {
        var files = Enumerable.Range(0, 5_000)
            .Select(i => File($@"C:\src\{i:D5} {new string('n', 30)}.txt", $@"D:\dst\{i:D5} {new string('n', 30)}.txt"))
            .ToList();
        var original = new ExecutionPlan(
            [new ExecutionStep(new RobocopyStep(@"C:\src", @"D:\dst", files.Select(f => WinPath.GetFileName(f.SourcePath)).ToList(), false, true), ConflictPolicy.Ask, files)],
            [], [], [], []);

        var retry = RetryPlanner.ForFailures(original, files.Select(f => (0, f)).ToList(), [])!;

        Assert.True(retry.Steps.Count > 1);
        Assert.All(retry.Steps, s => Assert.True(
            ((RobocopyStep)s.Step).FileNames.Sum(n => RobocopyArgs.Quote(n).Length + 1) <= PastePlanner.DefaultFileListBudget));
        Assert.Equal(files, retry.Steps.SelectMany(s => s.Files));
    }

    [Fact]
    public void Only_robocopy_files_can_be_retried_by_name()
    {
        Assert.Throws<ArgumentException>(() => RetryPlanner.ForFailures(Original(), [(3, Kept)], []));
        Assert.Throws<ArgumentException>(() => RetryPlanner.ForFailures(Original(), [(9, A)], []));
        Assert.Throws<ArgumentException>(() => RetryPlanner.ForFailures(Original(), [], [0]));
        // Robocopy cannot rename, so a file planned under another name cannot be its retry.
        Assert.Throws<ArgumentException>(() => RetryPlanner.ForFailures(Original(), [(1, Loose with { DestinationPath = @"D:\dst\other.txt" })], []));
    }

    [Fact]
    public void The_ledger_and_the_retry_planner_agree_on_what_to_repeat()
    {
        var original = Original();
        var ledger = new StepLedger(original);
        ledger.Apply(0, new FileReported(10, A.SourcePath));
        ledger.Apply(0, new ErrorReported(5, "Copying File", B.SourcePath, "Access is denied."));
        ledger.StepFinished(0, new RobocopyExitCode(9), killedByCancel: false);
        ledger.StepFinished(1, null, killedByCancel: false);
        ledger.InProcessCompleted(2);
        ledger.StepFinished(2, null, killedByCancel: false);
        ledger.StepFinished(3, null, killedByCancel: false);

        var retry = RetryPlanner.ForFailures(original, ledger.Retryable, ledger.FailedInProcessSteps)!;

        // B errored and Loose never ran; C was a late arrival and stays put.
        Assert.Equal([B, Loose, Kept], retry.Steps.SelectMany(s => s.Files));
        Assert.Equal([C], ledger.SkippedLateArrivals);
    }
}

public class FailureTextTests
{
    private const string SecretPath = @"C:\secret-folder\payroll 2026.xlsx";

    private static StepOutcome Fatal(int code, string operation = "Copying File", string message = "Some message.") =>
        new([], [new ErrorReported(code, operation, SecretPath, message)], new RobocopyExitCode(16), null);

    [Theory]
    [InlineData(2, "Copying File", "The source or destination could not be found (error 2).")]
    [InlineData(3, "Scanning Source Directory", "The source or destination could not be found (error 3).")]
    [InlineData(5, "Accessing Destination Directory", "Access to the destination folder was denied (error 5).")]
    [InlineData(5, "Creating Destination Directory", "Access to the destination folder was denied (error 5).")]
    [InlineData(5, "Scanning Source Directory", "Access to the source was denied (error 5).")]
    [InlineData(5, "Copying File", "Access to a file was denied (error 5).")]
    [InlineData(32, "Copying File", "A file is in use by another program (error 32).")]
    [InlineData(53, "Accessing Source Directory", "The network path was not found (error 53).")]
    [InlineData(67, "Accessing Destination Directory", "The network path was not found (error 67).")]
    [InlineData(112, "Copying File", "There is not enough space on the destination disk (error 112).")]
    [InlineData(1314, "Copying NTFS Security to Destination File", "A required privilege is not held (error 1314).")]
    public void Common_codes_get_their_own_wording(int code, string operation, string expected) =>
        Assert.Equal(expected, FailureText.Describe(Fatal(code, operation)));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(53)]
    [InlineData(67)]
    [InlineData(112)]
    [InlineData(1314)]
    [InlineData(1392)]
    [InlineData(87)]
    public void No_description_ever_carries_a_path(int code)
    {
        // The system message itself may quote a path; it must not get through either.
        foreach (var message in new[] { "Plain message.", $"Could not open {SecretPath}.", "The file 'payroll 2026.xlsx' is locked." })
        {
            var text = FailureText.Describe(Fatal(code, message: message));

            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payroll", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\\', text);
            Assert.DoesNotContain(':', text.Replace($"Windows error {code}:", string.Empty, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Other_codes_quote_the_system_message()
    {
        Assert.Equal(
            "Windows error 1392: The file or directory is corrupted and unreadable.",
            FailureText.Describe(Fatal(1392, message: "The file or directory is corrupted and unreadable.")));
        Assert.Equal("Windows error 87: The parameter is incorrect.", FailureText.Describe(Fatal(87, message: "  The parameter is incorrect  ")));
        Assert.Equal("Windows error 1392.", FailureText.Describe(Fatal(1392, message: "")));
        Assert.Equal("Windows error 1392.", FailureText.Describe(Fatal(1392, message: $"Bad file {SecretPath}")));
    }

    [Fact]
    public void The_first_error_is_the_one_described()
    {
        var outcome = new StepOutcome(
            [],
            [new ErrorReported(112, "Copying File", SecretPath, "x"), new ErrorReported(5, "Copying File", SecretPath, "y")],
            new RobocopyExitCode(16),
            null);

        Assert.Equal("There is not enough space on the destination disk (error 112).", FailureText.Describe(outcome));
    }

    [Fact]
    public void A_fatal_exit_without_an_error_line_names_the_exit_code()
    {
        Assert.Equal(
            "Robocopy stopped with a fatal error (exit code 16).",
            FailureText.Describe(new StepOutcome([], [], new RobocopyExitCode(16), null)));
        Assert.Equal("Robocopy stopped unexpectedly.", FailureText.Describe(new StepOutcome([], [], new RobocopyExitCode(-1), null)));
        Assert.Equal(
            "Some files could not be copied (exit code 8).",
            FailureText.Describe(new StepOutcome([], [], new RobocopyExitCode(8), null)));
        Assert.Equal("The transfer did not finish.", FailureText.Describe(new StepOutcome([], [], null, null)));
    }

    [Fact]
    public void A_launch_failure_passes_through_only_when_it_names_nothing()
    {
        static string Describe(string failure) => FailureText.Describe(new StepOutcome([], [], null, failure));

        Assert.Equal("The output pipe was opened by an untrusted process.", Describe("The output pipe was opened by an untrusted process."));
        Assert.Equal("The system cannot find the file specified.", Describe("The system cannot find the file specified"));
        Assert.Equal("The transfer could not be started.", Describe($"Could not find file '{SecretPath}'."));
        Assert.Equal("The transfer could not be started.", Describe("Could not find file 'payroll.xlsx'."));
        Assert.Equal("The transfer could not be started.", Describe(@"\\server\share is offline"));
        Assert.Equal("The transfer could not be started.", Describe("D: is not ready"));
        Assert.Equal("The transfer could not be started.", Describe("  "));
        Assert.Equal("The transfer could not be started.", Describe(new string('a', 400)));
        // A failure wins over errors: it is the first thing that broke.
        Assert.Equal(
            "Robocopy could not be started.",
            FailureText.Describe(new StepOutcome([], [new ErrorReported(5, "Copying File", SecretPath, "m")], null, "Robocopy could not be started.")));
    }
}
