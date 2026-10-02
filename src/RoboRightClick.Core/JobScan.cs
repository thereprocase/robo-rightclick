namespace RoboRightClick.Core;

public enum ScanEntryKind
{
    File,
    Directory,

    /// <summary>
    /// A junction or directory symlink. Robocopy skips these (/XJD) and Explorer leaves
    /// an empty folder with the link's name, so the job recreates that empty folder.
    /// </summary>
    DirectoryLink,
}

/// <summary>One child of a directory. <paramref name="File"/> is set only for files.</summary>
public sealed record ScanEntry(string Name, ScanEntryKind Kind, FileFacts? File);

/// <summary>
/// The file-system facts the Scanning state needs. The host answers from disk
/// (FileSystemFacts, with FindFirstFileEx-style enumeration so one directory read yields
/// names, kinds, sizes and times); tests answer from a fixture.
/// </summary>
public interface IScanFacts
{
    /// <summary>
    /// Children of <paramref name="directory"/>, enumerated lazily without following
    /// reparse points; null when it does not exist or cannot be opened (robocopy reports
    /// the same error during the run).
    /// </summary>
    IEnumerable<ScanEntry>? List(string directory);

    /// <summary>Facts about the file at <paramref name="path"/>, or null when no file is there.</summary>
    FileFacts? FileAt(string path);
}

/// <summary>Running totals while scanning, for Explorer's "Discovered N items (X GB)".</summary>
public readonly record struct ScanProgress(long Files, long Bytes);

/// <summary>What one plan step will write.</summary>
/// <param name="Files">Every destination file the step creates or overwrites.</param>
/// <param name="LinkFolders">Destination paths of directory links the step skips; recreated empty in Finalizing.</param>
public sealed record StepScan(PlanStep Step, IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> LinkFolders)
{
    /// <summary>
    /// Source directories the walk found below a recursive step's root: not the root itself,
    /// not directory links, not a folder refused because a file holds its name. Robocopy /E
    /// recreates empty folders, so when <see cref="ExecutionPlanner"/> cuts a tree it needs
    /// every folder, not only those holding files, or the empty ones would be lost.
    /// </summary>
    public IReadOnlyList<string> Directories { get; init; } = [];
}

/// <summary>
/// The result of the Scanning state. Pre-existing destinations are exactly the
/// conflicts' destination paths; cancel cleanup must never delete those.
/// </summary>
/// <param name="Issues">The paste plan's rejections plus anything the scan refused, such as a file whose destination name is taken by a folder.</param>
public sealed record ScanResult(
    PastePlan Plan,
    IReadOnlyList<StepScan> Steps,
    IReadOnlyList<FileConflict> Conflicts,
    IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);
}

public static class JobScanner
{
    public const string FolderInTheWayReason = "A folder with this name already exists at the destination.";

    /// <summary>A source folder whose name a file holds at the destination; the folder's whole tree is left out.</summary>
    public const string FileInTheWayReason = "A file with this name already exists at the destination.";

    /// <summary>How often progress is reported (in files) and cancellation checked (in entries).</summary>
    public const int ReportInterval = 1_000;

    /// <summary>
    /// Expands a plan into the files it will write (Explorer's "Calculating…").
    /// For a <see cref="RobocopyStep"/>: the named files (one <see cref="IScanFacts.FileAt"/>
    /// each), or the whole tree when recursive, mapped to DestinationDirectory + relative
    /// path; directory links are not descended into and land in LinkFolders. For a
    /// <see cref="DuplicateFileStep"/>: its single file. A <see cref="RenameStep"/> writes no
    /// data and lists no files.
    /// </summary>
    /// <remarks>
    /// Conflicts are found by listing each destination directory once and matching names,
    /// not by one stat per planned file: a directory whose destination does not exist is not
    /// listed at all, nor is anything below it, so pasting into a new folder costs no extra
    /// I/O. A destination name held by a folder where the source is a file becomes an issue
    /// (<see cref="FolderInTheWayReason"/>) and the file leaves the plan; a destination file
    /// where the source is a folder likewise (<see cref="FileInTheWayReason"/>, with the
    /// folder's whole tree). <see cref="FileConflict.KeepBothAllowed"/> is
    /// false for a conflict in a move step whose source and destination are on different
    /// volumes (<see cref="IPlanningFacts.SameVolume"/>). <paramref name="progress"/> is
    /// reported at most every 1,000 files and at the end; <paramref name="cancellationToken"/>
    /// is checked per directory and every 1,000 entries.
    /// </remarks>
    public static ScanResult Scan(
        PastePlan plan,
        IScanFacts facts,
        IPlanningFacts planning,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scanner = new Scanner(facts, planning, progress, cancellationToken);
        var steps = new List<StepScan>(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            steps.Add(scanner.ScanStep(step));
        }
        scanner.ReportFinal();
        return new ScanResult(plan, steps, scanner.Conflicts, [.. plan.Rejected, .. scanner.Issues]);
    }

    private enum DestinationKind
    {
        Absent,
        File,
        Directory,
    }

    /// <summary>One scan's state: running totals, issues, conflicts and the destination listing cache.</summary>
    private sealed class Scanner(
        IScanFacts facts,
        IPlanningFacts planning,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        /// <summary>
        /// Destination listings by directory, null for one that is absent or could not be
        /// listed. Each destination directory is read at most once per scan, even when
        /// several steps merge into it.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, ScanEntry>?> _listings = new(WinPath.Comparer);

        /// <summary>Step destinations found to be files; the listing cache alone would call them absent.</summary>
        private readonly HashSet<string> _destinationFiles = new(WinPath.Comparer);

        private long _files;
        private long _bytes;
        private long _entries;

        public List<FileConflict> Conflicts { get; } = [];

        public List<PlanIssue> Issues { get; } = [];

        public StepScan ScanStep(PlanStep step) => step switch
        {
            RobocopyStep { FileNames.Count: > 0 } batch => ScanNamedFiles(batch),
            RobocopyStep tree => ScanTree(tree),
            DuplicateFileStep duplicate => ScanDuplicate(duplicate),
            _ => new StepScan(step, [], []),
        };

        public void ReportFinal() => progress?.Report(new ScanProgress(_files, _bytes));

        private StepScan ScanDuplicate(DuplicateFileStep step)
        {
            // The planner chose a free name and CopyFileEx with FAIL_IF_EXISTS guards the
            // race, so there is no destination to compare against here.
            if (facts.FileAt(step.Source) is not { } source)
            {
                Issues.Add(new PlanIssue(step.Source, PastePlanner.MissingReason));
                return new StepScan(step, [], []);
            }
            var file = new PlannedFile(step.Source, step.Destination, source);
            Count(file);
            return new StepScan(step, [file], []);
        }

        private StepScan ScanNamedFiles(RobocopyStep step)
        {
            var (kind, listing) = ResolveDestination(step.DestinationDirectory);
            var existingNames = kind == DestinationKind.Directory ? listing : null;
            var keepBoth = new KeepBothRule(step, planning);
            var files = new List<PlannedFile>(step.FileNames.Count);
            foreach (var name in step.FileNames)
            {
                CountEntry();
                var sourcePath = WinPath.Combine(step.SourceDirectory, name);
                if (facts.FileAt(sourcePath) is not { } source)
                {
                    Issues.Add(new PlanIssue(sourcePath, PastePlanner.MissingReason));
                    continue;
                }
                var file = new PlannedFile(sourcePath, WinPath.Combine(step.DestinationDirectory, name), source);
                if (AcceptFile(file, Existing(existingNames, name), keepBoth))
                {
                    files.Add(file);
                }
            }
            return new StepScan(step, files, []);
        }

        /// <summary>
        /// Depth-first walk of a robocopy tree. Each source directory is read completely before
        /// its subdirectories, so no enumeration stays open while the walk descends, and an
        /// explicit stack keeps a 16,000-level path from exhausting the thread's stack.
        /// </summary>
        private StepScan ScanTree(RobocopyStep step)
        {
            var root = WinPath.TrimTrailingSeparators(step.SourceDirectory);
            var (rootKind, rootListing) = ResolveDestination(step.DestinationDirectory);
            if (rootKind == DestinationKind.File)
            {
                Issues.Add(new PlanIssue(root, FileInTheWayReason));
                return new StepScan(step, [], []);
            }

            var keepBoth = new KeepBothRule(step, planning);
            var files = new List<PlannedFile>();
            var links = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<PendingDirectory>();
            pending.Push(new PendingDirectory(
                root,
                WinPath.TrimTrailingSeparators(step.DestinationDirectory),
                rootKind == DestinationKind.Directory,
                IsRoot: true));

            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The root's destination is already resolved. Below it a destination folder
                // is listed only when its parent's listing showed it, so nothing under an
                // absent or unreadable destination folder is ever read.
                var existingNames = directory.IsRoot
                    ? rootListing
                    : directory.DestinationExists ? ListDestination(directory.Destination) : null;

                var entries = facts.List(directory.Source);
                if (entries is null)
                {
                    // Missing or unreadable: robocopy reports the same error during the run.
                    continue;
                }

                var subdirectories = new List<PendingDirectory>();
                foreach (var entry in entries)
                {
                    CountEntry();
                    if (entry.Name is "" or "." or "..")
                    {
                        continue;
                    }
                    var sourcePath = WinPath.Combine(directory.Source, entry.Name);
                    var destinationPath = WinPath.Combine(directory.Destination, entry.Name);
                    var existing = Existing(existingNames, entry.Name);
                    switch (entry.Kind)
                    {
                        case ScanEntryKind.File:
                            if ((entry.File ?? facts.FileAt(sourcePath)) is not { } source)
                            {
                                // Listed, then gone before its facts could be read.
                                continue;
                            }
                            var file = new PlannedFile(sourcePath, destinationPath, source);
                            if (AcceptFile(file, existing, keepBoth))
                            {
                                files.Add(file);
                            }
                            break;

                        // Without /E robocopy copies only the top folder's files.
                        case ScanEntryKind.Directory when step.Recursive:
                            if (existing is { Kind: ScanEntryKind.File })
                            {
                                Issues.Add(new PlanIssue(sourcePath, FileInTheWayReason));
                                break;
                            }
                            directories.Add(sourcePath);
                            subdirectories.Add(new PendingDirectory(sourcePath, destinationPath, existing is not null, IsRoot: false));
                            break;

                        case ScanEntryKind.DirectoryLink when step.Recursive:
                            // Robocopy skips links (/XJD) and Finalizing recreates an empty
                            // folder, which a file holding the name would make impossible.
                            if (existing is { Kind: ScanEntryKind.File })
                            {
                                Issues.Add(new PlanIssue(sourcePath, FileInTheWayReason));
                                break;
                            }
                            links.Add(destinationPath);
                            break;
                    }
                }

                // Reversed, so the first subdirectory listed is the first one walked.
                for (var i = subdirectories.Count - 1; i >= 0; i--)
                {
                    pending.Push(subdirectories[i]);
                }
            }
            return new StepScan(step, files, links) { Directories = directories };
        }

        /// <summary>Records a conflict, or refuses the file; returns whether it stays in the plan.</summary>
        private bool AcceptFile(PlannedFile file, ScanEntry? existing, KeepBothRule keepBoth)
        {
            switch (existing?.Kind)
            {
                case ScanEntryKind.Directory or ScanEntryKind.DirectoryLink:
                    Issues.Add(new PlanIssue(file.SourcePath, FolderInTheWayReason));
                    return false;
                case ScanEntryKind.File:
                    // An entry that vanished before its facts could be read is treated as
                    // absent: the Ask flags still keep robocopy from overwriting it unasked.
                    if ((existing.File ?? facts.FileAt(file.DestinationPath)) is { } present)
                    {
                        Conflicts.Add(new FileConflict(file.SourcePath, file.DestinationPath, file.Source, present)
                        {
                            KeepBothAllowed = keepBoth.Allowed,
                        });
                    }
                    break;
            }
            Count(file);
            return true;
        }

        /// <summary>
        /// What is at a step's destination folder. A listing already read for its parent
        /// answers without touching the disk, and a folder under one known to be absent is
        /// absent. Otherwise the folder is listed directly, and only when that fails is it
        /// checked for being a file (a folder pasted where a file has its name).
        /// </summary>
        private (DestinationKind Kind, Dictionary<string, ScanEntry>? Listing) ResolveDestination(string directory)
        {
            var path = WinPath.TrimTrailingSeparators(directory);
            if (_destinationFiles.Contains(path))
            {
                return (DestinationKind.File, null);
            }
            if (_listings.TryGetValue(path, out var cached))
            {
                return cached is null ? (DestinationKind.Absent, null) : (DestinationKind.Directory, cached);
            }

            var parent = WinPath.GetParent(path);
            if (parent.Length > 0 && _listings.TryGetValue(parent, out var parentListing) && parentListing is not null)
            {
                switch (Existing(parentListing, WinPath.GetFileName(path))?.Kind)
                {
                    case null:
                        _listings[path] = null;
                        return (DestinationKind.Absent, null);
                    case ScanEntryKind.File:
                        _destinationFiles.Add(path);
                        return (DestinationKind.File, null);
                }
            }
            else if (UnderAbsentDirectory(path))
            {
                _listings[path] = null;
                return (DestinationKind.Absent, null);
            }

            var listing = ListDestination(path);
            if (listing is not null)
            {
                return (DestinationKind.Directory, listing);
            }
            if (facts.FileAt(path) is null)
            {
                return (DestinationKind.Absent, null);
            }
            _destinationFiles.Add(path);
            return (DestinationKind.File, null);
        }

        /// <summary>True when the nearest ancestor this scan has looked at was absent or unreadable.</summary>
        private bool UnderAbsentDirectory(string path)
        {
            for (var ancestor = WinPath.GetParent(path); ancestor.Length > 0; ancestor = WinPath.GetParent(ancestor))
            {
                if (_listings.TryGetValue(ancestor, out var listing))
                {
                    return listing is null;
                }
            }
            return false;
        }

        private Dictionary<string, ScanEntry>? ListDestination(string directory)
        {
            var path = WinPath.TrimTrailingSeparators(directory);
            if (_listings.TryGetValue(path, out var cached))
            {
                return cached;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, ScanEntry>? listing = null;
            if (facts.List(path) is { } entries)
            {
                listing = new Dictionary<string, ScanEntry>(WinPath.Comparer);
                foreach (var entry in entries)
                {
                    CountEntry();
                    // A case-sensitive folder can hold names that differ only in case; the
                    // first stands for the name, as a case-insensitive lookup would see it.
                    listing.TryAdd(entry.Name, entry);
                }
            }
            _listings[path] = listing;
            return listing;
        }

        private static ScanEntry? Existing(Dictionary<string, ScanEntry>? listing, string name) =>
            listing is not null && listing.TryGetValue(name, out var entry) ? entry : null;

        private void Count(PlannedFile file)
        {
            _files++;
            _bytes += file.Source.Size;
            if (_files % ReportInterval == 0)
            {
                progress?.Report(new ScanProgress(_files, _bytes));
            }
        }

        private void CountEntry()
        {
            if (++_entries % ReportInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private readonly record struct PendingDirectory(string Source, string Destination, bool DestinationExists, bool IsRoot);

    /// <summary>
    /// Whether a conflict in this step can be kept both ways: not for a cut across volumes,
    /// which would need an OS move that deletes the source (invariant 1). Asked at most once
    /// per step, and only when the step has a conflict.
    /// </summary>
    private sealed class KeepBothRule(RobocopyStep step, IPlanningFacts planning)
    {
        private bool? _allowed;

        public bool Allowed => _allowed ??= !step.Move || planning.SameVolume(step.SourceDirectory, step.DestinationDirectory);
    }
}
