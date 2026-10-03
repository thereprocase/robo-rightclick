namespace RoboRightClick.Core;

/// <summary>What one robocopy event meant for the job.</summary>
/// <param name="Completed">The planned file this event completed, if any (its size feeds JobProgress).</param>
/// <param name="Uncompleted">A file that had been counted complete and an ERROR for it arrived later; subtract it.</param>
public sealed record LedgerUpdate(PlannedFile? Completed, PlannedFile? Uncompleted, ErrorReported? Error)
{
    /// <summary>
    /// With robocopy's own retries on, a file whose ERROR came earlier and which robocopy then
    /// reported copied (<see cref="Completed"/> is the same file). Its earlier errors no longer
    /// stand: the job takes them off its error list.
    /// </summary>
    public PlannedFile? Recovered { get; init; }
}

/// <summary>A file "Try again" repeats, and whether the retry may overwrite what is at its destination.</summary>
/// <param name="MayOverwrite">
/// True only where this paste may itself have left the destination file: robocopy reported an
/// error naming the file (so it reached it) and the destination did not exist when its step
/// started, or the user's answer for that step already was to overwrite. Anything else (a
/// folder-level error, a run that died before reaching the file, a file nobody proved robocopy
/// wrote) must not overwrite unasked: the file there may be someone else's, saved after the
/// paste. Those run with Skip flags, and the retry's scan treats any that exist as conflicts:
/// settled by the configured conflict policy (the prompt only under Ask), except a file this
/// paste may have left half written (<see cref="StepLedger.SuspectedPartials"/>), which is
/// always asked about (<see cref="FileConflict.SuspectedPartial"/>).
/// </param>
public sealed record RetryCandidate(int StepIndex, PlannedFile File, bool MayOverwrite);

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
/// and otherwise unreported: robocopy said nothing about them in a run that ended normally.
/// Until the host checks them (<see cref="ResolveUnreported"/>) they count as skipped late
/// arrivals, except under policy Replace, whose flags skip nothing, where they count as
/// failed. The check sorts them by what is on disk: a source that is gone was not copied
/// because it vanished (<see cref="SourcesGone"/>); a destination that exists, under a policy
/// with Skip flags, is a late arrival robocopy left alone; anything else failed.</para>
/// <para>Robocopy's own retries (/R:n above 0): it prints the ERROR, waits, tries again, and
/// prints the file's line when an attempt succeeds. The parser drops a file line that its
/// ERROR follows, so a file line that reaches the ledger after the file's ERROR is the
/// success of a later attempt (LIKELY ordering; no capture with /R above 0 yet). With
/// retries on, the last word wins: such a line completes the file again and
/// <see cref="LedgerUpdate.Recovered"/> tells the job to drop its errors. With /R:0 there is
/// no later attempt, and a file robocopy said failed is never counted as done.</para>
/// <para>Only robocopy steps' files are ever <see cref="Retryable"/>: robocopy cannot write
/// a file under a new name, so a failed rename, duplicate or keep-both is repeated as a
/// whole step through <see cref="FailedInProcessSteps"/> instead.</para>
/// </remarks>
public sealed class StepLedger
{
    private static readonly LedgerUpdate NoChange = new(null, null, null);

    private readonly StepState[] _steps;
    private readonly bool _robocopyRetries;
    private bool _pathsUnreliable;

    // Kept up to date as steps finish, so reading it at every step start costs nothing.
    private long _completedBytesOfFinishedSteps;

    /// <param name="robocopyRetries">The runs use /R above 0 (<see cref="Settings.Retries"/>); see the remarks.</param>
    public StepLedger(ExecutionPlan plan, bool robocopyRetries = false)
    {
        Plan = plan;
        _robocopyRetries = robocopyRetries;
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
        state.Ran = true;
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
                Complete(state, i);
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
        if (!state.Finished)
        {
            state.Finished = true;
            _completedBytesOfFinishedSteps += state.CompletedBytes;
        }
        state.KilledByCancel = killedByCancel;
        if (state.IsRobocopy && exitCode is not null)
        {
            state.Ran = true;
        }

        FileStatus outcome;
        var unreported = false;
        if (killedByCancel)
        {
            outcome = FileStatus.CanceledInFlight;
        }
        else if (!state.IsRobocopy)
        {
            outcome = FileStatus.FailedUnreached;
        }
        else if (exitCode is not { } code || code.Value < 0 || code.Value >= 16)
        {
            // Never ran, died, or fatal: robocopy may have stopped anywhere, including inside
            // a pre-allocated full-length file, so nothing unreported can count as done.
            outcome = FileStatus.FailedUnreached;
        }
        else
        {
            // Robocopy's Skip flags explain the silence; Replace's flags skip nothing.
            outcome = state.Policy == ConflictPolicy.Replace ? FileStatus.FailedUnreached : FileStatus.SkippedLateArrival;
            unreported = true;
        }

        for (var i = 0; i < state.Status.Length; i++)
        {
            if (state.Status[i] == FileStatus.Pending)
            {
                state.Status[i] = outcome;
                if (unreported)
                {
                    (state.Unreported ??= []).Add(i);
                }
            }
        }
    }

    /// <summary>
    /// Files of a robocopy step that ended normally which robocopy never mentioned. The host
    /// checks each one's source and destination (off the job's lock) and passes the answers to
    /// <see cref="ResolveUnreported"/>.
    /// </summary>
    public IReadOnlyList<PlannedFile> Unreported(int stepIndex)
    {
        var state = StateOf(stepIndex);
        return state.Unreported is { } indices ? indices.Select(i => state.Files[i]).ToList() : [];
    }

    /// <summary>
    /// Sorts a step's unreported files (see the remarks) by what the host found on disk after
    /// the run. Only files still unreported are touched; calling it twice changes nothing more.
    /// Returns the files it found failed: robocopy neither copied them nor said why, so the job
    /// lists each as an error (<see cref="UnreportedFailure"/>) and does not end Done.
    /// </summary>
    /// <param name="sourceExists">Whether the file's source is still there.</param>
    /// <param name="destinationExists">Whether something is at the file's destination.</param>
    public IReadOnlyList<PlannedFile> ResolveUnreported(int stepIndex, Func<PlannedFile, bool> sourceExists, Func<PlannedFile, bool> destinationExists)
    {
        var state = StateOf(stepIndex);
        if (state.Unreported is not { } indices)
        {
            return [];
        }
        state.Unreported = null;
        var failed = new List<PlannedFile>();
        foreach (var i in indices)
        {
            if (state.Status[i] is not (FileStatus.SkippedLateArrival or FileStatus.FailedUnreached))
            {
                continue;
            }
            var file = state.Files[i];
            state.Status[i] = !sourceExists(file) ? FileStatus.SourceGone
                : state.Policy != ConflictPolicy.Replace && destinationExists(file) ? FileStatus.SkippedLateArrival
                : FileStatus.FailedUnreached;
            if (state.Status[i] == FileStatus.FailedUnreached)
            {
                failed.Add(file);
            }
        }
        return failed;
    }

    /// <summary>The message of <see cref="UnreportedFailure"/>. Path-free, like every message the summary shows.</summary>
    public const string UnreportedFailureMessage = "Robocopy ended without copying this file and without reporting an error for it.";

    /// <summary>
    /// The error the job lists for a file <see cref="ResolveUnreported"/> found failed. Code 0:
    /// robocopy gave none. Named by its source, as robocopy names the files it fails on.
    /// </summary>
    public static ErrorReported UnreportedFailure(PlannedFile file) => new(0, "Copying File", file.SourcePath, UnreportedFailureMessage);

    /// <summary>
    /// How many files of robocopy steps robocopy did not finish, as the ledger stands: reported
    /// failing, never reached, left by a run that died, or found failed by
    /// <see cref="ResolveUnreported"/>. Counted whether or not <see cref="PathsUnreliable"/>:
    /// <see cref="JobOutcome.FinalState"/> reads it, and a job with any such file never ends
    /// Done ("Everything was copied") however its runs exited.
    /// </summary>
    public int FailedRobocopyFileCount
    {
        get
        {
            var count = 0;
            foreach (var state in _steps)
            {
                if (state.IsRobocopy)
                {
                    count += state.Status.Count(IsFailed);
                }
            }
            return count;
        }
    }

    /// <summary>
    /// The files of a robocopy step that ran and did not finish them, for the host's look at
    /// their destinations after the run (<see cref="RecordAbsentAfterRun"/>). Empty for a step
    /// robocopy never ran.
    /// </summary>
    public IReadOnlyList<PlannedFile> FailedAfterRun(int stepIndex)
    {
        var state = StateOf(stepIndex);
        if (!state.IsRobocopy || !state.Ran)
        {
            return [];
        }
        var files = new List<PlannedFile>();
        for (var i = 0; i < state.Status.Length; i++)
        {
            if (IsFailed(state.Status[i]))
            {
                files.Add(state.Files[i]);
            }
        }
        return files;
    }

    /// <summary>
    /// The host's look after a run: which of <see cref="FailedAfterRun"/>'s destinations it
    /// proved absent (one listing per folder). Nothing there means robocopy wrote nothing there,
    /// so the file is not <see cref="SuspectedPartials"/>. Only proof counts: a folder that
    /// could not be listed, or a step nobody looked at, leaves its files possibly incomplete.
    /// </summary>
    public void RecordAbsentAfterRun(int stepIndex, Func<PlannedFile, bool> destinationAbsent)
    {
        var state = StateOf(stepIndex);
        for (var i = 0; i < state.Status.Length; i++)
        {
            if (IsFailed(state.Status[i]) && destinationAbsent(state.Files[i]))
            {
                (state.AbsentAfterRun ??= []).Add(i);
            }
        }
    }

    private static bool IsFailed(FileStatus status) => status is FileStatus.FailedReported or FileStatus.FailedUnreached;

    /// <summary>
    /// Files robocopy did not copy because their source was gone when it got to them (a temp
    /// file deleted mid-paste, a source a cut had already moved). The job reports each as a
    /// refusal with <see cref="PastePlanner.MissingReason"/>; nothing is retried.
    /// </summary>
    public IReadOnlyList<PlannedFile> SourcesGone =>
        _steps.SelectMany(s => s.Files.Where((_, i) => s.Status[i] == FileStatus.SourceGone)).ToList();

    /// <summary>
    /// Destinations that may hold part of a file, decided per step whatever the job's end state:
    /// files of robocopy runs that actually ran (they produced output or an exit code) and
    /// failed or died without finishing them, where robocopy may actually have written: the
    /// destination was free when the step started, or the step's policy overwrites (a file that
    /// was there before under Skip flags is the user's, untouched by robocopy), and it was not
    /// proved absent after the run (<see cref="RecordAbsentAfterRun"/>: a run that failed on the
    /// destination folder wrote nothing). Robocopy allocates a file at full length before
    /// writing it, so these can look complete. The one list for both uses: the summary's "may
    /// be incomplete" section, and what a retry child always asks about rather than letting a
    /// configured Skip or KeepNewer keep it (<see cref="FileConflict.SuspectedPartial"/>).
    /// </summary>
    /// <param name="presentBeforeStep">As for <see cref="RetryCandidates"/>, by normalized path.</param>
    public IReadOnlyList<PlannedFile> SuspectedPartials(Func<string, bool> presentBeforeStep)
    {
        var files = new List<PlannedFile>();
        foreach (var state in _steps)
        {
            if (!state.IsRobocopy || !state.Ran)
            {
                continue;
            }
            var overwrites = RobocopyArgs.MayOverwriteExisting(state.Policy);
            for (var i = 0; i < state.Status.Length; i++)
            {
                if (!IsFailed(state.Status[i]) || state.AbsentAfterRun?.Contains(i) == true)
                {
                    continue;
                }
                var file = state.Files[i];
                if (overwrites || !presentBeforeStep(WinPath.NormalizeForMatch(file.DestinationPath)))
                {
                    files.Add(file);
                }
            }
        }
        return files;
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

    /// <summary>
    /// Cancel cleanup's candidates: the files of the robocopy runs the user's cancel killed
    /// that had not completed, each with its step's policy. A run that ended on its own is
    /// left out entirely: whatever it did not report it skipped (a late arrival, someone
    /// else's file) or failed on, and neither is a partial copy of this cancel.
    /// </summary>
    public IReadOnlyList<KilledRunFile> KilledRunFiles
    {
        get
        {
            var files = new List<KilledRunFile>();
            foreach (var state in _steps)
            {
                if (!state.IsRobocopy || !state.KilledByCancel)
                {
                    continue;
                }
                for (var i = 0; i < state.Status.Length; i++)
                {
                    if (state.Status[i] != FileStatus.Completed)
                    {
                        files.Add(new KilledRunFile(state.Files[i], state.Policy));
                    }
                }
            }
            return files;
        }
    }

    /// <summary>
    /// The files of a step not reported complete so far: what the host looks at while the run
    /// is suspended for a cancel, to see which of them robocopy has open.
    /// </summary>
    public IReadOnlyList<PlannedFile> UnfinishedFiles(int stepIndex)
    {
        var state = StateOf(stepIndex);
        var files = new List<PlannedFile>();
        for (var i = 0; i < state.Status.Length; i++)
        {
            if (state.Status[i] != FileStatus.Completed)
            {
                files.Add(state.Files[i]);
            }
        }
        return files;
    }

    /// <summary>Bytes of completed files in steps that have finished: the base for observed bytes. O(1).</summary>
    public long CompletedBytesOfFinishedSteps => _completedBytesOfFinishedSteps;

    /// <summary>
    /// Files of robocopy steps that "Try again" should repeat. Never a file of a rename,
    /// duplicate or keep-both step: robocopy would write it under its source name, with
    /// policy Replace, over the file the user chose to keep. Empty once
    /// <see cref="PathsUnreliable"/>: the host then offers a full re-run of the paste
    /// (JobManager.Rerun: a new scan in which every file now present is a conflict for the
    /// configured policy, and a suspected partial copy is always asked about) instead.
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
                    if (state.Status[i] is FileStatus.FailedReported or FileStatus.FailedUnreached)
                    {
                        retryable.Add((s, state.Files[i]));
                    }
                }
            }
            return retryable;
        }
    }

    /// <summary>
    /// <see cref="Retryable"/>, each with whether the retry may overwrite its destination
    /// (<see cref="RetryCandidate.MayOverwrite"/>).
    /// </summary>
    /// <param name="presentBeforeStep">
    /// Whether a destination existed when its step started (the scan's conflicts plus the
    /// job's re-check before each step), by <see cref="WinPath.NormalizeForMatch"/> path.
    /// </param>
    public IReadOnlyList<RetryCandidate> RetryCandidates(Func<string, bool> presentBeforeStep)
    {
        var candidates = new List<RetryCandidate>();
        foreach (var (stepIndex, file) in Retryable)
        {
            var state = _steps[stepIndex];
            var reported = state.Status[state.BySource[WinPath.NormalizeForMatch(file.SourcePath)]] == FileStatus.FailedReported;
            var mayOverwrite = reported
                && (RobocopyArgs.MayOverwriteExisting(state.Policy) || !presentBeforeStep(WinPath.NormalizeForMatch(file.DestinationPath)));
            candidates.Add(new RetryCandidate(stepIndex, file, mayOverwrite));
        }
        return candidates;
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
            case FileStatus.FailedReported or FileStatus.FailedUnreached when _robocopyRetries:
                // A later attempt of robocopy's own retry succeeded (see the remarks).
                var hadError = state.Status[index] == FileStatus.FailedReported;
                Complete(state, index);
                return new LedgerUpdate(state.Files[index], null, null) { Recovered = hadError ? state.Files[index] : null };
            case FileStatus.FailedReported or FileStatus.FailedUnreached:
                // Its ERROR came first. A file robocopy said failed is never counted as done:
                // it may be a pre-allocated full-length partial.
                return NoChange;
            default:
                Complete(state, index);
                return new LedgerUpdate(state.Files[index], null, null);
        }
    }

    /// <summary>
    /// <paramref name="errors"/> without those robocopy's own retry overcame, so a recovered
    /// file does not make the step count as failed: an error naming a file of the step that
    /// robocopy then copied after all, and a folder-level error (creating or scanning a
    /// folder) once every planned file of the step under that folder completed
    /// (<see cref="RecoveredFolderErrors"/>). Nothing is dropped with retries off: a failed
    /// file is never completed then.
    /// </summary>
    public IReadOnlyList<ErrorReported> StandingErrors(int stepIndex, IReadOnlyList<ErrorReported> errors)
    {
        var state = StateOf(stepIndex);
        return errors.Where(error =>
        {
            var path = WinPath.NormalizeForMatch(error.Path);
            var found = state.BySource.TryGetValue(path, out var index) || state.ByDestination.TryGetValue(path, out index);
            return found ? state.Status[index] != FileStatus.Completed : !FolderRecovered(state, path);
        }).ToList();
    }

    /// <summary>
    /// The folder-level errors of <paramref name="errors"/> that robocopy's own retry overcame:
    /// every planned file of the step under the folder completed. A folder with no planned
    /// file under it (an empty folder robocopy could not create) is not recovered: nothing
    /// shows the folder was made. The job takes these off its error list; file errors it
    /// already took off when their file's line arrived (<see cref="LedgerUpdate.Recovered"/>).
    /// </summary>
    public IReadOnlyList<ErrorReported> RecoveredFolderErrors(int stepIndex, IReadOnlyList<ErrorReported> errors)
    {
        var state = StateOf(stepIndex);
        return errors.Where(error =>
        {
            var path = WinPath.NormalizeForMatch(error.Path);
            return !state.BySource.ContainsKey(path) && !state.ByDestination.ContainsKey(path) && FolderRecovered(state, path);
        }).ToList();
    }

    private static bool FolderRecovered(StepState state, string folder)
    {
        var any = false;
        foreach (var index in state.FilesUnder(folder))
        {
            if (state.Status[index] != FileStatus.Completed)
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    private void Complete(StepState state, int index)
    {
        state.Complete(index);
        if (state.Finished)
        {
            _completedBytesOfFinishedSteps += state.Files[index].Source.Size;
        }
    }

    private void Fail(StepState state, int index, FileStatus status)
    {
        if (state.Status[index] == FileStatus.Completed && state.Finished)
        {
            _completedBytesOfFinishedSteps -= state.Files[index].Source.Size;
        }
        state.Fail(index, status);
    }

    private LedgerUpdate ApplyError(StepState state, ErrorReported error)
    {
        var path = WinPath.NormalizeForMatch(error.Path);
        if (state.BySource.TryGetValue(path, out var index) || state.ByDestination.TryGetValue(path, out index))
        {
            if (state.Status[index] == FileStatus.FailedReported)
            {
                // Robocopy's own retry failed again: one file, one error, so "Try again (N)"
                // counts files and a later recovery has exactly one error to take back.
                return NoChange;
            }
            var wasCompleted = state.Status[index] == FileStatus.Completed;
            Fail(state, index, FileStatus.FailedReported);
            return new LedgerUpdate(null, wasCompleted ? state.Files[index] : null, error);
        }

        // A directory: everything planned below it that has not completed failed with it.
        // Robocopy never reached those files, so they are not proof it wrote anything.
        foreach (var below in state.FilesUnder(path))
        {
            if (state.Status[below] is not (FileStatus.Completed or FileStatus.FailedReported))
            {
                Fail(state, below, FileStatus.FailedUnreached);
            }
        }
        return new LedgerUpdate(null, null, error);
    }

    private enum FileStatus : byte
    {
        Pending,
        Completed,

        /// <summary>Robocopy reported an error naming this file: retryable, and robocopy reached it.</summary>
        FailedReported,

        /// <summary>A folder-level error, or the run died, failed fatally or never ran before reporting it: retryable.</summary>
        FailedUnreached,

        /// <summary>Unfinished when the user's cancel killed the run: cancel cleanup's business, never retry's.</summary>
        CanceledInFlight,

        /// <summary>The run ended normally without reporting it: skipped by Ask/Skip flags because its name appeared.</summary>
        SkippedLateArrival,

        /// <summary>The run ended normally without reporting it, and its source was gone afterwards.</summary>
        SourceGone,
    }

    private sealed class StepState
    {
        private (string Path, int Index)[]? _sortedSources;
        private (string Path, int Index)[]? _sortedDestinations;
        private Dictionary<string, int>? _byDestination;

        public StepState(ExecutionStep step)
        {
            Files = step.Files;
            Policy = step.Policy;
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

        public ConflictPolicy Policy { get; }

        public bool IsRobocopy { get; }

        public FileStatus[] Status { get; }

        public string[] NormalizedSources { get; }

        public Dictionary<string, int> BySource { get; }

        /// <summary>Built on the first error that names no planned source; most runs never need it.</summary>
        public Dictionary<string, int> ByDestination => _byDestination ??= BuildByDestination();

        public long CompletedBytes { get; private set; }

        public bool Started { get; set; }

        /// <summary>Robocopy itself ran for this step: it produced output or an exit code.</summary>
        public bool Ran { get; set; }

        /// <summary>Files <see cref="StepFinished"/> found unreported after a normal exit, until resolved.</summary>
        public List<int>? Unreported { get; set; }

        /// <summary>Failed files whose destination the host proved absent after the run (<see cref="RecordAbsentAfterRun"/>).</summary>
        public HashSet<int>? AbsentAfterRun { get; set; }

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

        public void Fail(int index, FileStatus status)
        {
            if (Status[index] == FileStatus.Completed)
            {
                CompletedBytes -= Files[index].Source.Size;
            }
            Status[index] = status;
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
