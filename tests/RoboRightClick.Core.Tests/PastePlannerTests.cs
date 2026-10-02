using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class PastePlannerTests
{
    private sealed class FakeFacts(params string[] existing) : IPlanningFacts
    {
        private readonly HashSet<string> _existing = new(existing, WinPath.Comparer);

        public bool Exists(string path) => _existing.Contains(WinPath.TrimTrailingSeparators(path));

        // Volume = drive letter for these tests.
        public bool SameVolume(string a, string b) => WinPath.Comparer.Equals(WinPath.GetRoot(a), WinPath.GetRoot(b));

        // These tests hand the planner resolved SourceItems; only the PasteOrder entry
        // point asks for kinds, and no path here is a link or an 8.3 alias.
        public ItemKind KindOf(string path) => throw new NotSupportedException();

        public string FinalPath(string path) => path;
    }

    private static PasteRequest Request(TransferVerb verb, string destination, params (string Path, bool Dir)[] sources) =>
        new(sources.Select(s => new SourceItem(s.Path, s.Dir)).ToList(), destination, verb);

    [Fact]
    public void Copy_files_from_one_folder_become_one_robocopy_run()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Copy, @"D:\dst", (@"C:\src\a.txt", false), (@"C:\src\b.txt", false)),
            new FakeFacts());

        var step = Assert.IsType<RobocopyStep>(Assert.Single(plan.Steps));
        Assert.Equal(@"C:\src", step.SourceDirectory);
        Assert.Equal(@"D:\dst", step.DestinationDirectory);
        Assert.Equal(["a.txt", "b.txt"], step.FileNames);
        Assert.False(step.Recursive);
        Assert.False(step.Move);
    }

    [Fact]
    public void Copy_folder_targets_same_named_folder_recursively()
    {
        var plan = PastePlanner.Plan(Request(TransferVerb.Copy, @"D:\dst", (@"C:\src\photos", true)), new FakeFacts());

        var step = Assert.IsType<RobocopyStep>(Assert.Single(plan.Steps));
        Assert.Equal(@"C:\src\photos", step.SourceDirectory);
        Assert.Equal(@"D:\dst\photos", step.DestinationDirectory);
        Assert.Empty(step.FileNames);
        Assert.True(step.Recursive);
    }

    [Fact]
    public void Same_volume_move_without_collision_is_a_rename()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Move, @"C:\dst", (@"C:\src\photos", true), (@"C:\src\a.txt", false)),
            new FakeFacts());

        Assert.Equal(
            [new RenameStep(@"C:\src\photos", @"C:\dst\photos"), new RenameStep(@"C:\src\a.txt", @"C:\dst\a.txt")],
            plan.Steps);
    }

    [Fact]
    public void Same_volume_move_onto_existing_name_merges_through_robocopy()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Move, @"C:\dst", (@"C:\src\photos", true)),
            new FakeFacts(@"C:\dst\photos"));

        var step = Assert.IsType<RobocopyStep>(Assert.Single(plan.Steps));
        Assert.True(step.Move);
        Assert.True(step.Recursive);
    }

    [Fact]
    public void Cross_volume_move_uses_robocopy_move()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Move, @"D:\dst", (@"C:\src\a.txt", false), (@"C:\src\photos", true)),
            new FakeFacts());

        Assert.All(plan.Steps, s => Assert.True(Assert.IsType<RobocopyStep>(s).Move));
        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void Pasting_a_folder_into_itself_or_below_is_rejected_and_others_continue()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Copy, @"C:\src\photos\2024",
                (@"C:\src\photos", true),
                (@"C:\src\a.txt", false)),
            new FakeFacts());

        var issue = Assert.Single(plan.Rejected);
        Assert.Equal(@"C:\src\photos", issue.Path);
        Assert.Equal(PastePlanner.SubfolderReason, issue.Reason);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void Copy_into_same_folder_creates_explorer_style_duplicates()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Copy, @"C:\src",
                (@"C:\src\report.txt", false),
                (@"C:\src\photos", true)),
            new FakeFacts(@"C:\src\report - Copy.txt"));

        Assert.Contains(new DuplicateFileStep(@"C:\src\report.txt", @"C:\src\report - Copy (2).txt"), plan.Steps);
        var folder = plan.Steps.OfType<RobocopyStep>().Single();
        Assert.Equal(@"C:\src\photos - Copy", folder.DestinationDirectory);
        Assert.True(folder.Recursive);
    }

    [Fact]
    public void Duplicates_created_by_one_paste_never_share_a_name()
    {
        // "a" and "a - Copy" pasted into their own folder: the first takes
        // "a - Copy (2)" because "a - Copy" exists, the second "a - Copy - Copy".
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Copy, @"C:\src", (@"C:\src\a", true), (@"C:\src\a - Copy", true)),
            new FakeFacts(@"C:\src\a - Copy"));

        var targets = plan.Steps.OfType<RobocopyStep>().Select(s => s.DestinationDirectory).ToList();
        Assert.Equal([@"C:\src\a - Copy (2)", @"C:\src\a - Copy - Copy"], targets);
    }

    [Fact]
    public void Move_into_same_folder_is_a_no_op()
    {
        var plan = PastePlanner.Plan(Request(TransferVerb.Move, @"C:\src\", (@"C:\src\a.txt", false)), new FakeFacts());

        Assert.Empty(plan.Steps);
        Assert.Equal(PastePlanner.SameFolderMoveReason, Assert.Single(plan.NoOps).Reason);
    }

    [Fact]
    public void Drive_roots_are_rejected()
    {
        var plan = PastePlanner.Plan(Request(TransferVerb.Copy, @"D:\dst", (@"E:\", true)), new FakeFacts());

        Assert.Equal(PastePlanner.RootReason, Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void Long_file_lists_are_chunked_under_the_budget()
    {
        var sources = Enumerable.Range(0, 1000).Select(i => ($@"C:\src\file-{i:D4}-with-a-long-name.bin", false)).ToArray();
        var plan = PastePlanner.Plan(Request(TransferVerb.Copy, @"D:\dst", sources), new FakeFacts(), fileListBudget: 4_000);

        var steps = plan.Steps.Cast<RobocopyStep>().ToList();
        Assert.True(steps.Count > 1);
        Assert.Equal(1000, steps.Sum(s => s.FileNames.Count));
        Assert.All(steps, s => Assert.True(s.FileNames.Sum(n => RobocopyArgs.Quote(n).Length + 1) <= 4_000));
        Assert.Equal(sources.Select(s => WinPath.GetFileName(s.Item1)), steps.SelectMany(s => s.FileNames));
    }

    [Fact]
    public void Files_from_different_folders_get_separate_runs_in_selection_order()
    {
        var plan = PastePlanner.Plan(
            Request(TransferVerb.Copy, @"D:\dst",
                (@"C:\one\a.txt", false),
                (@"C:\two\b.txt", false),
                (@"C:\ONE\c.txt", false)),
            new FakeFacts());

        var steps = plan.Steps.Cast<RobocopyStep>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(["a.txt", "c.txt"], steps[0].FileNames);
        Assert.Equal(["b.txt"], steps[1].FileNames);
    }
}
