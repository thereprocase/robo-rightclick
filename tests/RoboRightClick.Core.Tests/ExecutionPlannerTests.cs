using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class ExecutionPlannerTests
{
    private static readonly Func<string, bool> NothingTaken = _ => false;

    private static RobocopyStep Tree(string source, string destination, bool move) =>
        new(source, destination, [], Recursive: true, Move: move);

    private static RobocopyStep Batch(string source, string destination, bool move, params string[] names) =>
        new(source, destination, names, Recursive: false, Move: move);

    private static ScanResult Scan(FakeDisk disk, params PlanStep[] steps) =>
        JobScanner.Scan(new PastePlan(steps, [], []), disk, disk, null, CancellationToken.None);

    private static List<RobocopyStep> RobocopySteps(ExecutionPlan plan) =>
        plan.Steps.Select(s => s.Step).OfType<RobocopyStep>().ToList();

    private static ConflictChoice.DecideEach Decide(params (string Destination, FileDecision Decision)[] decisions) =>
        new(decisions.ToDictionary(d => d.Destination, d => d.Decision));

    [Fact]
    public void An_empty_plan_is_a_no_op_unless_something_was_refused()
    {
        var empty = new ScanResult(new PastePlan([], [], []), [], [], []);
        var refused = empty with { Issues = [new PlanIssue(@"C:\", PastePlanner.RootReason)] };

        var plan = ExecutionPlanner.Apply(empty, ConflictPolicy.Ask, null, NothingTaken);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Steps);
        Assert.False(ExecutionPlanner.Apply(refused, ConflictPolicy.Ask, null, NothingTaken).IsNoOp);
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Replace)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.KeepNewer)]
    public void Without_conflicts_the_steps_run_unchanged_with_policy_ask(ConflictPolicy configured)
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"C:\src\T\b.txt").Dir(@"D:\dst");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move: true), Batch(@"C:\src", @"D:\dst", true, "a.txt"));

        var plan = ExecutionPlanner.Apply(scan, configured, null, NothingTaken);

        Assert.Equal(scan.Plan.Steps, plan.Steps.Select(s => s.Step));
        Assert.All(plan.Steps, s => Assert.Equal(ConflictPolicy.Ask, s.Policy));
        Assert.Equal(scan.Steps.Select(s => s.Files), plan.Steps.Select(s => s.Files));
        Assert.Empty(plan.PresentBeforeRun);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void A_cut_whose_every_conflict_is_skipped_plans_no_step()
    {
        // The job then goes from AwaitingDecision straight to Finalizing, which the state
        // table must allow (a skipped conflict is the most common answer).
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"D:\dst\a.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", true, "a.txt"));
        Assert.Single(scan.Conflicts);

        foreach (var choice in new ConflictChoice[]
                 {
                     new ConflictChoice.SkipAll(),
                     Decide((@"D:\dst\a.txt", FileDecision.Skip)),
                     Decide(),
                     // Keep both is not allowed across drives for a cut, so it is a Skip.
                     Decide((@"D:\dst\a.txt", FileDecision.KeepBoth)),
                 })
        {
            var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, NothingTaken);

            Assert.Empty(plan.Steps);
            Assert.Equal(@"D:\dst\a.txt", Assert.Single(plan.Kept).DestinationPath);
            Assert.True(JobStates.CanTransition(JobState.AwaitingDecision, JobState.Finalizing));
        }
    }

    [Fact]
    public void Ask_with_conflicts_needs_a_choice()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"D:\dst\a.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", false, "a.txt"));

        Assert.Throws<InvalidOperationException>(() => ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, null, NothingTaken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replace_everywhere_keeps_the_steps_with_policy_replace(bool move)
    {
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").File(@"C:\src\T\s\b.txt").File(@"D:\dst\T\s\b.txt");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move));

        foreach (var (configured, choice) in new (ConflictPolicy, ConflictChoice?)[]
                 {
                     (ConflictPolicy.Replace, null),
                     (ConflictPolicy.Ask, new ConflictChoice.ReplaceAll()),
                     (ConflictPolicy.Ask, Decide((@"D:\dst\T\s\b.txt", FileDecision.Replace))),
                 })
        {
            var plan = ExecutionPlanner.Apply(scan, configured, choice, NothingTaken);

            var step = Assert.Single(plan.Steps);
            Assert.Same(scan.Plan.Steps[0], step.Step);
            Assert.Equal(ConflictPolicy.Replace, step.Policy);
            Assert.Equal(2, step.Files.Count);
            Assert.Empty(plan.Kept);
            Assert.Equal([@"D:\dst\T\s\b.txt"], plan.PresentBeforeRun);
        }
    }

    [Fact]
    public void A_copy_that_skips_every_conflict_keeps_its_steps_and_leaves_kept_files_out_of_their_lists()
    {
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").File(@"C:\src\T\s\b.txt").File(@"D:\dst\T\s\b.txt");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move: false));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, new ConflictChoice.SkipAll(), NothingTaken);

        var step = Assert.Single(plan.Steps);
        Assert.Same(scan.Plan.Steps[0], step.Step);
        Assert.Equal(ConflictPolicy.Skip, step.Policy);
        Assert.Equal([@"C:\src\T\a.txt"], step.Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\T\s\b.txt"], plan.Kept.Select(c => c.SourcePath));
    }

    [Fact]
    public void A_keep_newer_copy_keeps_its_steps_and_keeps_the_files_that_are_not_newer()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\new.txt", minute: 9).File(@"D:\dst\new.txt", minute: 1)
            .File(@"C:\src\old.txt", minute: 1).File(@"D:\dst\old.txt", minute: 9)
            .File(@"C:\src\same.txt", minute: 5).File(@"D:\dst\same.txt", minute: 5);
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", false, "new.txt", "old.txt", "same.txt"));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.KeepNewer, null, NothingTaken);

        var step = Assert.Single(plan.Steps);
        Assert.Same(scan.Plan.Steps[0], step.Step);
        Assert.Equal(ConflictPolicy.KeepNewer, step.Policy);
        Assert.Equal([@"C:\src\new.txt"], step.Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\old.txt", @"C:\src\same.txt"], plan.Kept.Select(c => c.SourcePath));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.KeepNewer)]
    public void A_cut_that_keeps_files_drops_their_names_instead_of_filtering(ConflictPolicy configured)
    {
        var disk = new FakeDisk()
            .File(@"C:\src\keep.txt", minute: 1).File(@"D:\dst\keep.txt", minute: 9)
            .File(@"C:\src\free.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", true, "keep.txt", "free.txt"));

        var plan = ExecutionPlanner.Apply(scan, configured, null, NothingTaken);

        var step = Assert.Single(plan.Steps);
        Assert.True(((RobocopyStep)step.Step).Move);
        Assert.Equal(["free.txt"], ((RobocopyStep)step.Step).FileNames);
        Assert.Equal(ConflictPolicy.Ask, step.Policy);
        Assert.Equal([@"C:\src\keep.txt"], plan.Kept.Select(c => c.SourcePath));
    }

    [Fact]
    public void Deciding_each_file_splits_a_batch_into_replace_and_ask_and_drops_the_rest()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\r.txt").File(@"D:\dst\r.txt")
            .File(@"C:\src\s.txt").File(@"D:\dst\s.txt")
            .File(@"C:\src\unanswered.txt").File(@"D:\dst\unanswered.txt")
            .File(@"C:\src\free.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", false, "r.txt", "s.txt", "unanswered.txt", "free.txt"));
        // Keys compare the way NTFS does.
        var choice = Decide((@"d:\DST\R.TXT", FileDecision.Replace), (@"D:\dst\s.txt", FileDecision.Skip));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, NothingTaken);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(["r.txt"], ((RobocopyStep)plan.Steps[0].Step).FileNames);
        Assert.Equal(ConflictPolicy.Replace, plan.Steps[0].Policy);
        Assert.Equal(["free.txt"], ((RobocopyStep)plan.Steps[1].Step).FileNames);
        Assert.Equal(ConflictPolicy.Ask, plan.Steps[1].Policy);
        Assert.Equal([@"C:\src\s.txt", @"C:\src\unanswered.txt"], plan.Kept.Select(c => c.SourcePath));
        Assert.Equal(3, plan.PresentBeforeRun.Count);
    }

    [Fact]
    public void A_tree_is_cut_along_the_folders_that_hold_kept_files()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\kept.txt").File(@"D:\dst\T\kept.txt")
            .File(@"C:\src\T\b.txt")
            .File(@"C:\src\T\one\x.txt").File(@"D:\dst\T\one\x.txt")
            .File(@"C:\src\T\two\deep\kept2.txt").File(@"D:\dst\T\two\deep\kept2.txt")
            .File(@"C:\src\T\two\deep\y.txt")
            .File(@"C:\src\T\two\deep\leaf\w.txt")
            .File(@"C:\src\T\two\z.txt")
            .Dir(@"C:\src\T\empty")
            .Link(@"C:\src\T\two\lnk");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move: true));
        var choice = Decide((@"D:\dst\T\one\x.txt", FileDecision.Replace));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, NothingTaken);

        var expected = new (RobocopyStep Step, ConflictPolicy Policy)[]
        {
            (Batch(@"C:\src\T", @"D:\dst\T", true, "b.txt"), ConflictPolicy.Ask),
            (Tree(@"C:\src\T\one", @"D:\dst\T\one", true), ConflictPolicy.Replace),
            (Batch(@"C:\src\T\two", @"D:\dst\T\two", true, "z.txt"), ConflictPolicy.Ask),
            (Batch(@"C:\src\T\two\deep", @"D:\dst\T\two\deep", true, "y.txt"), ConflictPolicy.Ask),
            (Tree(@"C:\src\T\two\deep\leaf", @"D:\dst\T\two\deep\leaf", true), ConflictPolicy.Ask),
            // An empty folder still gets its own step, so the cut does not lose it.
            (Tree(@"C:\src\T\empty", @"D:\dst\T\empty", true), ConflictPolicy.Ask),
        };
        Assert.Equal(expected.Length, plan.Steps.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = (RobocopyStep)plan.Steps[i].Step;
            Assert.Equal((expected[i].Step.SourceDirectory, expected[i].Step.DestinationDirectory), (actual.SourceDirectory, actual.DestinationDirectory));
            Assert.Equal(expected[i].Step.FileNames, actual.FileNames);
            Assert.Equal(expected[i].Step.Recursive, actual.Recursive);
            Assert.True(actual.Move);
            Assert.Equal(expected[i].Policy, plan.Steps[i].Policy);
        }
        Assert.Equal([@"C:\src\T\one\x.txt"], plan.Steps[1].Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\T\two\deep\leaf\w.txt"], plan.Steps[4].Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\T\kept.txt", @"C:\src\T\two\deep\kept2.txt"], plan.Kept.Select(k => k.SourcePath));
        Assert.Equal([@"D:\dst\T\two\lnk"], plan.LinkFolders);
    }

    [Fact]
    public void Items_the_scan_refused_are_cut_out_even_when_every_conflict_is_replaced()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\ok.txt")
            .File(@"C:\src\T\sub\blocked.txt").Dir(@"D:\dst\T\sub\blocked.txt")
            .File(@"C:\src\T\sub\fine.txt")
            .File(@"C:\src\T\folder\inner.txt").File(@"D:\dst\T\folder")
            .File(@"C:\src\T\r.txt").File(@"D:\dst\T\r.txt");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move: true));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Replace, null, NothingTaken);

        // Neither refused item is reachable: "sub" is split, "folder" gets no step at all.
        Assert.DoesNotContain(RobocopySteps(plan), s => s.Recursive && WinPath.AreSame(s.SourceDirectory, @"C:\src\T"));
        Assert.DoesNotContain(RobocopySteps(plan), s => WinPath.AreSame(s.SourceDirectory, @"C:\src\T\folder"));
        var sub = Assert.Single(RobocopySteps(plan), s => WinPath.AreSame(s.SourceDirectory, @"C:\src\T\sub"));
        Assert.Equal(["fine.txt"], sub.FileNames);
        Assert.Equal(scan.Issues, plan.Issues);
        Assert.Equal(3, plan.TotalFiles);
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Replace)]
    public void A_step_the_scan_refused_whole_does_not_run(ConflictPolicy configured)
    {
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").File(@"D:\dst\T").Dir(@"C:\src\folder");
        var scan = Scan(
            disk,
            Tree(@"C:\src\T", @"D:\dst\T", move: true),
            new DuplicateFileStep(@"C:\src\gone.txt", @"C:\src\gone - Copy.txt"),
            new RenameStep(@"C:\src\folder", @"C:\other\folder"));

        var plan = ExecutionPlanner.Apply(scan, configured, null, NothingTaken);

        Assert.IsType<RenameStep>(Assert.Single(plan.Steps).Step);
        Assert.Equal(2, plan.Issues.Count);
    }

    [Fact]
    public void A_batch_never_runs_with_an_empty_name_list()
    {
        // Every name is gone or blocked; robocopy without names would copy the whole folder.
        var disk = new FakeDisk().File(@"C:\src\blocked.txt").Dir(@"D:\dst\blocked.txt").File(@"C:\src\other.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", true, "missing.txt", "blocked.txt"));

        foreach (var configured in new[] { ConflictPolicy.Ask, ConflictPolicy.Replace })
        {
            var plan = ExecutionPlanner.Apply(scan, configured, null, NothingTaken);

            Assert.Empty(plan.Steps);
            Assert.Equal(2, plan.Issues.Count);
            Assert.False(plan.IsNoOp);
        }
    }

    [Fact]
    public void A_batch_with_a_dropped_name_runs_with_the_remaining_names()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"C:\src\c.txt").Dir(@"D:\dst");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", false, "a.txt", "missing.txt", "c.txt"));

        var step = Assert.Single(ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, null, NothingTaken).Steps);

        Assert.Equal(["a.txt", "c.txt"], ((RobocopyStep)step.Step).FileNames);
    }

    [Fact]
    public void Split_batches_are_chunked_like_the_paste_planner_and_lose_no_name()
    {
        var disk = new FakeDisk().Dir(@"D:\dst");
        var names = new List<string>();
        for (var i = 0; i < 3_000; i++)
        {
            var name = $"{i:D4} {new string('x', 40)} ñ.txt";
            names.Add(name);
            disk.File(WinPath.Combine(@"C:\src", name));
            if (i % 3 == 0)
            {
                disk.File(WinPath.Combine(@"D:\dst", name));
            }
        }
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", true, [.. names]));
        var choice = new ConflictChoice.DecideEach(
            scan.Conflicts.Select((c, i) => (c.DestinationPath, i % 2 == 0 ? FileDecision.Replace : FileDecision.Skip))
                .ToDictionary(p => p.DestinationPath, p => p.Item2));
        const int budget = 4_000;

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, NothingTaken, budget);

        var steps = RobocopySteps(plan);
        Assert.True(steps.Count > 10);
        Assert.All(steps, s => Assert.True(s.FileNames.Sum(n => RobocopyArgs.Quote(n).Length + 1) <= budget));
        Assert.All(plan.Steps, s => Assert.Equal(((RobocopyStep)s.Step).FileNames, s.Files.Select(f => WinPath.GetFileName(f.SourcePath))));
        var written = steps.SelectMany(s => s.FileNames).ToList();
        Assert.Equal(written.Count, written.Distinct().Count());
        Assert.Equal(names.Count - plan.Kept.Count, written.Count);
        Assert.Empty(written.Intersect(plan.Kept.Select(k => WinPath.GetFileName(k.SourcePath))));
    }

    [Fact]
    public void Keep_both_files_get_free_names_and_run_last()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\report.txt", size: 4).File(@"D:\dst\report.txt")
            .File(@"C:\src\report (3).txt")
            .File(@"C:\src\T\report.txt").File(@"D:\dst\T\report.txt");
        var scan = Scan(disk, Tree(@"C:\src\T", @"D:\dst\T", move: false), Batch(@"C:\src", @"D:\dst", false, "report.txt", "report (3).txt"));
        var choice = Decide((@"D:\dst\report.txt", FileDecision.KeepBoth), (@"D:\dst\T\report.txt", FileDecision.KeepBoth));
        // "report (2).txt" exists on disk; "report (3).txt" is about to be written by this plan.
        Func<string, bool> taken = path => WinPath.AreSame(path, @"D:\dst\report (2).txt");

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, choice, taken);

        var keepBoth = plan.Steps.TakeLast(2).ToList();
        Assert.All(plan.Steps.SkipLast(2), s => Assert.IsType<RobocopyStep>(s.Step));
        Assert.Equal(new KeepBothStep(@"C:\src\T\report.txt", @"D:\dst\T\report (2).txt", Move: false), keepBoth[0].Step);
        Assert.Equal(new KeepBothStep(@"C:\src\report.txt", @"D:\dst\report (4).txt", Move: false), keepBoth[1].Step);
        Assert.Equal(
            [new PlannedFile(@"C:\src\report.txt", @"D:\dst\report (4).txt", new FileFacts(4, FakeDisk.BaseTime))],
            keepBoth[1].Files);
        Assert.Empty(plan.Kept);
        // Both destinations existed before the run and stay protected from cancel cleanup.
        Assert.Equal([@"D:\dst\T\report.txt", @"D:\dst\report.txt"], plan.PresentBeforeRun);
        Assert.DoesNotContain(RobocopySteps(plan), s => s.FileNames.Contains("report.txt") || s.Recursive);
    }

    [Fact]
    public void Two_keep_both_files_into_one_folder_never_pick_the_same_name()
    {
        var disk = new FakeDisk()
            .File(@"C:\a\x.txt").File(@"C:\b\x.txt").File(@"D:\dst\x.txt");
        var scan = Scan(disk, Batch(@"C:\a", @"D:\dst", false, "x.txt"), Batch(@"C:\b", @"D:\dst", false, "x.txt"));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, Decide((@"D:\dst\x.txt", FileDecision.KeepBoth)), NothingTaken);

        Assert.Equal(
            [@"D:\dst\x (2).txt", @"D:\dst\x (3).txt"],
            plan.Steps.Select(s => ((KeepBothStep)s.Step).Destination));
    }

    [Fact]
    public void Keep_both_where_it_is_not_allowed_means_skip()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"D:\dst\a.txt").File(@"C:\src\b.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"D:\dst", true, "a.txt", "b.txt"));
        Assert.False(Assert.Single(scan.Conflicts).KeepBothAllowed);

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, Decide((@"D:\dst\a.txt", FileDecision.KeepBoth)), NothingTaken);

        Assert.DoesNotContain(plan.Steps, s => s.Step is KeepBothStep);
        Assert.Equal(["b.txt"], Assert.Single(RobocopySteps(plan)).FileNames);
        Assert.Equal([@"C:\src\a.txt"], plan.Kept.Select(k => k.SourcePath));
    }

    [Fact]
    public void A_same_volume_cut_keeps_both_with_a_move_step()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt").File(@"C:\dst\a.txt");
        var scan = Scan(disk, Batch(@"C:\src", @"C:\dst", true, "a.txt"));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, Decide((@"C:\dst\a.txt", FileDecision.KeepBoth)), NothingTaken);

        Assert.Equal(new KeepBothStep(@"C:\src\a.txt", @"C:\dst\a (2).txt", Move: true), Assert.Single(plan.Steps).Step);
    }

    [Fact]
    public void A_drive_root_source_splits_cleanly()
    {
        var disk = new FakeDisk().File(@"C:\keep.txt").File(@"D:\dst\keep.txt").File(@"C:\go.txt");
        var scan = Scan(disk, Batch(@"C:\", @"D:\dst", true, "keep.txt", "go.txt"));

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Skip, null, NothingTaken);

        var step = Assert.Single(RobocopySteps(plan));
        Assert.Equal(@"C:\", step.SourceDirectory);
        Assert.Equal(["go.txt"], step.FileNames);
    }

    [Fact]
    public void Plan_level_lists_are_unions_and_issues_pass_through()
    {
        var disk = new FakeDisk()
            .Link(@"C:\src\A\l1").Link(@"C:\src\B\l2").File(@"C:\src\A\f.txt").Dir(@"D:\dst");
        var rejected = new PlanIssue(@"C:\x", PastePlanner.MissingReason);
        var scan = JobScanner.Scan(
            new PastePlan([Tree(@"C:\src\A", @"D:\dst\A", false), Tree(@"C:\src\B", @"D:\dst\B", false)], [rejected], []),
            disk, disk, null, CancellationToken.None);

        var plan = ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, null, NothingTaken);

        Assert.Equal([@"D:\dst\A\l1", @"D:\dst\B\l2"], plan.LinkFolders);
        Assert.Equal([rejected], plan.Issues);
    }

    /// <summary>
    /// Safety property (a): over generated trees and decisions, no source the user kept is
    /// reachable by any move step. Also: keep-both and refused sources are reachable by no
    /// robocopy step at all; a kept file is reachable only by a copy step whose own flags skip
    /// it (Skip or KeepNewer, the uniform copy cases); every other planned file is reachable
    /// exactly once; each step's Files is exactly what it reaches minus kept files; and no
    /// source folder is lost. Reachability is computed from the disk itself, not from the
    /// scan, so a scan defect cannot hide a planner defect.
    /// </summary>
    [Fact]
    public void No_kept_source_is_reachable_by_any_robocopy_step()
    {
        var splitsSeen = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var move = random.Next(4) != 0;
            var disk = new FakeDisk();
            var sources = new List<string>();
            var folders = new List<string>();
            GenerateTree(random, disk, @"C:\src\T", depth: 0, sources, folders);
            for (var i = 0; i < random.Next(0, 4); i++)
            {
                var loose = $@"C:\src\loose{i}.txt";
                disk.File(loose, minute: random.Next(10));
                sources.Add(loose);
            }
            // A second folder with same-named files, selected together (a search-results view):
            // every one of them names a destination the first folder's files also go to.
            var others = new List<string>();
            for (var i = 0; i < random.Next(0, 3); i++)
            {
                var other = $@"C:\other\loose{i}.txt";
                disk.File(other, minute: random.Next(10));
                others.Add(other);
            }
            disk.Dir(@"D:\dst");
            switch (random.Next(12))
            {
                case 0:
                    // A file holds the pasted folder's name: the whole tree is refused.
                    disk.File(@"D:\dst\T");
                    break;
                case < 8:
                    disk.Dir(@"D:\dst\T");
                    break;
            }
            GenerateDestination(random, disk, sources, folders);

            var selection = new List<SourceItem> { new(@"C:\src\T", IsDirectory: true) };
            selection.AddRange(sources.Where(s => WinPath.AreSame(WinPath.GetParent(s), @"C:\src")).Select(s => new SourceItem(s, IsDirectory: false)));
            selection.AddRange(others.Select(s => new SourceItem(s, IsDirectory: false)));
            var pastePlan = PastePlanner.Plan(new PasteRequest(selection, @"D:\dst", move ? TransferVerb.Move : TransferVerb.Copy), disk);
            var steps = pastePlan.Steps;
            var scan = JobScanner.Scan(pastePlan, disk, disk, null, CancellationToken.None);
            var (configured, choice) = RandomDecision(random, scan);

            var plan = ExecutionPlanner.Apply(scan, configured, choice, NothingTaken, fileListBudget: 60);

            var context = $"seed {seed}, move {move}, {configured}";
            var kept = new HashSet<string>(plan.Kept.Select(k => k.SourcePath), WinPath.Comparer);
            var protectedSources = new HashSet<string>(kept, WinPath.Comparer);
            protectedSources.UnionWith(plan.Steps.Select(s => s.Step).OfType<KeepBothStep>().Select(k => k.Source));
            protectedSources.UnionWith(scan.Issues.Select(i => i.Path));
            var reached = new Dictionary<string, int>(WinPath.Comparer);
            var writtenTo = new Dictionary<string, string>(WinPath.Comparer);
            foreach (var step in plan.Steps.Where(s => s.Step is RobocopyStep))
            {
                var robocopy = (RobocopyStep)step.Step;
                var reachable = Reachable(disk, robocopy);
                foreach (var source in reachable.Where(s => !kept.Contains(s)))
                {
                    // No two sources of one paste may land on one destination name.
                    var destination = WinPath.Combine(robocopy.DestinationDirectory, source[(WinPath.TrimTrailingSeparators(robocopy.SourceDirectory).Length + 1)..]);
                    Assert.False(
                        writtenTo.TryGetValue(destination, out var earlier) && !WinPath.AreSame(earlier, source),
                        $"{context}: {source} and {earlier} both go to {destination}");
                    writtenTo[destination] = source;
                }
                var filtersMayKeep = !robocopy.Move && step.Policy is ConflictPolicy.Skip or ConflictPolicy.KeepNewer;
                foreach (var source in reachable)
                {
                    if (robocopy.Move)
                    {
                        Assert.False(kept.Contains(source), $"{context}: kept {source} is reachable by a move step");
                    }
                    if (kept.Contains(source))
                    {
                        Assert.True(filtersMayKeep, $"{context}: kept {source} is reachable under {step.Policy}");
                        continue;
                    }
                    Assert.False(protectedSources.Contains(source), $"{context}: {source} is keep-both or refused but reachable");
                    Assert.False(protectedSources.Any(p => WinPath.IsStrictlyUnder(source, p)), $"{context}: {source} is under a refused folder");
                    reached[source] = reached.GetValueOrDefault(source) + 1;
                }
                Assert.Equal(
                    reachable.Where(s => !kept.Contains(s)).Order(WinPath.Comparer),
                    step.Files.Select(f => f.SourcePath).Order(WinPath.Comparer));
            }
            var expectedToRun = scan.Steps.SelectMany(s => s.Files).Select(f => f.SourcePath)
                .Where(s => !protectedSources.Contains(s));
            Assert.All(expectedToRun, s => Assert.True(reached.GetValueOrDefault(s) == 1, $"{context}: {s} reached {reached.GetValueOrDefault(s)} times"));
            Assert.Equal(reached.Count, reached.Keys.Count(s => expectedToRun.Contains(s, WinPath.Comparer)));

            // Every source folder that is not refused is either copied by a recursive step or
            // was split (it holds a protected item, so its destination already exists).
            foreach (var folder in folders.Where(f => !protectedSources.Any(p => WinPath.AreSame(f, p) || WinPath.IsStrictlyUnder(f, p))))
            {
                var covered = RobocopySteps(plan).Any(s => s.Recursive && (WinPath.AreSame(folder, s.SourceDirectory) || WinPath.IsStrictlyUnder(folder, s.SourceDirectory)))
                    || protectedSources.Any(p => WinPath.IsStrictlyUnder(p, folder))
                    || RobocopySteps(plan).Any(s => !s.Recursive && WinPath.AreSame(s.SourceDirectory, folder));
                Assert.True(covered, $"{context}: folder {folder} is lost");
            }
            if (RobocopySteps(plan).Count > steps.Count(s => s is RobocopyStep))
            {
                splitsSeen++;
            }
        }
        // The generator must actually exercise the split path, not only uniform plans.
        Assert.True(splitsSeen > 100, $"only {splitsSeen} split plans");
    }

    private static void GenerateTree(Random random, FakeDisk disk, string directory, int depth, List<string> sources, List<string> folders)
    {
        disk.Dir(directory);
        if (depth > 0)
        {
            folders.Add(directory);
        }
        for (var i = 0; i < random.Next(0, 4); i++)
        {
            var file = WinPath.Combine(directory, $"f{i}.txt");
            disk.File(file, minute: random.Next(10));
            sources.Add(file);
        }
        if (random.Next(6) == 0)
        {
            disk.Link(WinPath.Combine(directory, "link"));
        }
        if (depth < 4)
        {
            for (var i = 0; i < random.Next(0, 3); i++)
            {
                GenerateTree(random, disk, WinPath.Combine(directory, $"d{i}"), depth + 1, sources, folders);
            }
        }
    }

    /// <summary>Mirrors part of the source at the destination: conflicting files, and names held by the other kind.</summary>
    private static void GenerateDestination(Random random, FakeDisk disk, List<string> sources, List<string> folders)
    {
        static string ToDestination(string source) => @"D:\dst" + source[@"C:\src".Length..];

        foreach (var folder in folders)
        {
            switch (random.Next(8))
            {
                case 0:
                    disk.File(ToDestination(folder));
                    break;
                case < 4:
                    disk.Dir(ToDestination(folder));
                    break;
            }
        }
        foreach (var source in sources)
        {
            var destination = ToDestination(source);
            if (disk.KindOf(WinPath.GetParent(destination)) != ItemKind.Directory)
            {
                continue;
            }
            switch (random.Next(10))
            {
                case 0:
                    disk.Dir(destination);
                    break;
                case < 5:
                    disk.File(destination, minute: random.Next(10));
                    break;
            }
        }
    }

    private static (ConflictPolicy, ConflictChoice?) RandomDecision(Random random, ScanResult scan) => random.Next(6) switch
    {
        0 => (ConflictPolicy.Skip, null),
        1 => (ConflictPolicy.KeepNewer, null),
        2 => (ConflictPolicy.Ask, new ConflictChoice.SkipAll()),
        _ => (ConflictPolicy.Ask, new ConflictChoice.DecideEach(
            scan.Conflicts.Where(_ => random.Next(5) != 0)
                .ToDictionary(c => c.DestinationPath, _ => (FileDecision)random.Next(3), WinPath.Comparer))),
    };

    private static bool IsFile(FakeDisk disk, string path) => disk.KindOf(path) == ItemKind.File;

    /// <summary>Every source file robocopy could touch for this step, read from the disk (links not followed, /XJD).</summary>
    private static List<string> Reachable(FakeDisk disk, RobocopyStep step)
    {
        if (step.FileNames.Count > 0)
        {
            return step.FileNames.Select(n => WinPath.Combine(step.SourceDirectory, n)).Where(p => IsFile(disk, p)).ToList();
        }
        var found = new List<string>();
        var pending = new Stack<string>([step.SourceDirectory]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in disk.List(directory) ?? [])
            {
                var path = WinPath.Combine(directory, entry.Name);
                if (entry.Kind == ScanEntryKind.File)
                {
                    found.Add(path);
                }
                else if (entry.Kind == ScanEntryKind.Directory && step.Recursive)
                {
                    pending.Push(path);
                }
            }
        }
        return found;
    }
}
