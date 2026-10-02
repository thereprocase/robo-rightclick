namespace RoboRightClick.Core;

/// <summary>Per-file answer in Explorer's "Let me decide" list.</summary>
public enum FileDecision
{
    /// <summary>Only the source box ticked: overwrite the destination.</summary>
    Replace,

    /// <summary>Only the destination box ticked (or neither): leave it, do not copy.</summary>
    Skip,

    /// <summary>Both boxes ticked: copy the source under <see cref="DuplicateNamer.KeepBothName"/>.</summary>
    KeepBoth,
}

/// <summary>The answer to the conflict dialog. Null wherever it is passed means the user canceled the job.</summary>
public abstract record ConflictChoice
{
    private ConflictChoice()
    {
    }

    public sealed record ReplaceAll : ConflictChoice;

    public sealed record SkipAll : ConflictChoice;

    /// <param name="ByDestination">Decision per conflicting destination path; a missing entry means Skip.</param>
    public sealed record DecideEach(IReadOnlyDictionary<string, FileDecision> ByDestination) : ConflictChoice;
}

/// <summary>
/// A "keep both" file, written in-process under a new name because robocopy cannot rename.
/// A copy uses CopyFileEx (COPY_FILE_FAIL_IF_EXISTS). A cut is allowed only within one
/// volume and runs as MoveFileEx with flags 0, a rename, which invariant 1 already permits;
/// a cross-volume cut never produces this step (<see cref="FileConflict.KeepBothAllowed"/>).
/// </summary>
public sealed record KeepBothStep(string Source, string Destination, bool Move) : PlanStep;

/// <summary>One step as it will actually run.</summary>
/// <param name="Policy">Conflict flags for a robocopy step (<see cref="RobocopyArgs.ConflictFlags"/>); ignored by other steps.</param>
/// <param name="Files">
/// Exactly the files this step is expected to report as done: every file the user chose to
/// keep is already left out. Progress totals, the ledger, cancel cleanup and retry all read
/// this list, so it must never include a file the step will not touch.
/// </param>
public sealed record ExecutionStep(PlanStep Step, ConflictPolicy Policy, IReadOnlyList<PlannedFile> Files);

/// <param name="PresentBeforeRun">Destinations the scan found already present (the conflicts). Cancel cleanup never deletes these.</param>
/// <param name="LinkFolders">Destination paths of skipped directory links; recreated empty in Finalizing (docs/parity.md).</param>
/// <param name="Kept">Conflicting files the user chose to leave alone (Skip, or not newer under KeepNewer); listed in the summary.</param>
/// <param name="Issues">Items refused at planning or scanning; Explorer reports these as errors.</param>
public sealed record ExecutionPlan(
    IReadOnlyList<ExecutionStep> Steps,
    IReadOnlyList<string> PresentBeforeRun,
    IReadOnlyList<string> LinkFolders,
    IReadOnlyList<FileConflict> Kept,
    IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);

    /// <summary>Nothing to run and nothing refused: every item is already where it was asked to go.</summary>
    public bool IsNoOp => Steps.Count == 0 && Issues.Count == 0;
}

public static class ExecutionPlanner
{
    /// <summary>
    /// Turns a scan plus the conflict answer into the steps that run. A file the user chose
    /// to keep is protected by construction (it is left out of every robocopy step) and
    /// never by robocopy's class filters or /XF: under /MOV robocopy can delete the source
    /// of a "same" file it skipped (LIKELY; M4 checks it), and /XF matching on full paths is
    /// unverified. Nothing here depends on either.
    /// </summary>
    /// <remarks>
    /// <para>Decision per conflict. Configured Replace / Skip apply to every conflict;
    /// KeepNewer replaces where <see cref="FileConflict.SourceIsNewer"/> and keeps the rest;
    /// Ask with conflicts takes <paramref name="choice"/> (ReplaceAll, SkipAll, or DecideEach,
    /// where a missing entry is Skip and KeepBoth where <see cref="FileConflict.KeepBothAllowed"/>
    /// is false is Skip). Ask with no conflicts runs every step with policy Ask, whose flags
    /// skip anything that appeared after the scan.</para>
    /// <para>Uniform cases keep the plan's steps unchanged: every conflict Replace (policy
    /// Replace), or a copy where every conflict is Skip (policy Skip) or decided by KeepNewer
    /// (policy KeepNewer). Robocopy's class filters are safe there because a copy deletes
    /// nothing.</para>
    /// <para>Every other case is split. Let K be the source directories holding a conflict
    /// that is not replaced (kept or keep-both). A file-batch step becomes: the Replace names
    /// (policy Replace), the non-conflicting names (policy Ask), each chunked like
    /// <see cref="PastePlanner"/>; kept names are dropped. A recursive step is cut along K:
    /// each directory on the path from the step's root to a directory in K becomes file-batch
    /// steps as above for its own files, and each child directory not on such a path becomes
    /// its own recursive step, policy Replace if it holds a replaced conflict, else Ask.
    /// Because every split directory still contains a kept file, a cut never leaves an
    /// emptied source folder behind. Keep-both files get one <see cref="KeepBothStep"/>
    /// each, named with <see cref="DuplicateNamer.KeepBothName"/> against
    /// <paramref name="destinationTaken"/> and names this plan already claimed.</para>
    /// <para>Steps come out in the scan's order (split pieces in place of their step), then
    /// the keep-both steps. Scan issues pass through to <see cref="ExecutionPlan.Issues"/>
    /// along with the paste plan's rejections.</para>
    /// <para>Items the scan refused (<see cref="JobScanner.FolderInTheWayReason"/>,
    /// <see cref="JobScanner.FileInTheWayReason"/>) are left out by construction in the same
    /// way: a file batch drops their names, and a tree holding one is cut along its folder as
    /// if it held a kept file, so robocopy never reaches what the scan said leaves the plan.
    /// A batch whose names are all gone produces no step at all: a robocopy step without
    /// names would copy the whole folder.</para>
    /// <para>With no conflicts every step runs with policy Ask, whatever the configured
    /// default: there was nothing to decide, and Ask's flags skip a file that appears at the
    /// destination after the scan instead of overwriting it unseen.</para>
    /// </remarks>
    /// <param name="destinationTaken">Whether a full destination path is already taken on disk; used only to name keep-both files.</param>
    /// <exception cref="InvalidOperationException">Policy Ask with conflicts and no <paramref name="choice"/>.</exception>
    public static ExecutionPlan Apply(
        ScanResult scan,
        ConflictPolicy configured,
        ConflictChoice? choice,
        Func<string, bool> destinationTaken,
        int fileListBudget = PastePlanner.DefaultFileListBudget)
    {
        var decisions = Decide(scan.Conflicts, configured, choice);
        var decisionByFile = new Dictionary<string, FileDecision>(WinPath.Comparer);
        for (var i = 0; i < scan.Conflicts.Count; i++)
        {
            decisionByFile[FileKey(scan.Conflicts[i].SourcePath, scan.Conflicts[i].DestinationPath)] = decisions[i];
        }

        var builder = new StepBuilder(decisionByFile, ScanRefusals(scan), fileListBudget);
        var uniform = UniformPolicy(scan, configured, decisions);
        foreach (var stepScan in scan.Steps)
        {
            if (uniform is { } policy)
            {
                builder.AddUniform(stepScan, policy);
            }
            else
            {
                builder.AddSplit(stepScan);
            }
        }
        builder.AddKeepBothSteps(ClaimedDestinations(scan), destinationTaken);

        var kept = new List<FileConflict>();
        for (var i = 0; i < scan.Conflicts.Count; i++)
        {
            if (decisions[i] == FileDecision.Skip)
            {
                kept.Add(scan.Conflicts[i]);
            }
        }

        return new ExecutionPlan(
            builder.Steps,
            Distinct(scan.Conflicts.Select(c => c.DestinationPath)),
            Distinct(scan.Steps.SelectMany(s => s.LinkFolders)),
            kept,
            scan.Issues);
    }

    /// <summary>The decision for each conflict, index for index.</summary>
    private static FileDecision[] Decide(IReadOnlyList<FileConflict> conflicts, ConflictPolicy configured, ConflictChoice? choice)
    {
        if (conflicts.Count == 0)
        {
            return [];
        }
        Func<FileConflict, FileDecision> rule = configured switch
        {
            ConflictPolicy.Replace => _ => FileDecision.Replace,
            ConflictPolicy.Skip => _ => FileDecision.Skip,
            ConflictPolicy.KeepNewer => c => c.SourceIsNewer ? FileDecision.Replace : FileDecision.Skip,
            ConflictPolicy.Ask => choice switch
            {
                ConflictChoice.ReplaceAll => _ => FileDecision.Replace,
                ConflictChoice.SkipAll => _ => FileDecision.Skip,
                ConflictChoice.DecideEach each => DecideEachRule(each),
                null => throw new InvalidOperationException("Conflicts need a decision before the job can run."),
                _ => throw new ArgumentOutOfRangeException(nameof(choice)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(configured)),
        };
        return conflicts.Select(rule).ToArray();
    }

    private static Func<FileConflict, FileDecision> DecideEachRule(ConflictChoice.DecideEach each)
    {
        // Looked up the way NTFS names compare. Two spellings of one path that disagree
        // resolve to Skip, the answer that writes nothing.
        var byDestination = new Dictionary<string, FileDecision>(WinPath.Comparer);
        foreach (var (path, decision) in each.ByDestination)
        {
            byDestination[path] = byDestination.TryGetValue(path, out var earlier) && earlier != decision
                ? FileDecision.Skip
                : decision;
        }
        return conflict =>
        {
            if (!byDestination.TryGetValue(conflict.DestinationPath, out var decision))
            {
                return FileDecision.Skip;
            }
            return decision == FileDecision.KeepBoth && !conflict.KeepBothAllowed ? FileDecision.Skip : decision;
        };
    }

    /// <summary>The one policy every step can keep, or null when the plan must be split.</summary>
    private static ConflictPolicy? UniformPolicy(ScanResult scan, ConflictPolicy configured, FileDecision[] decisions)
    {
        if (decisions.Length == 0)
        {
            return ConflictPolicy.Ask;
        }
        // Class filters may only protect kept files in a run that deletes nothing.
        var copy = !scan.Steps.Any(s => s.Step is RobocopyStep { Move: true });
        if (copy && configured == ConflictPolicy.KeepNewer)
        {
            return ConflictPolicy.KeepNewer;
        }
        if (decisions.All(d => d == FileDecision.Replace))
        {
            return ConflictPolicy.Replace;
        }
        if (copy && decisions.All(d => d == FileDecision.Skip))
        {
            return ConflictPolicy.Skip;
        }
        return null;
    }

    /// <summary>Source paths the scan itself refused (its issues after the paste plan's rejections).</summary>
    private static HashSet<string> ScanRefusals(ScanResult scan) =>
        new(scan.Issues.Skip(scan.Plan.Rejected.Count).Select(i => WinPath.TrimTrailingSeparators(i.Path)), WinPath.Comparer);

    /// <summary>
    /// Every destination path this plan will create, so a keep-both name never collides with
    /// a file or folder that does not exist yet but will by the time keep-both steps run.
    /// </summary>
    private static HashSet<string> ClaimedDestinations(ScanResult scan)
    {
        var claimed = new HashSet<string>(WinPath.Comparer);
        foreach (var stepScan in scan.Steps)
        {
            switch (stepScan.Step)
            {
                case RenameStep rename:
                    claimed.Add(WinPath.TrimTrailingSeparators(rename.Destination));
                    break;
                case RobocopyStep { Recursive: true } tree:
                    var root = WinPath.TrimTrailingSeparators(tree.SourceDirectory);
                    var destinationRoot = WinPath.TrimTrailingSeparators(tree.DestinationDirectory);
                    claimed.Add(destinationRoot);
                    foreach (var directory in stepScan.Directories.Where(d => WinPath.IsStrictlyUnder(d, root)))
                    {
                        claimed.Add(WinPath.Combine(destinationRoot, RelativePath(directory, root)));
                    }
                    foreach (var file in stepScan.Files)
                    {
                        // Folders robocopy creates on the way to each file; stops at the first already claimed.
                        var folder = WinPath.GetParent(file.DestinationPath);
                        while (WinPath.IsStrictlyUnder(folder, destinationRoot) && claimed.Add(folder))
                        {
                            folder = WinPath.GetParent(folder);
                        }
                    }
                    break;
            }
            foreach (var file in stepScan.Files)
            {
                claimed.Add(file.DestinationPath);
            }
            foreach (var link in stepScan.LinkFolders)
            {
                claimed.Add(WinPath.TrimTrailingSeparators(link));
            }
        }
        return claimed;
    }

    private static List<string> Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(WinPath.Comparer);
        return paths.Where(seen.Add).ToList();
    }

    /// <summary>A planned file's identity: its source and destination together (NUL never occurs in a path).</summary>
    private static string FileKey(string source, string destination) => source + "\0" + destination;

    /// <summary>The part of <paramref name="path"/> below <paramref name="root"/>; the caller has checked it is strictly under.</summary>
    private static string RelativePath(string path, string root)
    {
        var prefix = root.EndsWith(WinPath.Separator) ? root : root + WinPath.Separator;
        return path[prefix.Length..];
    }

    /// <summary>Planned files chunked by their source names (<see cref="ChunkByLength{T}"/>).</summary>
    internal static IEnumerable<List<PlannedFile>> ChunkByNameLength(IEnumerable<PlannedFile> files, int budget) =>
        ChunkByLength(files, file => WinPath.GetFileName(file.SourcePath), budget);

    /// <summary>File names in command-line order, cut where the next would pass <paramref name="budget"/>.</summary>
    internal static IEnumerable<List<string>> ChunkNamesByLength(IEnumerable<string> names, int budget) =>
        ChunkByLength(names, name => name, budget);

    /// <summary>
    /// The one command-line budget rule (PastePlanner, ExecutionPlanner and RetryPlanner all
    /// use it): each name costs its quoted length plus a separating space, and a chunk is cut
    /// before the name that would pass the budget. A single name over budget still gets a
    /// chunk of its own, so nothing is dropped.
    /// </summary>
    private static IEnumerable<List<T>> ChunkByLength<T>(IEnumerable<T> items, Func<T, string> nameOf, int budget)
    {
        var chunk = new List<T>();
        var used = 0;
        foreach (var item in items)
        {
            var cost = RobocopyArgs.Quote(nameOf(item)).Length + 1;
            if (chunk.Count > 0 && used + cost > budget)
            {
                yield return chunk;
                chunk = [];
                used = 0;
            }
            chunk.Add(item);
            used += cost;
        }
        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>Accumulates the executed steps in order, then the keep-both steps.</summary>
    private sealed class StepBuilder(Dictionary<string, FileDecision> decisionByFile, HashSet<string> refusals, int budget)
    {
        private readonly List<(PlannedFile File, bool Move)> _keepBoth = [];

        public List<ExecutionStep> Steps { get; } = [];

        private FileDecision? DecisionOf(PlannedFile file) =>
            decisionByFile.TryGetValue(FileKey(file.SourcePath, file.DestinationPath), out var decision) ? decision : null;

        /// <summary>The plan's own step under one policy, minus the files the decision keeps.</summary>
        public void AddUniform(StepScan stepScan, ConflictPolicy policy)
        {
            if (RefusedWhole(stepScan))
            {
                return;
            }
            var files = stepScan.Files.Where(f => DecisionOf(f) != FileDecision.Skip).ToList();
            switch (stepScan.Step)
            {
                case RobocopyStep { FileNames.Count: > 0 } batch:
                    var step = batch;
                    if (stepScan.Files.Count != batch.FileNames.Count)
                    {
                        // The scan dropped names (missing, or a folder in the way). Robocopy must
                        // not be handed them, and an emptied list would mean "the whole folder".
                        if (stepScan.Files.Count == 0)
                        {
                            return;
                        }
                        step = batch with { FileNames = stepScan.Files.Select(f => WinPath.GetFileName(f.SourcePath)).ToList() };
                    }
                    Steps.Add(new ExecutionStep(step, policy, files));
                    break;
                case RobocopyStep tree when HasRefusalUnder(tree):
                    AddSplit(stepScan);
                    break;
                default:
                    Steps.Add(new ExecutionStep(stepScan.Step, policy, files));
                    break;
            }
        }

        public void AddSplit(StepScan stepScan)
        {
            if (RefusedWhole(stepScan))
            {
                return;
            }
            switch (stepScan.Step)
            {
                case RobocopyStep { Recursive: true } tree:
                    AddTree(stepScan, tree);
                    break;
                case RobocopyStep batch:
                    AddBatches(batch.SourceDirectory, batch.DestinationDirectory, stepScan.Files, batch.Move);
                    break;
                default:
                    Steps.Add(new ExecutionStep(stepScan.Step, ConflictPolicy.Ask, stepScan.Files));
                    break;
            }
        }

        /// <summary>
        /// A step the scan refused as a whole: a tree whose destination name a file holds, or
        /// a duplicate whose source is gone. Running it anyway would hand robocopy the very
        /// tree the scan left out, or fail with an error already reported as an issue.
        /// </summary>
        private bool RefusedWhole(StepScan stepScan) => stepScan.Step switch
        {
            RobocopyStep { FileNames.Count: 0 } tree => refusals.Contains(WinPath.TrimTrailingSeparators(tree.SourceDirectory)),
            DuplicateFileStep => stepScan.Files.Count == 0,
            _ => false,
        };

        private bool HasRefusalUnder(RobocopyStep tree)
        {
            var root = WinPath.TrimTrailingSeparators(tree.SourceDirectory);
            return refusals.Any(path => WinPath.IsStrictlyUnder(path, root));
        }

        /// <summary>One folder's own files: Replace names, then the rest under Ask; kept names dropped.</summary>
        private void AddBatches(string sourceDirectory, string destinationDirectory, IEnumerable<PlannedFile> files, bool move)
        {
            var replace = new List<PlannedFile>();
            var ask = new List<PlannedFile>();
            foreach (var file in files)
            {
                switch (DecisionOf(file))
                {
                    case FileDecision.Replace:
                        replace.Add(file);
                        break;
                    case FileDecision.KeepBoth:
                        _keepBoth.Add((file, move));
                        break;
                    case null:
                        ask.Add(file);
                        break;
                }
            }
            foreach (var (group, policy) in new[] { (replace, ConflictPolicy.Replace), (ask, ConflictPolicy.Ask) })
            {
                foreach (var chunk in ChunkByNameLength(group, budget))
                {
                    var names = chunk.Select(f => WinPath.GetFileName(f.SourcePath)).ToList();
                    Steps.Add(new ExecutionStep(
                        new RobocopyStep(sourceDirectory, destinationDirectory, names, Recursive: false, Move: move),
                        policy,
                        chunk));
                }
            }
        }

        /// <summary>
        /// Cuts a recursive step along the folders that hold a kept, keep-both or refused item.
        /// Those folders ("marked") run as file batches of their own files; every other child
        /// of a marked folder runs as its own recursive step and so never reaches a kept file.
        /// </summary>
        private void AddTree(StepScan stepScan, RobocopyStep tree)
        {
            var root = WinPath.TrimTrailingSeparators(tree.SourceDirectory);
            var marked = new HashSet<string>(WinPath.Comparer);
            var holdsReplaced = new HashSet<string>(WinPath.Comparer);
            foreach (var file in stepScan.Files)
            {
                switch (DecisionOf(file))
                {
                    case FileDecision.Skip or FileDecision.KeepBoth:
                        MarkUpToRoot(marked, WinPath.GetParent(file.SourcePath), root);
                        break;
                    case FileDecision.Replace:
                        MarkUpToRoot(holdsReplaced, WinPath.GetParent(file.SourcePath), root);
                        break;
                }
            }
            foreach (var refused in refusals.Where(path => WinPath.IsStrictlyUnder(path, root)))
            {
                MarkUpToRoot(marked, WinPath.GetParent(refused), root);
            }

            if (marked.Count == 0)
            {
                Steps.Add(new ExecutionStep(tree, holdsReplaced.Count > 0 ? ConflictPolicy.Replace : ConflictPolicy.Ask, stepScan.Files));
                return;
            }

            var children = ChildDirectories(stepScan, root);
            var ownFiles = new Dictionary<string, List<PlannedFile>>(WinPath.Comparer);
            var subtreeFiles = new Dictionary<string, List<PlannedFile>>(WinPath.Comparer);
            var topByDirectory = new Dictionary<string, string>(WinPath.Comparer);

            // The topmost unmarked folder above a directory is the recursive step that copies it.
            // Memoized along each climb: on a path thousands of levels deep, climbing once per
            // file would rebuild every ancestor string for every file.
            string TopUnmarked(string start)
            {
                var climbed = new List<string>();
                var current = start;
                string top;
                while (true)
                {
                    if (topByDirectory.TryGetValue(current, out var known))
                    {
                        top = known;
                        break;
                    }
                    climbed.Add(current);
                    var up = WinPath.GetParent(current);
                    if (marked.Contains(up))
                    {
                        top = current;
                        break;
                    }
                    current = up;
                }
                foreach (var directory in climbed)
                {
                    topByDirectory[directory] = top;
                }
                return top;
            }
            foreach (var file in stepScan.Files)
            {
                var parent = WinPath.GetParent(file.SourcePath);
                if (marked.Contains(parent))
                {
                    ListFor(ownFiles, parent).Add(file);
                    continue;
                }
                ListFor(subtreeFiles, TopUnmarked(parent)).Add(file);
            }

            string DestinationOf(string directory) => WinPath.AreSame(directory, root)
                ? tree.DestinationDirectory
                : WinPath.Combine(tree.DestinationDirectory, RelativePath(directory, root));

            // Explicit stack: a marked chain can be as deep as the longest path.
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                if (!marked.Contains(directory))
                {
                    Steps.Add(new ExecutionStep(
                        new RobocopyStep(directory, DestinationOf(directory), [], Recursive: true, Move: tree.Move),
                        holdsReplaced.Contains(directory) ? ConflictPolicy.Replace : ConflictPolicy.Ask,
                        subtreeFiles.GetValueOrDefault(directory) ?? []));
                    continue;
                }
                var source = WinPath.AreSame(directory, root) ? tree.SourceDirectory : directory;
                AddBatches(source, DestinationOf(directory), ownFiles.GetValueOrDefault(directory) ?? [], tree.Move);
                if (children.TryGetValue(directory, out var below))
                {
                    for (var i = below.Count - 1; i >= 0; i--)
                    {
                        pending.Push(below[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Immediate subfolders of every folder in the tree, in the order the scan met them:
        /// from the scan's folder list (which includes empty folders) and, for a scan built
        /// without one, from the planned files' own folders. Refused folders are left out.
        /// </summary>
        private Dictionary<string, List<string>> ChildDirectories(StepScan stepScan, string root)
        {
            var children = new Dictionary<string, List<string>>(WinPath.Comparer);
            var known = new HashSet<string>(WinPath.Comparer) { root };
            var chain = new List<string>();

            void Add(string directory)
            {
                chain.Clear();
                for (var d = directory; !known.Contains(d); d = WinPath.GetParent(d))
                {
                    EnsureUnder(d, root);
                    chain.Add(d);
                }
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    known.Add(chain[i]);
                    ListFor(children, WinPath.GetParent(chain[i])).Add(chain[i]);
                }
            }

            foreach (var directory in stepScan.Directories)
            {
                var d = WinPath.TrimTrailingSeparators(directory);
                if (!refusals.Contains(d))
                {
                    Add(d);
                }
            }
            foreach (var file in stepScan.Files)
            {
                Add(WinPath.GetParent(file.SourcePath));
            }
            return children;
        }

        /// <summary>
        /// The paths are named against the destination as it was scanned plus every name this
        /// plan writes, and each chosen name is claimed so two keep-both files never pick the same one.
        /// </summary>
        public void AddKeepBothSteps(HashSet<string> claimed, Func<string, bool> destinationTaken)
        {
            foreach (var (file, move) in _keepBoth)
            {
                var directory = WinPath.GetParent(file.DestinationPath);
                var name = DuplicateNamer.KeepBothName(
                    WinPath.GetFileName(file.DestinationPath),
                    candidate =>
                    {
                        var path = WinPath.Combine(directory, candidate);
                        return claimed.Contains(path) || destinationTaken(path);
                    });
                var destination = WinPath.Combine(directory, name);
                claimed.Add(destination);
                Steps.Add(new ExecutionStep(
                    new KeepBothStep(file.SourcePath, destination, move),
                    ConflictPolicy.Ask,
                    [file with { DestinationPath = destination }]));
            }
        }

        private static void MarkUpToRoot(HashSet<string> set, string directory, string root)
        {
            for (var d = directory; ; d = WinPath.GetParent(d))
            {
                if (WinPath.AreSame(d, root))
                {
                    set.Add(root);
                    return;
                }
                EnsureUnder(d, root);
                if (!set.Add(d))
                {
                    // Already marked, and with it every folder up to the root.
                    return;
                }
            }
        }

        private static void EnsureUnder(string path, string root)
        {
            if (!WinPath.IsStrictlyUnder(path, root))
            {
                throw new InvalidOperationException("A scanned item lies outside its step's source folder.");
            }
        }

        private static List<T> ListFor<T>(Dictionary<string, List<T>> map, string key)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            return list;
        }
    }
}
