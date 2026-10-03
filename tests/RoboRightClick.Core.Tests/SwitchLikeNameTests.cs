using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// A file name robocopy would read as a switch must never become a file filter, on any path
/// that turns names into a robocopy step: a split tree, a retry, and the robocopy move that
/// replaces a rename. Each refuses the file with <see cref="PastePlanner.SwitchLikeNameReason"/>.
/// </summary>
public class SwitchLikeNameTests
{
    private const string Pipe = "RoboRightClick-test";

    private static readonly Func<string, bool> NothingTaken = _ => false;

    private static ScanResult Scan(FakeDisk disk, params PlanStep[] steps) =>
        JobScanner.Scan(new PastePlan(steps, [], []), disk, disk, null, CancellationToken.None);

    private static void AssertEveryStepBuilds(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps.Select(s => s.Step).OfType<RobocopyStep>())
        {
            Assert.DoesNotContain(step.FileNames, RobocopyArgs.IsSwitchLikeName);
            RobocopyArgs.Build(step, Settings.Default, ConflictPolicy.Ask, Pipe);
        }
    }

    [Theory]
    [InlineData(FileDecision.Skip, true)]
    [InlineData(FileDecision.KeepBoth, false)]
    [InlineData(FileDecision.KeepBoth, true)]
    public void A_split_tree_refuses_a_switch_like_name_in_a_folder_it_names_file_by_file(FileDecision decision, bool move)
    {
        // Notes holds a conflict the user keeps (or keeps both), so Notes runs as named batches
        // of its own files, and "-E" would turn on /E for that batch.
        var disk = new FakeDisk()
            .File(@"C:\src\Notes\todo.txt")
            .File(@"C:\src\Notes\-E")
            .File(@"C:\src\Notes\plain.txt")
            .File(@"C:\src\Notes\sub\-deep.txt")
            .File(@"C:\dst\Notes\todo.txt");
        var scan = Scan(disk, new RobocopyStep(@"C:\src\Notes", @"C:\dst\Notes", [], Recursive: true, Move: move));
        var choice = new ConflictChoice.DecideEach(new Dictionary<string, FileDecision> { [@"C:\dst\Notes\todo.txt"] = decision });

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, NothingTaken);

        AssertEveryStepBuilds(plan);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(@"C:\src\Notes\-E", issue.Path);
        Assert.Equal(PastePlanner.SwitchLikeNameReason, issue.Reason);
        Assert.DoesNotContain(plan.Steps.SelectMany(s => s.Files), f => f.SourcePath == @"C:\src\Notes\-E");
        // The subfolder is not split: robocopy copies "-deep.txt" in a whole-tree run that names nothing.
        Assert.Contains(plan.Steps.SelectMany(s => s.Files), f => f.SourcePath == @"C:\src\Notes\sub\-deep.txt");
        Assert.Contains(plan.Steps.SelectMany(s => s.Files), f => f.SourcePath == @"C:\src\Notes\plain.txt");
    }

    [Fact]
    public void A_retry_refuses_a_switch_like_name_it_would_have_to_name()
    {
        static PlannedFile File(string source, string destination) => new(source, destination, new FileFacts(10, FakeDisk.BaseTime));
        var dash = File(@"C:\src\T\-x.txt", @"D:\dst\T\-x.txt");
        var plain = File(@"C:\src\T\y.txt", @"D:\dst\T\y.txt");
        var original = new ExecutionPlan(
            [new ExecutionStep(new RobocopyStep(@"C:\src\T", @"D:\dst\T", [], Recursive: true, Move: true), ConflictPolicy.Ask, [dash, plain])],
            [], [], [], []);

        var retry = RetryPlanner.ForFailures(original, [(0, dash), (0, plain)], [])!;

        AssertEveryStepBuilds(retry);
        Assert.Equal(["y.txt"], Assert.Single(retry.Steps.Select(s => s.Step).OfType<RobocopyStep>()).FileNames);
        var issue = Assert.Single(retry.Issues);
        Assert.Equal(dash.SourcePath, issue.Path);
        Assert.Equal(PastePlanner.SwitchLikeNameReason, issue.Reason);
    }

    [Fact]
    public void A_rename_replanned_as_a_robocopy_move_refuses_a_switch_like_name()
    {
        // Same-volume says yes (two shares of one NAS), the rename fails with NOT_SAME_DEVICE,
        // and the replacement move would hand "-draft.docx" to robocopy as a filter.
        var disk = new FakeDisk().File(@"C:\src\-draft.docx").File(@"C:\src\ok.docx").Dir(@"C:\dst");
        var renames = new[]
        {
            new RenameStep(@"C:\src\-draft.docx", @"C:\dst\-draft.docx"),
            new RenameStep(@"C:\src\ok.docx", @"C:\dst\ok.docx"),
        };

        var plan = ReplacementMovePlanner.Plan(renames, disk, disk, NothingTaken, CancellationToken.None);

        AssertEveryStepBuilds(plan);
        Assert.Equal(["ok.docx"], Assert.Single(plan.Steps.Select(s => s.Step).OfType<RobocopyStep>()).FileNames);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(@"C:\src\-draft.docx", issue.Path);
        Assert.Equal(PastePlanner.SwitchLikeNameReason, issue.Reason);
    }

    [Fact]
    public void A_batch_step_handed_a_switch_like_name_drops_it()
    {
        // The paste planner never builds such a batch; any other caller of the execution
        // planner (the replacement moves, for one) gets the same refusal.
        var disk = new FakeDisk().File(@"C:\src\-S").File(@"C:\src\ok.txt").Dir(@"D:\dst");
        var scan = Scan(disk, new RobocopyStep(@"C:\src", @"D:\dst", ["-S", "ok.txt"], Recursive: false, Move: true));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, null, NothingTaken);

        AssertEveryStepBuilds(plan);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(["ok.txt"], ((RobocopyStep)step.Step).FileNames);
        Assert.Equal(["ok.txt"], step.Files.Select(f => WinPath.GetFileName(f.SourcePath)));
        Assert.Equal(PastePlanner.SwitchLikeNameReason, Assert.Single(plan.Issues).Reason);
    }

    [Fact]
    public void A_name_that_cannot_be_passed_fails_its_step_instead_of_throwing()
    {
        var step = new RobocopyStep(@"C:\src", @"D:\dst", ["ok.txt", "-E"], Recursive: false, Move: true);

        Assert.Null(RobocopyArgs.TryBuild(step, Settings.Default, ConflictPolicy.Ask, Pipe));
        Assert.NotNull(RobocopyArgs.TryBuild(step with { FileNames = ["ok.txt"] }, Settings.Default, ConflictPolicy.Ask, Pipe));
        var outcome = new StepOutcome([], [], null, RobocopyArgs.UnsafeStepFailure);
        Assert.Equal(RobocopyArgs.UnsafeStepFailure, FailureText.Describe(outcome));
    }
}
