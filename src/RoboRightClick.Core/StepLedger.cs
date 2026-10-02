namespace RoboRightClick.Core;

/// <summary>What one robocopy event meant for the job.</summary>
/// <param name="Completed">The planned file this event completed, if any (its size feeds JobProgress).</param>
/// <param name="Uncompleted">A file that had been counted complete and an ERROR for it arrived later; subtract it.</param>
public sealed record LedgerUpdate(PlannedFile? Completed, PlannedFile? Uncompleted, ErrorReported? Error);

/// <summary>
/// The single reconciliation of "planned" against "reported" for one job, which progress,
/// cancel cleanup, retry and the summary all read. Without it each of those would match
/// robocopy's output against the plan its own way, and the ways would drift.
/// Not thread-safe: the job applies events under its lock.
/// </summary>
/// <remarks>
/// <para>Matching: robocopy's /FP path and the planned source path are compared after
/// <see cref="WinPath.NormalizeForMatch"/>. A FileReported that matches no planned file of
/// the current step sets <see cref="PathsUnreliable"/>: from then on cancel cleanup deletes
/// nothing for this job and "Try again" re-runs the whole paste with a fresh scan rather
/// than repeating steps, because a mismatch means the ledger can no longer tell finished
/// files from partial ones, and repeating a recursive /MOVE step under Replace is not safe.</para>
/// <para>Errors: an ErrorReported for a file path marks that file failed, and removes it
/// from the completed set if its line came first (order under /MT is not guaranteed). An
/// ErrorReported for a directory (robocopy's "Creating Destination Directory", "Scanning
/// Source Directory", or a destination path) marks every planned file of the step under
/// that directory, on the source or destination side, failed unless already completed.</para>
/// <para>After a step ends (<see cref="StepFinished"/>): files neither completed nor
/// failed are failed when the run was killed by the app's cancel (they go to cancel
/// cleanup, not retry), failed when the run died or exited fatal (>= 16) or never ran,
/// and otherwise "kept": robocopy skipped them under Skip flags because a file with that
/// name appeared after the scan, which the summary reports and retry leaves alone.</para>
/// <para>Only robocopy steps' files are ever <see cref="Retryable"/>: robocopy cannot write
/// a file under a new name, so a failed rename, duplicate or keep-both is repeated as a
/// whole step through <see cref="FailedInProcessSteps"/> instead.</para>
/// </remarks>
public sealed class StepLedger
{
    private static readonly LedgerUpdate NoChange = new(null, null, null);

    private readonly StepState[] _steps;
    private bool _pathsUnreliable;

    public StepLedger(ExecutionPlan plan)
    {
        Plan = plan;
        _steps = plan.Steps.Select(step => new StepState(step)).ToArray();
    }

    public ExecutionPlan Plan { get; }

    /// <summary>A reported path matched nothing planned; see the remarks.</summary>
    public bool PathsUnreliable => _pathsUnreliable;

    public void StepStarted(int stepIndex) => StateOf(stepIndex).Started = true;

    public LedgerUpdate Apply(int stepIndex, RobocopyEvent robocopyEvent)
    {
        var state = StateOf(stepIndex);
        // Output from a run proves the run started, whether or not StepStarted came first.
        state.Started = true;
        return robocopyEvent switch
        {
            FileReported reported => ApplyFile(state, reported),
            ErrorReported error => ApplyError(state, error),
            _ => NoChange,
        };
    }

    /// <summary>An in-process step finished its file (rename, duplicate, keep-both).</summary>
    public void InProcessCompleted(int stepIndex)
    {
        var state = StateOf(stepIndex);
        state.Started = true;
        state.InProcessSucceeded = true;
        for (var i = 0; i < state.Status.Length; i++)
        {
            if (state.Status[i] != FileStatus.Completed)
            {
                state.Complete(i);
            }
        }
    }

    /// <summary>
    /// An in-process rename failed with ERROR_NOT_SAME_DEVICE (<see cref="StepOutcome.ReplanAsMove"/>):
    /// the item is planned again as a robocopy move, so this step is neither done nor failed.
    /// Call instead of <see cref="InProcessCompleted"/>, before <see cref="StepFinished"/>.
    /// </summary>
    public void InProcessReplannedAsMove(int stepIndex)
    {
        var state = StateOf(stepIndex);
        state.Started = true;
        state.ReplannedAsMove = true;
    }

    /// <summary>
    /// A move-mode keep-both rename turned out to cross volumes: robocopy cannot write under
    /// the keep-both name, so the item is refused (a plan issue), never retried. Call instead
    /// of <see cref="InProcessCompleted"/>, before <see cref="StepFinished"/>.
    /// </summary>
    public void InProcessRefused(int stepIndex)
    {
        var state = StateOf(stepIndex);
        state.Started = true;
        state.Refused = true;
    }

    /// <param name="killedByCancel">The app killed the run because the user canceled.</param>
    public void StepFinished(int stepIndex, RobocopyExitCode? exitCode, bool killedByCancel)
    {
        var state = StateOf(stepIndex);
        state.Finished = true;
        state.KilledByCancel = killedByCancel;

        FileStatus outcome;
        if (killedByCancel)
        {
            outcome = FileStatus.CanceledInFlight;
        }
        else if (!state.IsRobocopy)
        {
            outcome = FileStatus.Failed;
        }
        else if (exitCode is not { } code || code.Value < 0 || code.Value >= 16)
        {
            // Never ran, died, or fatal: robocopy may have stopped anywhere, including inside
            // a pre-allocated full-length file, so nothing unreported can count as done.
            outcome = FileStatus.Failed;
        }
        else
        {
            outcome = FileStatus.SkippedLateArrival;
        }

        for (var i = 0; i < state.Status.Length; i++)
        {
            if (state.Status[i] == FileStatus.Pending)
            {
                state.Status[i] = outcome;
            }
        }
    }

    /// <summary>Normalized source paths reported complete, for cancel cleanup.</summary>
    public IReadOnlyCollection<string> CompletedSources
    {
        get
        {
            var completed = new HashSet<string>(WinPath.Comparer);
            foreach (var state in _steps)
            {
                for (var i = 0; i < state.Status.Length; i++)
                {
                    if (state.Status[i] == FileStatus.Completed)
                    {
                        completed.Add(state.NormalizedSources[i]);
                    }
                }
            }
            return completed;
        }
    }

    /// <summary>Planned files of the robocopy steps that started: cancel cleanup's candidates.</summary>
    public IReadOnlyList<PlannedFile> StartedRobocopyFiles =>
        _steps.Where(s => s.IsRobocopy && s.Started).SelectMany(s => s.Files).ToList();

    /// <summary>Bytes of completed files in steps that have finished: the base for observed bytes.</summary>
    public long CompletedBytesOfFinishedSteps => _steps.Where(s => s.Finished).Sum(s => s.CompletedBytes);

    /// <summary>
    /// Files of robocopy steps that "Try again" should repeat. Never a file of a rename,
    /// duplicate or keep-both step: robocopy would write it under its source name, with
    /// policy Replace, over the file the user chose to keep. Empty once
    /// <see cref="PathsUnreliable"/>: the host then offers a full re-run of the paste
    /// (JobManager.Rerun: a new scan that asks about every file now present) instead.
    /// </summary>
    public IReadOnlyList<(int StepIndex, PlannedFile File)> Retryable
    {
        get
        {
            var retryable = new List<(int, PlannedFile)>();
            if (_pathsUnreliable)
            {
                // The ledger cannot tell finished files from partial ones any more.
                return retryable;
            }
            for (var s = 0; s < _steps.Length; s++)
            {
                var state = _steps[s];
                if (!state.IsRobocopy)
                {
                    continue;
                }
                for (var i = 0; i < state.Status.Length; i++)
                {
                    if (state.Status[i] == FileStatus.Failed)
                    {
                        retryable.Add((s, state.Files[i]));
                    }
                }
            }
            return retryable;
        }
    }

    /// <summary>
    /// In-process steps that failed, for "Try again" to repeat whole. Not counted: steps
    /// re-planned as a move (<see cref="InProcessReplannedAsMove"/>), refused ones
    /// (<see cref="InProcessRefused"/>) and steps a cancel interrupted.
    /// </summary>
    public IReadOnlyList<int> FailedInProcessSteps
    {
        get
        {
            var failed = new List<int>();
            for (var s = 0; s < _steps.Length; s++)
            {
                var state = _steps[s];
                if (!state.IsRobocopy && state.Finished && !state.InProcessSucceeded && !state.ReplannedAsMove && !state.Refused && !state.KilledByCancel)
                {
                    failed.Add(s);
                }
            }
            return failed;
        }
    }

    /// <summary>Files left alone because their name appeared at the destination during the run.</summary>
    public IReadOnlyList<PlannedFile> SkippedLateArrivals =>
        _steps.SelectMany(s => s.Files.Where((_, i) => s.Status[i] == FileStatus.SkippedLateArrival)).ToList();

    private StepState StateOf(int stepIndex)
    {
        if ((uint)stepIndex >= (uint)_steps.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(stepIndex));
        }
        return _steps[stepIndex];
    }

    private LedgerUpdate ApplyFile(StepState state, FileReported reported)
    {
        if (!state.BySource.TryGetValue(WinPath.NormalizeForMatch(reported.Path), out var index))
        {
            _pathsUnreliable = true;
            return NoChange;
        }
        switch (state.Status[index])
        {
            case FileStatus.Completed:
                // Reported twice: counted once.
                return NoChange;
            case FileStatus.Failed:
                // Its ERROR came first. A file robocopy said failed is never counted as done:
                // it may be a pre-allocated full-length partial.
                return NoChange;
            default:
                state.Complete(index);
                return new LedgerUpdate(state.Files[index], null, null);
        }
    }

    private LedgerUpdate ApplyError(StepState state, ErrorReported error)
    {
        var path = WinPath.NormalizeForMatch(error.Path);
        if (state.BySource.TryGetValue(path, out var index) || state.ByDestination.TryGetValue(path, out index))
        {
            var wasCompleted = state.Status[index] == FileStatus.Completed;
            state.Fail(index);
            return new LedgerUpdate(null, wasCompleted ? state.Files[index] : null, error);
        }

        // A directory: everything planned below it that has not completed failed with it.
        foreach (var below in state.FilesUnder(path))
        {
            if (state.Status[below] != FileStatus.Completed)
            {
                state.Fail(below);
            }
        }
        return new LedgerUpdate(null, null, error);
    }

    private enum FileStatus : byte
    {
        Pending,
        Completed,

        /// <summary>Robocopy reported an error, or its run died, failed fatally or never ran: retryable.</summary>
        Failed,

        /// <summary>Unfinished when the user's cancel killed the run: cancel cleanup's business, never retry's.</summary>
        CanceledInFlight,

        /// <summary>The run ended normally without reporting it: skipped by Ask/Skip flags because its name appeared.</summary>
        SkippedLateArrival,
    }

    private sealed class StepState
    {
        private (string Path, int Index)[]? _sortedSources;
        private (string Path, int Index)[]? _sortedDestinations;
        private Dictionary<string, int>? _byDestination;

        public StepState(ExecutionStep step)
        {
            Files = step.Files;
            IsRobocopy = step.Step is RobocopyStep;
            Status = new FileStatus[Files.Count];
            NormalizedSources = Files.Select(f => WinPath.NormalizeForMatch(f.SourcePath)).ToArray();
            BySource = new Dictionary<string, int>(Files.Count, WinPath.Comparer);
            for (var i = 0; i < NormalizedSources.Length; i++)
            {
                BySource.TryAdd(NormalizedSources[i], i);
            }
        }

        public IReadOnlyList<PlannedFile> Files { get; }

        public bool IsRobocopy { get; }

        public FileStatus[] Status { get; }

        public string[] NormalizedSources { get; }

        public Dictionary<string, int> BySource { get; }

        /// <summary>Built on the first error that names no planned source; most runs never need it.</summary>
        public Dictionary<string, int> ByDestination => _byDestination ??= BuildByDestination();

        public long CompletedBytes { get; private set; }

        public bool Started { get; set; }

        public bool Finished { get; set; }

        public bool KilledByCancel { get; set; }

        public bool InProcessSucceeded { get; set; }

        public bool ReplannedAsMove { get; set; }

        public bool Refused { get; set; }

        public void Complete(int index)
        {
            Status[index] = FileStatus.Completed;
            CompletedBytes += Files[index].Source.Size;
        }

        public void Fail(int index)
        {
            if (Status[index] == FileStatus.Completed)
            {
                CompletedBytes -= Files[index].Source.Size;
            }
            Status[index] = FileStatus.Failed;
        }

        /// <summary>
        /// Files strictly below <paramref name="directory"/> on either side. Sorted ordinal-
        /// ignore-case, every path sharing a prefix is one contiguous run, so each lookup is
        /// a binary search instead of a pass over the whole step: a full disk can fail every
        /// folder of a large tree, one ERROR line each.
        /// </summary>
        public IEnumerable<int> FilesUnder(string directory)
        {
            var prefix = directory.EndsWith(WinPath.Separator) ? directory : directory + WinPath.Separator;
            _sortedSources ??= Sorted(NormalizedSources);
            _sortedDestinations ??= Sorted(Files.Select(f => WinPath.NormalizeForMatch(f.DestinationPath)).ToArray());
            return InRange(_sortedSources, prefix).Concat(InRange(_sortedDestinations, prefix)).Distinct();
        }

        private Dictionary<string, int> BuildByDestination()
        {
            var map = new Dictionary<string, int>(Files.Count, WinPath.Comparer);
            for (var i = 0; i < Files.Count; i++)
            {
                map.TryAdd(WinPath.NormalizeForMatch(Files[i].DestinationPath), i);
            }
            return map;
        }

        private static (string Path, int Index)[] Sorted(string[] paths)
        {
            var sorted = paths.Select((path, index) => (path, index)).ToArray();
            Array.Sort(sorted, (a, b) => WinPath.Comparer.Compare(a.path, b.path));
            return sorted;
        }

        private static IEnumerable<int> InRange((string Path, int Index)[] sorted, string prefix)
        {
            // Lower bound: the first path not ordered before the prefix.
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (WinPath.Comparer.Compare(sorted[mid].Path, prefix) < 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            for (var i = low; i < sorted.Length && sorted[i].Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++)
            {
                yield return sorted[i].Index;
            }
        }
    }
}
