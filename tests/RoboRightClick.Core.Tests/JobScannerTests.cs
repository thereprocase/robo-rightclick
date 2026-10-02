using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// A file system in memory that records every question asked of it, so tests can prove
/// which directories were read. Drive roots exist implicitly; adding a path creates its
/// parent folders. Shared by the engine tests in this assembly.
/// </summary>
internal sealed class FakeDisk : IScanFacts, IPlanningFacts
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, (ScanEntryKind Kind, FileFacts? Facts)> _nodes = new(WinPath.Comparer);
    private readonly Dictionary<string, List<string>> _children = new(WinPath.Comparer);

    public List<string> Listed { get; } = [];

    public List<string> FilesAsked { get; } = [];

    public HashSet<string> Unreadable { get; } = new(WinPath.Comparer);

    public int SameVolumeCalls { get; private set; }

    /// <summary>Runs inside every <see cref="List"/> call, before it answers.</summary>
    public Action<string>? OnList { get; set; }

    public FakeDisk Dir(string path)
    {
        Add(path, ScanEntryKind.Directory, null);
        return this;
    }

    public FakeDisk File(string path, long size = 10, int minute = 0)
    {
        Add(path, ScanEntryKind.File, new FileFacts(size, BaseTime.AddMinutes(minute)));
        return this;
    }

    public FakeDisk Link(string path)
    {
        Add(path, ScanEntryKind.DirectoryLink, null);
        return this;
    }

    public int ListCount(string directory) => Listed.Count(p => WinPath.AreSame(p, directory));

    public IEnumerable<string> ListedUnder(string root) =>
        Listed.Where(p => WinPath.AreSame(p, root) || WinPath.IsStrictlyUnder(p, root));

    public IEnumerable<ScanEntry>? List(string directory)
    {
        Listed.Add(directory);
        OnList?.Invoke(directory);
        var path = WinPath.TrimTrailingSeparators(directory);
        if (Unreadable.Contains(path) || !IsFolder(path))
        {
            return null;
        }
        return _children.TryGetValue(path, out var names)
            ? names.Select(name =>
            {
                var node = _nodes[WinPath.Combine(path, name)];
                return new ScanEntry(name, node.Kind, node.Facts);
            }).ToList()
            : [];
    }

    public FileFacts? FileAt(string path)
    {
        FilesAsked.Add(path);
        return _nodes.TryGetValue(WinPath.TrimTrailingSeparators(path), out var node) && node.Kind == ScanEntryKind.File
            ? node.Facts
            : null;
    }

    public bool Exists(string path) => IsFolder(WinPath.TrimTrailingSeparators(path)) || _nodes.ContainsKey(WinPath.TrimTrailingSeparators(path));

    public bool SameVolume(string a, string b)
    {
        SameVolumeCalls++;
        return WinPath.Comparer.Equals(WinPath.GetRoot(a), WinPath.GetRoot(b));
    }

    public ItemKind KindOf(string path)
    {
        var p = WinPath.TrimTrailingSeparators(path);
        if (WinPath.IsRoot(p))
        {
            return ItemKind.Directory;
        }
        return _nodes.TryGetValue(p, out var node)
            ? node.Kind switch
            {
                ScanEntryKind.File => ItemKind.File,
                ScanEntryKind.Directory => ItemKind.Directory,
                _ => ItemKind.DirectoryLink,
            }
            : ItemKind.Missing;
    }

    public string FinalPath(string path) => WinPath.TrimTrailingSeparators(path);

    private bool IsFolder(string path) =>
        WinPath.IsRoot(path) || (_nodes.TryGetValue(path, out var node) && node.Kind != ScanEntryKind.File);

    private void Add(string path, ScanEntryKind kind, FileFacts? facts)
    {
        var p = WinPath.TrimTrailingSeparators(path);
        var parent = WinPath.GetParent(p);
        if (!WinPath.IsRoot(parent) && !_nodes.ContainsKey(parent))
        {
            Dir(parent);
        }
        if (!_nodes.ContainsKey(p))
        {
            if (!_children.TryGetValue(parent, out var names))
            {
                names = [];
                _children[parent] = names;
            }
            names.Add(WinPath.GetFileName(p));
        }
        _nodes[p] = (kind, facts);
    }
}

/// <summary>Records reports synchronously (Progress&lt;T&gt; would post them to a thread pool).</summary>
internal sealed class RecordingProgress(Action<ScanProgress>? onReport = null) : IProgress<ScanProgress>
{
    public List<ScanProgress> Reports { get; } = [];

    public void Report(ScanProgress value)
    {
        Reports.Add(value);
        onReport?.Invoke(value);
    }
}

public class JobScannerTests
{
    private static PastePlan Plan(params PlanStep[] steps) => new(steps, [], []);

    private static ScanResult Scan(FakeDisk disk, PastePlan plan, IProgress<ScanProgress>? progress = null, CancellationToken token = default) =>
        JobScanner.Scan(plan, disk, disk, progress, token);

    private static RobocopyStep Tree(string source, string destination, bool move = false) =>
        new(source, destination, [], Recursive: true, Move: move);

    private static RobocopyStep Batch(string source, string destination, bool move, params string[] names) =>
        new(source, destination, names, Recursive: false, Move: move);

    [Fact]
    public void Named_files_are_read_one_by_one_and_a_missing_one_becomes_an_issue()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt", size: 5).File(@"C:\src\b.txt", size: 7).Dir(@"D:\dst");
        var rejected = new PlanIssue(@"C:\src\link", PastePlanner.LinkReason);
        var plan = new PastePlan([Batch(@"C:\src", @"D:\dst", false, "a.txt", "gone.txt", "b.txt")], [rejected], []);

        var scan = Scan(disk, plan);

        var files = Assert.Single(scan.Steps).Files;
        Assert.Equal([@"C:\src\a.txt", @"C:\src\b.txt"], files.Select(f => f.SourcePath));
        Assert.Equal([@"D:\dst\a.txt", @"D:\dst\b.txt"], files.Select(f => f.DestinationPath));
        Assert.Equal(12, scan.TotalBytes);
        Assert.Equal(2, scan.TotalFiles);
        // The paste plan's rejections come first, then the scan's own.
        Assert.Equal([rejected, new PlanIssue(@"C:\src\gone.txt", PastePlanner.MissingReason)], scan.Issues);
        Assert.Empty(disk.ListedUnder(@"C:\"));
    }

    [Fact]
    public void A_tree_maps_relative_paths_records_every_folder_and_does_not_enter_links()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\top.txt")
            .File(@"C:\src\T\sub\deep\leaf.bin", size: 100)
            .Dir(@"C:\src\T\empty")
            .Link(@"C:\src\T\junction")
            .File(@"C:\elsewhere\target-content.txt")
            .Dir(@"D:\dst");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")));

        var step = Assert.Single(scan.Steps);
        Assert.Equal(
            [(@"C:\src\T\top.txt", @"D:\dst\T\top.txt"), (@"C:\src\T\sub\deep\leaf.bin", @"D:\dst\T\sub\deep\leaf.bin")],
            step.Files.Select(f => (f.SourcePath, f.DestinationPath)));
        Assert.Equal([@"D:\dst\T\junction"], step.LinkFolders);
        Assert.Equal([@"C:\src\T\sub", @"C:\src\T\empty", @"C:\src\T\sub\deep"], step.Directories);
        Assert.Equal(0, disk.ListCount(@"C:\src\T\junction"));
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public void Nothing_below_an_absent_destination_folder_is_ever_listed()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\a.txt")
            .File(@"C:\src\T\x\b.txt")
            .File(@"C:\src\T\x\y\c.txt")
            .Dir(@"D:\dst");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.Equal(3, scan.TotalFiles);
        Assert.Empty(scan.Conflicts);
        // One attempt at the step's own destination, which fails; nothing below it.
        Assert.Equal([@"D:\dst\T"], disk.ListedUnder(@"D:\"));
    }

    [Fact]
    public void Only_destination_folders_that_exist_are_listed_and_each_only_once()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\x\b.txt")
            .File(@"C:\src\T\new\c.txt")
            .File(@"C:\src\T\new\deeper\d.txt")
            .File(@"C:\src\U\x\e.txt")
            .Dir(@"D:\dst\T\x");

        // Two steps write into the same destination folder.
        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T"), Tree(@"C:\src\U", @"D:\dst\T")));

        Assert.Equal(4, scan.TotalFiles);
        Assert.Equal(1, disk.ListCount(@"D:\dst\T"));
        Assert.Equal(1, disk.ListCount(@"D:\dst\T\x"));
        Assert.Equal(0, disk.ListCount(@"D:\dst\T\new"));
        Assert.Equal(0, disk.ListCount(@"D:\dst\T\new\deeper"));
    }

    [Fact]
    public void An_unreadable_destination_folder_hides_everything_below_it()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\x\y\a.txt")
            .Dir(@"D:\dst\T\x\y");
        disk.Unreadable.Add(@"D:\dst\T\x");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.Single(scan.Steps[0].Files);
        Assert.Equal(1, disk.ListCount(@"D:\dst\T\x"));
        Assert.Equal(0, disk.ListCount(@"D:\dst\T\x\y"));
    }

    [Fact]
    public void A_step_destination_below_one_already_found_absent_is_not_listed()
    {
        var disk = new FakeDisk().File(@"C:\src\A\a.txt").File(@"C:\src\B\b.txt").Dir(@"D:\dst");

        // The second step targets a folder inside the first one's absent destination.
        Scan(disk, Plan(Tree(@"C:\src\A", @"D:\dst\A"), Tree(@"C:\src\B", @"D:\dst\A\B")));

        Assert.Equal([@"D:\dst\A"], disk.ListedUnder(@"D:\"));
    }

    [Fact]
    public void A_listing_already_read_answers_for_a_step_destination_inside_it()
    {
        var disk = new FakeDisk().File(@"C:\src\loose.txt").File(@"C:\src\T\a.txt").Dir(@"D:\dst");

        // The batch lists D:\dst; the tree's destination D:\dst\T is then known absent from it.
        Scan(disk, Plan(Batch(@"C:\src", @"D:\dst", false, "loose.txt"), Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.Equal([@"D:\dst"], disk.ListedUnder(@"D:\"));
        Assert.DoesNotContain(@"D:\dst\T", disk.FilesAsked, WinPath.Comparer);
    }

    [Fact]
    public void Existing_files_become_conflicts_with_both_sides_facts()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\a.txt", size: 5, minute: 10)
            .File(@"C:\src\b.txt")
            .File(@"D:\dst\a.txt", size: 9, minute: 1);

        var scan = Scan(disk, Plan(Batch(@"C:\src", @"D:\dst", false, "a.txt", "b.txt")));

        var conflict = Assert.Single(scan.Conflicts);
        Assert.Equal(@"C:\src\a.txt", conflict.SourcePath);
        Assert.Equal(@"D:\dst\a.txt", conflict.DestinationPath);
        Assert.Equal(5, conflict.Source.Size);
        Assert.Equal(9, conflict.Existing.Size);
        Assert.True(conflict.SourceIsNewer);
        Assert.True(conflict.KeepBothAllowed);
        // A conflicting file still belongs to the plan; the decision comes later.
        Assert.Equal(2, scan.TotalFiles);
    }

    [Theory]
    [InlineData(false, @"D:\dst", true)]
    [InlineData(true, @"C:\dst", true)]
    [InlineData(true, @"D:\dst", false)]
    public void Keep_both_is_refused_only_for_a_cut_across_volumes(bool move, string destination, bool allowed)
    {
        var disk = new FakeDisk()
            .File(@"C:\src\a.txt").File(@"C:\src\b.txt")
            .File(WinPath.Combine(destination, "a.txt")).File(WinPath.Combine(destination, "b.txt"));

        var scan = Scan(disk, Plan(Batch(@"C:\src", destination, move, "a.txt", "b.txt")));

        Assert.Equal(2, scan.Conflicts.Count);
        Assert.All(scan.Conflicts, c => Assert.Equal(allowed, c.KeepBothAllowed));
        // Asked once per step at most, and never for a copy.
        Assert.Equal(move ? 1 : 0, disk.SameVolumeCalls);
    }

    [Fact]
    public void A_step_without_conflicts_never_asks_about_volumes()
    {
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").Dir(@"D:\dst");

        Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T", move: true)));

        Assert.Equal(0, disk.SameVolumeCalls);
    }

    [Fact]
    public void A_folder_holding_a_file_name_refuses_that_file()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\a.txt").File(@"C:\src\b.txt")
            .File(@"C:\src\T\c.txt").File(@"C:\src\T\d.txt")
            .Dir(@"D:\dst\a.txt")
            .Link(@"D:\dst\T\c.txt");

        var scan = Scan(disk, Plan(Batch(@"C:\src", @"D:\dst", false, "a.txt", "b.txt"), Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.Equal([@"C:\src\b.txt"], scan.Steps[0].Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\T\d.txt"], scan.Steps[1].Files.Select(f => f.SourcePath));
        Assert.Equal(
            [new PlanIssue(@"C:\src\a.txt", JobScanner.FolderInTheWayReason), new PlanIssue(@"C:\src\T\c.txt", JobScanner.FolderInTheWayReason)],
            scan.Issues);
        Assert.Empty(scan.Conflicts);
    }

    [Fact]
    public void A_file_holding_a_folder_name_refuses_the_whole_folder_without_reading_it()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\keep.txt")
            .File(@"C:\src\T\sub\a.txt")
            .File(@"C:\src\T\sub\inner\b.txt")
            .Link(@"C:\src\T\lnk")
            .File(@"D:\dst\T\sub")
            .File(@"D:\dst\T\lnk");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")));

        var step = Assert.Single(scan.Steps);
        Assert.Equal([@"C:\src\T\keep.txt"], step.Files.Select(f => f.SourcePath));
        Assert.Empty(step.Directories);
        Assert.Empty(step.LinkFolders);
        Assert.Equal(
            [new PlanIssue(@"C:\src\T\sub", JobScanner.FileInTheWayReason), new PlanIssue(@"C:\src\T\lnk", JobScanner.FileInTheWayReason)],
            scan.Issues);
        Assert.Equal(0, disk.ListCount(@"C:\src\T\sub"));
    }

    [Fact]
    public void A_file_holding_the_name_of_the_pasted_folder_refuses_the_step()
    {
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").File(@"D:\dst\T");

        // Twice: the second step must get the same answer from the scan's memory.
        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T"), Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.All(scan.Steps, s => Assert.Empty(s.Files));
        Assert.Equal(2, scan.Issues.Count(i => i == new PlanIssue(@"C:\src\T", JobScanner.FileInTheWayReason)));
        Assert.Equal(0, disk.ListCount(@"C:\src\T"));
    }

    [Fact]
    public void A_duplicate_is_one_file_and_a_rename_writes_nothing()
    {
        var disk = new FakeDisk().File(@"C:\src\a.txt", size: 3).Dir(@"C:\src\folder");
        var plan = Plan(
            new DuplicateFileStep(@"C:\src\a.txt", @"C:\src\a - Copy.txt"),
            new RenameStep(@"C:\src\folder", @"C:\other\folder"),
            new DuplicateFileStep(@"C:\src\gone.txt", @"C:\src\gone - Copy.txt"));

        var scan = Scan(disk, plan);

        Assert.Equal([new PlannedFile(@"C:\src\a.txt", @"C:\src\a - Copy.txt", new FileFacts(3, FakeDisk.BaseTime))], scan.Steps[0].Files);
        Assert.Empty(scan.Steps[1].Files);
        Assert.Empty(scan.Steps[2].Files);
        Assert.Equal([new PlanIssue(@"C:\src\gone.txt", PastePlanner.MissingReason)], scan.Issues);
        Assert.Same(plan, scan.Plan);
        Assert.Equal(plan.Steps, scan.Steps.Select(s => s.Step));
        Assert.Empty(disk.Listed);
    }

    [Fact]
    public void A_missing_or_unreadable_source_folder_yields_nothing()
    {
        var disk = new FakeDisk().File(@"C:\src\T\ok.txt").File(@"C:\src\T\locked\a.txt").Dir(@"D:\dst");
        disk.Unreadable.Add(@"C:\src\T\locked");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T"), Tree(@"C:\src\Gone", @"D:\dst\Gone")));

        Assert.Equal([@"C:\src\T\ok.txt"], scan.Steps[0].Files.Select(f => f.SourcePath));
        Assert.Equal([@"C:\src\T\locked"], scan.Steps[0].Directories);
        Assert.Empty(scan.Steps[1].Files);
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public void A_drive_root_source_folder_combines_cleanly()
    {
        var disk = new FakeDisk().File(@"C:\a.txt").File(@"D:\dst\a.txt");

        var scan = Scan(disk, Plan(Batch(@"C:\", @"D:\dst", false, "a.txt")));

        var file = Assert.Single(scan.Steps[0].Files);
        Assert.Equal(@"C:\a.txt", file.SourcePath);
        Assert.Equal(@"C:\a.txt", Assert.Single(scan.Conflicts).SourcePath);
    }

    [Fact]
    public void Destination_names_match_case_insensitively_including_non_ascii()
    {
        var disk = new FakeDisk()
            .File(@"C:\src\T\Ünïcödé 文件.txt")
            .File(@"C:\src\T\Straße\ok.txt")
            .File(@"D:\dst\T\ÜNÏCÖDÉ 文件.TXT")
            .File(@"D:\dst\T\STRASSE\other.txt")
            .Dir(@"D:\dst\T\straße");

        var scan = Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")));

        Assert.Equal(@"C:\src\T\Ünïcödé 文件.txt", Assert.Single(scan.Conflicts).SourcePath);
        // "straße" is listed because the folder exists under that name, not because of "STRASSE".
        Assert.Equal(1, disk.ListCount(@"D:\dst\T\Straße"));
        Assert.Equal(0, disk.ListCount(@"D:\dst\T\STRASSE"));
    }

    [Fact]
    public void Progress_is_reported_every_thousand_files_and_once_at_the_end()
    {
        var disk = new FakeDisk().Dir(@"D:\dst");
        for (var i = 0; i < 2_500; i++)
        {
            disk.File($@"C:\src\T\d{i % 7}\f{i}.bin", size: 2);
        }
        var progress = new RecordingProgress();

        Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")), progress);

        Assert.Equal([new(1_000, 2_000), new(2_000, 4_000), new(2_500, 5_000)], progress.Reports);
    }

    [Fact]
    public void An_empty_plan_still_reports_its_end()
    {
        var progress = new RecordingProgress();

        var scan = Scan(new FakeDisk(), Plan(), progress);

        Assert.Equal([new ScanProgress(0, 0)], progress.Reports);
        Assert.Empty(scan.Steps);
    }

    [Fact]
    public void Cancellation_before_the_scan_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var disk = new FakeDisk().File(@"C:\src\T\a.txt").Dir(@"D:\dst");

        Assert.Throws<OperationCanceledException>(() => Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")), token: cts.Token));
    }

    [Fact]
    public void Cancellation_is_seen_at_the_next_directory()
    {
        using var cts = new CancellationTokenSource();
        var disk = new FakeDisk()
            .File(@"C:\src\T\a\1.txt").File(@"C:\src\T\b\2.txt").File(@"C:\src\T\c\3.txt")
            .Dir(@"D:\dst");
        disk.OnList = dir =>
        {
            if (WinPath.AreSame(dir, @"C:\src\T\a"))
            {
                cts.Cancel();
            }
        };

        Assert.ThrowsAny<OperationCanceledException>(() => Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")), token: cts.Token));
        Assert.Equal(0, disk.ListCount(@"C:\src\T\b"));
    }

    [Fact]
    public void Cancellation_is_seen_within_a_large_directory_every_thousand_entries()
    {
        using var cts = new CancellationTokenSource();
        var disk = new FakeDisk().Dir(@"D:\dst");
        for (var i = 0; i < 5_000; i++)
        {
            disk.File($@"C:\src\T\f{i}.bin");
        }
        // Cancel at the first report (file 1,000); the next check is at entry 2,000.
        var progress = new RecordingProgress(_ => cts.Cancel());

        Assert.ThrowsAny<OperationCanceledException>(() => Scan(disk, Plan(Tree(@"C:\src\T", @"D:\dst\T")), progress, cts.Token));
        Assert.Equal([new ScanProgress(1_000, 10_000)], progress.Reports);
    }
}
