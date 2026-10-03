using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>What a job asks of the rest of this session's jobs.</summary>
internal interface IDestinationClaims
{
    /// <summary>
    /// True when a job other than <paramref name="askingJob"/> planned or wrote
    /// <paramref name="destinationPath"/> this session. Cancel cleanup never deletes such a file.
    /// </summary>
    bool ClaimedByOtherJob(Guid askingJob, string destinationPath);
}

/// <summary>Shared services a job uses; one instance for the app.</summary>
/// <param name="ClearClipboardIfUnchanged">Posts to the UI thread (the clipboard owner window lives there); returns false without posting once shutdown has begun.</param>
internal sealed record JobServices(
    IJobPrompts Prompts,
    FileSystemFacts FileSystem,
    ProgressSampler Sampler,
    TimeProvider Time,
    IDestinationClaims Claims,
    Func<uint, Task<bool>> ClearClipboardIfUnchanged);

/// <summary>
/// Everything fixed when a job is created. Settings and the logging mode are captured here,
/// so a later change (including the ephemeral toggle) affects only new jobs.
/// </summary>
/// <param name="Order">The paste exactly as it arrived; resolved on the worker thread in Scanning.</param>
/// <param name="RetryPlan">Set for a "Try again" child: Scanning only refreshes totals and presence, and never prompts.</param>
/// <param name="Logging">For a derived job, <see cref="JobSinks.ForDerivedJob"/>; otherwise the current mode.</param>
/// <param name="CutClipboardSequence">For a paste of cut data: the clipboard sequence number read at paste time.</param>
/// <param name="StartPaused">"Pause all" is on: the gate starts closed.</param>
internal sealed record JobStart(
    Guid Id,
    Guid? ParentId,
    PasteOrder Order,
    ExecutionPlan? RetryPlan,
    Settings Settings,
    LoggingMode Logging,
    IJobSink Sink,
    uint? CutClipboardSequence,
    bool StartPaused,
    DateTimeOffset CreatedAt);

/// <summary>
/// One paste, from Queued to a terminal state. <see cref="RunAsync"/> runs on worker
/// threads (the scan on a dedicated LongRunning thread); control methods and
/// <see cref="Snapshot"/> may be called from any thread and synchronize on one private lock
/// that guards the lifecycle, the <see cref="StepLedger"/>, progress and the error list.
/// Nothing is called while holding that lock: not the sink, not events, not the UI.
/// </summary>
/// <remarks>
/// <para>Scanning: PastePlanner.Plan(order, facts) (validation, kinds, links, guards),
/// then JobScanner.Scan with progress into the snapshot's totals. A retry child scans its
/// <see cref="JobStart.RetryPlan"/> files only for totals and presence.</para>
/// <para>AwaitingDecision when there are conflicts and the policy is Ask (IJobPrompts);
/// null = Canceled. Then ExecutionPlanner.Apply. An <see cref="ExecutionPlan.IsNoOp"/> plan
/// goes Scanning → Finalizing and ends Done with <see cref="JobSnapshot.NoOp"/> (no toast,
/// no clipboard clear). Everything refused also goes Scanning → Finalizing.</para>
/// <para>Running: steps in order. Before each step, re-check which of its destinations
/// exist (one listing per destination folder) and add them to the presence set that cancel
/// cleanup reads. RenameStep and move-mode KeepBothStep by InProcessCopier.Rename (a
/// <see cref="StepOutcome.ReplanAsMove"/> item is scanned again on its own and appended as a
/// robocopy move with policy Skip); DuplicateFileStep and copy-mode KeepBothStep by
/// InProcessCopier.CopyFileAsync; RobocopyStep by RobocopyRun with
/// RobocopyArgs.Build(step, DriveMedia.ThreadsFor(...).ApplyTo(settings), policy,
/// PipeNames.ForStep(id, index, CSPRNG nonce)): the /MT count is chosen per run from both
/// ends' drives, and the sink gets a "# threads" line after the command line.
/// Events go through the ledger; observed bytes = ledger.CompletedBytesOfFinishedSteps +
/// this run's read counter, from callbacks of the current run only. ShellNotify after each
/// step. A closed gate before Running moves straight on to Paused; the loop waits on the
/// gate between steps and before Finalizing; JobProgress.ResetRate on resume.</para>
/// <para>Cancel (ignored once Finalizing): CancelRequested in the snapshot at once; suspend
/// robocopy, record which unfinished destinations exist and which of them it holds open
/// (<see cref="ObserveAtKill"/>), kill and wait; then CancelCleanup.Select(ledger.KilledRunFiles,
/// ledger.CompletedSources, presence set, those observations, move, existence checks,
/// Claims.ClaimedByOtherJob), skipped entirely when ledger.PathsUnreliable. Each delete goes
/// through <see cref="ProcessNative.DeleteFileIfSameFile"/>, which checks identity and
/// attributes and deletes on one handle opened without following links; LeftInPlace becomes
/// <see cref="JobSnapshot.DamagedOnCancel"/> and <see cref="DamagedPaths"/>.</para>
/// <para>Finalizing: create LinkFolders empty, clear the clipboard for a cut that ended
/// Done with at least one item moved, ShellNotify, emit the JobSummary (errors capped at
/// <see cref="JobRecords.MaxRecordedErrors"/>), then JobOutcome.FinalState. A Failed job's
/// <see cref="JobSnapshot.FailureReason"/> comes from <see cref="FailureText.Describe"/>.
/// Any unexpected exception, in any state, ends the job Failed.</para>
/// <para>The job never deletes a source: only robocopy /MOV(E) and renames move anything.</para>
/// <para>Threads. The manager may call <see cref="RunAsync"/> and every control method on
/// the UI thread, which must never touch the file system. So RunAsync only records Queued →
/// Scanning and hands the rest to the thread pool; control methods change state under the
/// lock and deliver sink calls and <see cref="StateChanged"/> from the thread pool; cancel
/// uses CancellationTokenSource.CancelAsync so no job continuation runs inline on the caller;
/// and tasks completed on the UI thread (the conflict prompt, the clipboard clear) are
/// resumed on the thread pool. Sink calls and <see cref="StateChanged"/> are delivered in
/// the order the transitions happened, by one thread at a time.</para>
/// <para>Two ledger parts. A <see cref="StepLedger"/> is built for one fixed plan, so the
/// robocopy moves that replace failed renames (<see cref="StepOutcome.ReplanAsMove"/>) get a
/// second part with its own ledger. Progress, cancel cleanup, retry and the summary read
/// both parts.</para>
/// </remarks>
internal sealed class Job
{
    /// <summary>Path-free on purpose: an exception message can contain a path.</summary>
    public const string UnexpectedFailureReason = "An unexpected error stopped this job.";

    /// <summary>
    /// A same-volume rename turned out to cross volumes (<see cref="StepOutcome.ReplanAsMove"/>)
    /// for an item robocopy cannot move: robocopy cannot rename, so a keep-both name cannot be
    /// kept, and an OS move would be an app-initiated source deletion (invariant 1).
    /// </summary>
    public const string CannotMoveUnderNewNameReason =
        "This item could not be moved under a new name, because the destination turned out to be on a different drive.";

    /// <summary>Robocopy's own operation name, so FailureText and the error summary treat these like its errors.</summary>
    private const string CreateFolderOperation = "Creating Destination Directory";

    private readonly Lock _lock = new();
    private readonly JobLifecycle _lifecycle;
    private readonly PauseGate _gate = new();
    private readonly CancellationTokenSource _cancel = new();

    // Sink calls and StateChanged, queued under the lock and delivered outside it in order.
    private readonly Queue<Notice> _notices = new();
    private bool _delivering;

    /// <summary>Bumped on every change a snapshot can show; read lock-free by the manager's snapshot cache.</summary>
    private long _version;

    // Snapshot state.
    private JobWait _wait;
    private bool _cancelRequested;
    private bool _acknowledged;
    private bool _noOp;
    private long _totalBytes;
    private long _totalFiles;
    private long _doneFiles;
    private long _doneFileBytes;
    private JobProgress? _progress;
    private readonly List<ErrorReported> _errors = [];
    private int _totalErrors;
    private int? _finalErrorCount;
    private int _damagedOnCancel;
    private string? _failureReason;
    private readonly List<PlanIssue> _issues = [];

    // Detail lists for the error summary, each capped at JobRecords.MaxRecordedErrors; the
    // counts are exact.
    private readonly List<string> _damagedPaths = [];
    private readonly List<string> _skippedAppearedPaths = [];
    private int _skippedAppeared;

    // Execution state. Released on a terminal state unless "Try again" still needs it.
    private ExecutionPlan? _plan;
    private readonly List<LedgerPart> _parts = [];
    private HashSet<string>? _presence;

    // What was seen at the killed run's destinations while robocopy was suspended for the
    // cancel, by normalized path. Cancel cleanup deletes only files robocopy held open then.
    private readonly Dictionary<string, KillObservation> _atKill = new(WinPath.Comparer);

    // Claims: normalized destinations this job planned. Kept for the job's life in history.
    private readonly HashSet<string> _claimedFiles = new(WinPath.Comparer);
    private readonly List<string> _claimedRoots = [];

    // The run whose callbacks are current; 0 = none. Callbacks of any other run are ignored.
    private int _runSerial;
    private int _activeRun;
    private long _observedBase;
    private Exception? _callbackFault;

    public Job(JobStart start, JobServices services)
    {
        Start = start;
        Services = services;
        Footprint = JobFootprint.Of(start.Order);
        _lifecycle = new JobLifecycle(start.CreatedAt);
        if (start.StartPaused)
        {
            _gate.Pause();
        }
    }

    public JobStart Start { get; }

    public JobServices Services { get; }

    public Guid Id => Start.Id;

    public JobFootprint Footprint { get; }

    public JobState State
    {
        get
        {
            lock (_lock)
            {
                return _lifecycle.State;
            }
        }
    }

    /// <summary>Changes whenever a snapshot of this job would differ. Lock-free.</summary>
    internal long Version => Interlocked.Read(ref _version);

    /// <summary>Executed plan, available once Running; the input to RetryPlanner. Released once the job ends with nothing to retry.</summary>
    public ExecutionPlan? Plan
    {
        get
        {
            lock (_lock)
            {
                return _plan;
            }
        }
    }

    /// <summary>
    /// Robocopy printed a path the ledger could not match, so "Try again" cannot tell which
    /// files failed (<see cref="StepLedger.Retryable"/> is empty); the manager re-runs the
    /// whole paste instead, which re-scans and asks about every file now present.
    /// </summary>
    public bool PathsUnreliable
    {
        get
        {
            lock (_lock)
            {
                return _parts.Any(p => p.Ledger.PathsUnreliable);
            }
        }
    }

    /// <summary>Per-file errors kept so far (first <see cref="JobRecords.MaxRecordedErrors"/>).</summary>
    public IReadOnlyList<ErrorReported> Errors
    {
        get
        {
            lock (_lock)
            {
                return _errors.ToArray();
            }
        }
    }

    /// <summary>
    /// Files "Try again" would repeat, and failed in-process steps. DoneWithErrors: the
    /// ledger's retryable files of robocopy steps plus the in-process steps that failed.
    /// Canceled with damage: the files the cancel left possibly incomplete ("Finish copying
    /// them"). Null when there is nothing to repeat.
    /// </summary>
    /// <remarks>
    /// Only robocopy steps' files are taken from <see cref="StepLedger.Retryable"/> (the
    /// ledger promises that, and this filters again): a failed keep-both or duplicate file
    /// retried as a robocopy step would be written under its source's name, with policy
    /// Replace, over the very file the user chose to keep. In-process failures are repeated
    /// as their own step from <see cref="StepLedger.FailedInProcessSteps"/>, which leaves out
    /// renames re-planned as moves and refused keep-both moves.
    /// </remarks>
    public ExecutionPlan? RetryPlan()
    {
        lock (_lock)
        {
            var state = _lifecycle.State;
            if (state is not (JobState.DoneWithErrors or JobState.Canceled))
            {
                return null;
            }

            ExecutionPlan? result = null;
            foreach (var part in _parts)
            {
                IReadOnlyList<(int StepIndex, PlannedFile File)> files;
                IReadOnlyList<int> failedInProcess;
                if (state == JobState.Canceled)
                {
                    files = part.Damaged;
                    failedInProcess = [];
                }
                else
                {
                    files = RobocopyRetryable(part);
                    failedInProcess = part.Ledger.FailedInProcessSteps;
                }
                if (files.Count == 0 && failedInProcess.Count == 0)
                {
                    continue;
                }
                result = Merge(result, RetryPlanner.ForFailures(part.Plan, files, failedInProcess));
            }
            return result;
        }
    }

    /// <summary>Items refused at planning or while running, each with its fixed, path-free reason.</summary>
    public IReadOnlyList<PlanIssue> Issues
    {
        get
        {
            lock (_lock)
            {
                return _issues.ToArray();
            }
        }
    }

    /// <summary>Destination files a cancel left partly replaced (first <see cref="JobRecords.MaxRecordedErrors"/>).</summary>
    public IReadOnlyList<string> DamagedPaths
    {
        get
        {
            lock (_lock)
            {
                return _damagedPaths.ToArray();
            }
        }
    }

    /// <summary>Destinations skipped because a file with that name appeared after the scan (first <see cref="JobRecords.MaxRecordedErrors"/>).</summary>
    public IReadOnlyList<string> SkippedAppearedPaths
    {
        get
        {
            lock (_lock)
            {
                return _skippedAppearedPaths.ToArray();
            }
        }
    }

    /// <summary>Destinations this job planned, for <see cref="IDestinationClaims"/>.</summary>
    public bool Claims(string destinationPath)
    {
        var path = WinPath.NormalizeForMatch(destinationPath);
        lock (_lock)
        {
            if (_claimedFiles.Contains(path))
            {
                return true;
            }
            // A rename moves a whole item; everything under its destination is this job's.
            return _claimedRoots.Any(root => WinPath.AreSame(path, root) || WinPath.IsStrictlyUnder(path, root));
        }
    }

    /// <summary>Raised from worker threads on state changes only; progress is pulled through <see cref="Snapshot"/>.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The job description every sink call refers to.</summary>
    internal static JobDescription DescriptionOf(Guid id, PasteOrder order, DateTimeOffset createdAt) =>
        new(id, order.Verb, order.Sources, order.Destination, createdAt);

    /// <summary>
    /// Queues <see cref="IJobSink.JobCreated"/> and the initial Queued state ahead of any
    /// transition, without delivering them. The manager calls this once, before the job can
    /// start, and calls <see cref="DeliverInBackground"/> only after registering it: a job the
    /// manager refuses (the same cut pasted twice) is dropped before its sink sees anything.
    /// </summary>
    internal void Announce()
    {
        lock (_lock)
        {
            _notices.Enqueue(new Notice(DescriptionOf(Id, Start.Order, Start.CreatedAt), _lifecycle.History[0], null));
        }
    }

    /// <summary>Delivers queued sink calls and <see cref="StateChanged"/> from the thread pool.</summary>
    internal void DeliverInBackground() => DeliverOnThreadPool();

    /// <summary>Why this job is still Queued (set by the manager's scheduling pass).</summary>
    internal void SetWait(JobWait wait)
    {
        lock (_lock)
        {
            var value = _lifecycle.State == JobState.Queued ? wait : JobWait.None;
            if (_wait != value)
            {
                _wait = value;
                Touch();
            }
        }
    }

    /// <summary>
    /// For the crash path: requests cancellation without taking any lock and without waiting.
    /// The robocopy processes also die with the app through their kill-on-close job object.
    /// </summary>
    internal void AbortWithoutWaiting() => _ = _cancel.CancelAsync();

    /// <summary>
    /// Starts the lifecycle if the job is still Queued. Records Queued → Scanning before
    /// returning, so the manager's next scheduling pass counts this job as scanning; all other
    /// work runs on the thread pool. Never throws: an unexpected exception ends the job Failed
    /// with <see cref="UnexpectedFailureReason"/> (never the exception's message, which can
    /// contain a path).
    /// </summary>
    public Task RunAsync()
    {
        lock (_lock)
        {
            if (_lifecycle.State != JobState.Queued)
            {
                return Task.CompletedTask;
            }
            _wait = JobWait.None;
            MoveLocked(JobState.Scanning);
        }
        return Task.Run(RunGuardedAsync);
    }

    /// <summary>Latched in any non-terminal state (see <see cref="PauseGate"/>).</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (JobStates.IsTerminal(_lifecycle.State))
            {
                return;
            }
        }
        // Outside the lock: the gate raises Changed, and a robocopy step suspends its process there.
        _gate.Pause();
        lock (_lock)
        {
            ReconcilePauseLocked();
            // JobSnapshot.PauseRequested changed even when the state did not (a latched pause).
            Touch();
        }
        DeliverOnThreadPool();
    }

    public void Resume()
    {
        _gate.Resume();
        lock (_lock)
        {
            ReconcilePauseLocked();
            Touch();
        }
        DeliverOnThreadPool();
    }

    /// <summary>Valid until Finalizing, ignored after; returns immediately, RunAsync finishes the cleanup.</summary>
    public void Cancel()
    {
        bool stopWork;
        lock (_lock)
        {
            var state = _lifecycle.State;
            if (JobStates.IsTerminal(state) || state == JobState.Finalizing || _cancelRequested)
            {
                return;
            }
            _cancelRequested = true;
            Touch();
            stopWork = state != JobState.Queued;
            if (!stopWork)
            {
                // Never started: nothing ran, nothing to clean up.
                MoveLocked(JobState.Canceled, SummaryLocked(JobState.Canceled));
                ReleaseLocked();
            }
        }
        if (stopWork)
        {
            // Asynchronous, so the kill, the closing dialog and the job's own continuations run
            // on the thread pool rather than inline on the caller (often the UI thread).
            _ = _cancel.CancelAsync();
        }
        DeliverOnThreadPool();
    }

    /// <summary>The user has seen this job's errors or damage (clears the attention state).</summary>
    public void Acknowledge()
    {
        lock (_lock)
        {
            if (!_acknowledged)
            {
                _acknowledged = true;
                Touch();
            }
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_lock)
        {
            var state = _lifecycle.State;
            var terminal = JobStates.IsTerminal(state);
            var done = DoneBytesLocked();

            // No speed while paused, canceling or not running: the tray must not show a rate
            // or an ETA for work that is not happening.
            var rate = state == JobState.Running && !_cancelRequested ? _progress?.BytesPerSecond : null;
            if (rate is <= 0)
            {
                rate = null;
            }
            var remaining = JobProgress.Estimate(_totalBytes - done, rate);

            return new JobSnapshot(
                Id,
                Start.ParentId,
                Start.Order.Verb,
                Start.Order.Sources,
                Start.Order.Destination,
                state,
                Start.Logging,
                Start.CreatedAt,
                done,
                _totalBytes,
                _doneFiles,
                _totalFiles,
                rate,
                remaining,
                _finalErrorCount ?? _totalErrors,
                _acknowledged)
            {
                RefusedCount = _issues.Count,
                RefusalReason = _issues.Count > 0 ? _issues[0].Reason : null,
                DamagedOnCancel = _damagedOnCancel,
                FailureReason = _failureReason,
                CancelRequested = _cancelRequested && !terminal,
                Wait = state == JobState.Queued ? _wait : JobWait.None,
                NoOp = _noOp,
                PauseRequested = !terminal && _gate.IsPaused,
                SkippedAppeared = _skippedAppeared,
            };
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- lifecycle

    private async Task RunGuardedAsync()
    {
        try
        {
            DeliverNotices();
            var token = _cancel.Token;
            try
            {
                await RunLifecycleAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await FinishCanceledAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            FailUnexpectedly();
        }
    }

    private async Task RunLifecycleAsync(CancellationToken token)
    {
        var plan = await ScanAndDecideAsync(token).ConfigureAwait(false);
        if (plan is null)
        {
            await FinishCanceledAsync().ConfigureAwait(false);
            return;
        }

        var run = false;
        LedgerPart? main = null;
        lock (_lock)
        {
            if (!CancelRequestedLocked(token))
            {
                _plan = plan;
                main = new LedgerPart(plan);
                _parts.Add(main);
                _issues.Clear();
                _issues.AddRange(plan.Issues);
                _totalBytes = plan.TotalBytes;
                _totalFiles = plan.TotalFiles;
                _progress = new JobProgress(_totalBytes, _totalFiles);
                _presence = new HashSet<string>(plan.PresentBeforeRun.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
                AddClaimsLocked(plan);
                if (plan.Steps.Count == 0)
                {
                    // Nothing to run: everything is in place, kept, or refused. The outcome is
                    // still computed in Finalizing, so refusals are reported in one place.
                    _noOp = plan.IsNoOp;
                    MoveLocked(JobState.Finalizing);
                }
                else
                {
                    MoveLocked(JobState.Running);
                    // A pause latched before Running takes effect now.
                    ReconcilePauseLocked();
                    run = true;
                }
            }
        }
        DeliverNotices();
        if (main is null)
        {
            await FinishCanceledAsync().ConfigureAwait(false);
            return;
        }

        var outcomes = new List<StepOutcome>();
        if (run)
        {
            var completed = await RunPartAsync(main, 0, outcomes, token).ConfigureAwait(false);
            if (completed && main.Replans.Count > 0)
            {
                var appendix = await RunBlockingAsync(() => PlanReplacementMoves(main.Replans, token)).ConfigureAwait(false);
                LedgerPart second;
                lock (_lock)
                {
                    second = new LedgerPart(appendix);
                    _parts.Add(second);
                    _issues.AddRange(appendix.Issues);
                    _totalBytes += appendix.TotalBytes;
                    _totalFiles += appendix.TotalFiles;
                    foreach (var present in appendix.PresentBeforeRun)
                    {
                        _presence!.Add(WinPath.NormalizeForMatch(present));
                    }
                    AddClaimsLocked(appendix);
                    // JobProgress's total is fixed at construction; start a new one for the
                    // larger total, carrying over what is done (the rate starts again).
                    var done = DoneBytesLocked();
                    _progress = new JobProgress(_totalBytes, _totalFiles);
                    _progress.SetObservedBytes(done, Services.Time.GetUtcNow());
                    Touch();
                }
                completed = await RunPartAsync(second, main.Plan.Steps.Count, outcomes, token).ConfigureAwait(false);
            }
            if (!completed || !await EnterFinalizingAsync(token).ConfigureAwait(false))
            {
                await FinishCanceledAsync().ConfigureAwait(false);
                return;
            }
        }

        await FinalizeAsync(outcomes).ConfigureAwait(false);
    }

    /// <summary>Scanning and AwaitingDecision. Null means canceled.</summary>
    private async Task<ExecutionPlan?> ScanAndDecideAsync(CancellationToken token)
    {
        var fileSystem = Services.FileSystem;
        if (Start.RetryPlan is { } retry)
        {
            return await RunBlockingAsync(() => RefreshRetryPlan(retry, new ScanProgressReporter(this), token)).ConfigureAwait(false);
        }

        var scan = await RunBlockingAsync(() =>
        {
            var pastePlan = PastePlanner.Plan(Start.Order, fileSystem);
            return JobScanner.Scan(pastePlan, fileSystem, fileSystem, new ScanProgressReporter(this), token);
        }).ConfigureAwait(false);

        var configured = Start.Settings.ConflictDefault;
        ConflictChoice? choice = null;
        if (scan.Conflicts.Count > 0 && configured == ConflictPolicy.Ask)
        {
            bool ask;
            lock (_lock)
            {
                ask = !CancelRequestedLocked(token);
                if (ask)
                {
                    _issues.Clear();
                    _issues.AddRange(scan.Issues);
                    _totalBytes = scan.TotalBytes;
                    _totalFiles = scan.TotalFiles;
                    MoveLocked(JobState.AwaitingDecision);
                }
            }
            DeliverNotices();
            if (!ask)
            {
                return null;
            }

            // The dialog completes on the UI thread; continue on the thread pool, because
            // ExecutionPlanner.Apply below asks the disk about keep-both names.
            var prompt = Services.Prompts.ResolveConflictsAsync(Snapshot(), scan.Conflicts, token);
            choice = await OnThreadPool(prompt).ConfigureAwait(false);
            if (choice is null || token.IsCancellationRequested)
            {
                // The user closed the dialog or pressed Cancel: the job is canceled.
                lock (_lock)
                {
                    _cancelRequested = true;
                    Touch();
                }
                return null;
            }
        }

        return ExecutionPlanner.Apply(scan, configured, choice, fileSystem.Exists);
    }

    /// <summary>
    /// A retry child's Scanning: the planned files' current sizes for the totals, nothing
    /// else. The plan itself is fixed by the parent and never prompts.
    /// </summary>
    private ExecutionPlan RefreshRetryPlan(ExecutionPlan plan, IProgress<ScanProgress> progress, CancellationToken token)
    {
        var steps = new List<ExecutionStep>(plan.Steps.Count);
        long files = 0;
        long bytes = 0;
        foreach (var step in plan.Steps)
        {
            token.ThrowIfCancellationRequested();
            var refreshed = new List<PlannedFile>(step.Files.Count);
            foreach (var file in step.Files)
            {
                var current = Services.FileSystem.FileAt(file.SourcePath) is { } facts ? file with { Source = facts } : file;
                refreshed.Add(current);
                files++;
                bytes += current.Source.Size;
                if (files % 1_000 == 0)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new ScanProgress(files, bytes));
                }
            }
            steps.Add(step with { Files = refreshed });
        }
        progress.Report(new ScanProgress(files, bytes));
        return plan with { Steps = steps };
    }

    /// <summary>Runs one part's steps in order. False when canceled.</summary>
    private async Task<bool> RunPartAsync(LedgerPart part, int pipeIndexOffset, List<StepOutcome> outcomes, CancellationToken token)
    {
        for (var index = 0; index < part.Plan.Steps.Count; index++)
        {
            if (!await WaitForGateAsync(token).ConfigureAwait(false))
            {
                return false;
            }
            var outcome = await RunStepAsync(part, index, pipeIndexOffset + index, token).ConfigureAwait(false);
            if (outcome is null)
            {
                return false;
            }
            outcomes.Add(outcome);
            if (token.IsCancellationRequested)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>One step, from the presence check to ShellNotify. Null when canceled before it started.</summary>
    private async Task<StepOutcome?> RunStepAsync(LedgerPart part, int index, int pipeIndex, CancellationToken token)
    {
        var step = part.Plan.Steps[index];
        if (step.Step is RobocopyStep)
        {
            // Cancel cleanup only ever considers robocopy steps' files, so only they need the
            // re-check. One listing per destination folder; a dedicated thread, because a deep
            // tree on a slow share means many listings.
            await RunBlockingAsync(() => RecordPresence(step, token)).ConfigureAwait(false);
        }

        int run;
        lock (_lock)
        {
            if (CancelRequestedLocked(token))
            {
                return null;
            }
            part.Ledger.StepStarted(index);
            run = ++_runSerial;
            _activeRun = run;
            // No step runs at this point, so the job's own exact count of completed bytes is
            // also "completed bytes of finished steps"; the larger of the two keeps observed
            // bytes from lagging behind files an in-process step already finished.
            _observedBase = Math.Max(CompletedBytesOfFinishedStepsLocked(), _doneFileBytes);
        }

        var refused = false;
        StepOutcome outcome;
        switch (step.Step)
        {
            case RobocopyStep robocopy:
                outcome = await RunRobocopyAsync(part, index, pipeIndex, robocopy, step.Policy, run, token).ConfigureAwait(false);
                break;
            case RenameStep rename:
                outcome = InProcessCopier.Rename(rename.Source, rename.Destination);
                if (outcome.ReplanAsMove)
                {
                    part.Replans.Add(rename);
                }
                break;
            case KeepBothStep { Move: true } keepBoth:
                outcome = InProcessCopier.Rename(keepBoth.Source, keepBoth.Destination);
                // Robocopy cannot write under the keep-both name, and moving to the original
                // name would replace the file the user chose to keep.
                refused = outcome.ReplanAsMove;
                break;
            case KeepBothStep keepBoth:
                outcome = await CopyInProcessAsync(keepBoth.Source, keepBoth.Destination, run, token).ConfigureAwait(false);
                break;
            case DuplicateFileStep duplicate:
                outcome = await CopyInProcessAsync(duplicate.Source, duplicate.Destination, run, token).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Unknown plan step type.");
        }

        lock (_lock)
        {
            // From here on, callbacks of this run are stale.
            _activeRun = 0;
            // A run the app killed has no normal exit code; one that finished just before the
            // cancel arrived is judged on its own output.
            var killedByCancel = token.IsCancellationRequested && (outcome.ExitCode is not { } exit || exit.Value < 0);
            if (step.Step is RobocopyStep)
            {
                part.Ledger.StepFinished(index, outcome.ExitCode, killedByCancel);
            }
            else
            {
                if (InProcessSucceeded(outcome))
                {
                    part.Ledger.InProcessCompleted(index);
                    var now = Services.Time.GetUtcNow();
                    foreach (var file in step.Files)
                    {
                        FileCompletedLocked(file, now);
                    }
                }
                else if (refused)
                {
                    part.Ledger.InProcessRefused(index);
                    _issues.Add(new PlanIssue(SourceOf(step.Step), CannotMoveUnderNewNameReason));
                }
                else if (outcome.ReplanAsMove)
                {
                    part.Ledger.InProcessReplannedAsMove(index);
                }
                // Anything else failed; the ledger lists it in FailedInProcessSteps once finished.
                foreach (var error in outcome.Errors)
                {
                    RecordErrorLocked(error);
                }
                part.Ledger.StepFinished(index, null, killedByCancel);
            }
            Touch();
        }

        NotifyShellAfterStep(step.Step, outcome);

        if (Volatile.Read(ref _callbackFault) is { } fault)
        {
            // A ledger update failed on the run's consumer thread. The job's accounting can no
            // longer be trusted, so it ends Failed (no cleanup: when in doubt, keep data).
            ExceptionDispatchInfo.Throw(fault);
        }
        return outcome;
    }

    private async Task<StepOutcome> RunRobocopyAsync(
        LedgerPart part,
        int index,
        int pipeIndex,
        RobocopyStep step,
        ConflictPolicy policy,
        int run,
        CancellationToken token)
    {
        // Per-run thread count from both ends' drives. The volume queries can wait on a sleeping
        // disk or a slow share, so they run on a dedicated thread like the presence check;
        // ThreadsFor never throws (a failure means ThreadPolicy.Fallback).
        var threads = await RunBlockingAsync(
            () => DriveMedia.ThreadsFor(Start.Settings, step.SourceDirectory, step.DestinationDirectory)).ConfigureAwait(false);
        var pipeName = PipeNames.ForStep(Id, pipeIndex, NewPipeNonce());
        var arguments = RobocopyArgs.Build(step, threads.ApplyTo(Start.Settings), policy, pipeName);
        BestEffort(sink => sink.CommandStarted(Id, arguments));
        // Before the run starts, so it cannot interleave with robocopy's own lines.
        BestEffort(sink => sink.OutputLine(Id, threads.LogLine));

        StepOutcome outcome;
        using (var robocopy = new RobocopyRun(arguments, pipeName, _gate, Services.Sampler, new RunObserver(this, part.Ledger, index, run)))
        {
            outcome = await robocopy.RunAsync(token).ConfigureAwait(false);
        }

        if (outcome.ExitCode is { } exitCode)
        {
            BestEffort(sink => sink.CommandFinished(Id, exitCode.Value));
        }
        return outcome;
    }

    private async Task<StepOutcome> CopyInProcessAsync(string source, string destination, int run, CancellationToken token)
    {
        var copy = InProcessCopier.CopyFileAsync(
            source,
            destination,
            _gate,
            bytes => OnRunBytes(run, bytes, Services.Time.GetUtcNow()),
            token);
        // The copy completes on its dedicated thread; carry on from the pool, not that thread.
        return await OnThreadPool(copy).ConfigureAwait(false);
    }

    /// <summary>
    /// The second ledger part: each re-planned rename scanned again on its own and run as a
    /// robocopy move. Ask with SkipAll is the documented way to get Skip semantics both with
    /// and without conflicts: kept names are left out by construction (so a /MOV run never
    /// sees them), and anything that appears later is skipped by Ask's flags.
    /// </summary>
    private ExecutionPlan PlanReplacementMoves(IReadOnlyList<RenameStep> renames, CancellationToken token)
    {
        var fileSystem = Services.FileSystem;
        var steps = new List<PlanStep>();
        var refused = new List<PlanIssue>();
        foreach (var rename in renames)
        {
            var name = WinPath.GetFileName(rename.Source);
            if (!WinPath.Comparer.Equals(name, WinPath.GetFileName(rename.Destination)))
            {
                refused.Add(new PlanIssue(rename.Source, CannotMoveUnderNewNameReason));
                continue;
            }
            switch (fileSystem.KindOf(rename.Source))
            {
                case ItemKind.File:
                    steps.Add(new RobocopyStep(
                        WinPath.GetParent(rename.Source), WinPath.GetParent(rename.Destination), [name], Recursive: false, Move: true));
                    break;
                case ItemKind.Directory:
                    steps.Add(new RobocopyStep(rename.Source, rename.Destination, [], Recursive: true, Move: true));
                    break;
                case ItemKind.DirectoryLink:
                    // Robocopy follows a link given as its source root; /MOVE would empty the target.
                    refused.Add(new PlanIssue(rename.Source, PastePlanner.LinkReason));
                    break;
                default:
                    refused.Add(new PlanIssue(rename.Source, PastePlanner.MissingReason));
                    break;
            }
        }
        var scan = JobScanner.Scan(new PastePlan(steps, refused, []), fileSystem, fileSystem, null, token);
        return ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, new ConflictChoice.SkipAll(), fileSystem.Exists);
    }

    /// <summary>Waits while paused. False when canceled.</summary>
    private async Task<bool> WaitForGateAsync(CancellationToken token)
    {
        while (true)
        {
            await _gate.WaitWhilePausedAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return false;
            }
            bool open;
            lock (_lock)
            {
                ReconcilePauseLocked();
                open = !_gate.IsPaused;
            }
            DeliverNotices();
            if (open)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Running → Finalizing once the gate is open. The cancel flag is checked under the same
    /// lock as the move, so a cancel either lands before Finalizing or is ignored. False when canceled.
    /// </summary>
    private async Task<bool> EnterFinalizingAsync(CancellationToken token)
    {
        while (true)
        {
            if (!await WaitForGateAsync(token).ConfigureAwait(false))
            {
                return false;
            }
            bool canceled;
            bool entered = false;
            lock (_lock)
            {
                canceled = CancelRequestedLocked(token);
                if (!canceled)
                {
                    ReconcilePauseLocked();
                    entered = _lifecycle.State == JobState.Running && !_gate.IsPaused;
                    if (entered)
                    {
                        MoveLocked(JobState.Finalizing);
                    }
                }
            }
            DeliverNotices();
            if (canceled)
            {
                return false;
            }
            if (entered)
            {
                return true;
            }
        }
    }

    private async Task FinalizeAsync(List<StepOutcome> outcomes)
    {
        string[] linkFolders;
        lock (_lock)
        {
            linkFolders = _parts.SelectMany(p => p.Plan.LinkFolders).ToArray();
        }
        if (linkFolders.Length > 0)
        {
            var linkErrors = await RunBlockingAsync(() => CreateLinkFolders(linkFolders)).ConfigureAwait(false);
            if (linkErrors.Count > 0)
            {
                outcomes.Add(new StepOutcome([], linkErrors, null, null));
                lock (_lock)
                {
                    foreach (var error in linkErrors)
                    {
                        RecordErrorLocked(error);
                    }
                    Touch();
                }
            }
        }

        int issues;
        lock (_lock)
        {
            issues = _issues.Count;
        }
        var final = JobOutcome.FinalState(outcomes, canceled: false, issues);
        var completedSources = outcomes.Sum(o => o.CompletedSources.Count);

        if (final == JobState.Done && completedSources > 0 && Start.CutClipboardSequence is { } sequence)
        {
            // A newer clipboard write by anyone is left alone (ClearIfUnchanged compares).
            try
            {
                await OnThreadPool(Services.ClearClipboardIfUnchanged(sequence)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the paste itself succeeded.
            }
        }

        NotifyShellFinal();

        var failureReason = final == JobState.Failed && outcomes.FirstOrDefault(IsBroken) is { } broken
            ? FailureText.Describe(broken)
            : null;

        lock (_lock)
        {
            _failureReason = failureReason;
            _finalErrorCount = final == JobState.DoneWithErrors ? ErrorCountLocked(outcomes) : _totalErrors;
            RecordSkippedAppearedLocked();
            MoveLocked(final, SummaryLocked(final));
            ReleaseLocked();
        }
        DeliverNotices();
    }

    /// <summary>
    /// After a cancel: whatever ran has stopped (RobocopyRun returns only after the kill and
    /// the end of output; CopyFileEx removed its own partial file). Removes the partial files
    /// the job created, never anything else.
    /// </summary>
    private async Task FinishCanceledAsync()
    {
        LedgerPart[] parts;
        HashSet<string> presence;
        bool unreliable;
        List<KilledRunFile> killed;
        List<string> completed;
        Dictionary<string, KillObservation> atKill;
        lock (_lock)
        {
            _cancelRequested = true;
            Touch();
            parts = _parts.ToArray();
            presence = _presence is null ? new HashSet<string>(WinPath.Comparer) : new HashSet<string>(_presence, WinPath.Comparer);
            unreliable = parts.Any(p => p.Ledger.PathsUnreliable);
            killed = parts.SelectMany(p => p.Ledger.KilledRunFiles).ToList();
            completed = parts.SelectMany(p => p.Ledger.CompletedSources).ToList();
            atKill = new Dictionary<string, KillObservation>(_atKill, WinPath.Comparer);
        }
        DeliverNotices();

        Cleanup? cleanup = null;
        if (parts.Length > 0 && !unreliable && killed.Count > 0)
        {
            // Skipped entirely when the ledger could not match robocopy's paths: it can no
            // longer tell finished files from partial ones, and keeping data wins.
            cleanup = await RunBlockingAsync(() => CleanUpPartialFiles(parts, killed, completed, presence, atKill)).ConfigureAwait(false);
        }
        else
        {
            System.Diagnostics.Trace.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"RoboRightClick cancel cleanup: skipped, parts={parts.Length} pathsUnreliable={unreliable} candidates={killed.Count}"));
        }

        lock (_lock)
        {
            if (cleanup is { } c)
            {
                _damagedOnCancel = c.LeftInPlace.Count;
                _damagedPaths.AddRange(c.LeftInPlace.Take(JobRecords.MaxRecordedErrors));
                for (var i = 0; i < parts.Length; i++)
                {
                    parts[i].Damaged.AddRange(c.DamagedByPart[i]);
                }
            }
            RecordSkippedAppearedLocked();
            MoveLocked(JobState.Canceled, SummaryLocked(JobState.Canceled));
            ReleaseLocked();
        }
        DeliverNotices();
    }

    private Cleanup CleanUpPartialFiles(
        LedgerPart[] parts,
        List<KilledRunFile> killed,
        List<string> completed,
        HashSet<string> presence,
        Dictionary<string, KillObservation> atKill)
    {
        var move = Start.Order.Verb == TransferVerb.Move;
        var plan = CancelCleanup.Select(
            killed,
            completed,
            presence,
            atKill,
            move,
            destinationExists: Services.FileSystem.Exists,
            sourceStillExists: File.Exists,
            claimedByOtherJob: path => Services.Claims.ClaimedByOtherJob(Id, path));

        // Invariant 1, belt and braces: whatever the selection says, a path this job reads as
        // a source is never deleted by cleanup.
        var sources = new HashSet<string>(WinPath.Comparer);
        foreach (var part in parts)
        {
            foreach (var step in part.Plan.Steps)
            {
                foreach (var file in step.Files)
                {
                    sources.Add(WinPath.NormalizeForMatch(file.SourcePath));
                }
            }
        }

        var deleted = 0;
        foreach (var target in plan.Delete)
        {
            if (sources.Contains(WinPath.NormalizeForMatch(target.Path)))
            {
                continue;
            }
            // A file in use, already gone, denied or no longer the one robocopy held open is
            // left: when in doubt, keep data.
            if (ProcessNative.DeleteFileIfSameFile(target.Path, target.Identity))
            {
                deleted++;
            }
        }
        TraceCleanup(killed.Count, atKill, plan, deleted);

        // The files left in place may be incomplete: "Finish copying them" repeats them.
        // For a cut whose source is already gone the move had finished, so there is nothing
        // to repeat (and a retry would only fail on the missing source).
        var leftInPlace = new HashSet<string>(plan.LeftInPlace.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var damagedByPart = new List<(int StepIndex, PlannedFile File)>[parts.Length];
        for (var p = 0; p < parts.Length; p++)
        {
            damagedByPart[p] = [];
            if (leftInPlace.Count == 0)
            {
                continue;
            }
            var steps = parts[p].Plan.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Step is not RobocopyStep)
                {
                    continue;
                }
                foreach (var file in steps[i].Files)
                {
                    if (leftInPlace.Contains(WinPath.NormalizeForMatch(file.DestinationPath))
                        && (!move || File.Exists(file.SourcePath)))
                    {
                        damagedByPart[p].Add((i, file));
                    }
                }
            }
        }
        return new Cleanup(plan.LeftInPlace, damagedByPart);
    }

    /// <summary>
    /// One debug-output line per cancel cleanup (OutputDebugString through the default trace
    /// listener; nothing reaches a file): how many candidates there were, what was seen at
    /// the kill, and how many files were deleted or left. Counts only, never a path, so it is
    /// allowed in ephemeral mode. It is how a test tells "nothing was proven" from "the
    /// delete failed".
    /// </summary>
    private static void TraceCleanup(int candidates, Dictionary<string, KillObservation> atKill, CleanupPlan plan, int deleted)
    {
        int Count(KillEvidence evidence) => atKill.Values.Count(o => o.Evidence == evidence);
        System.Diagnostics.Trace.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"RoboRightClick cancel cleanup: candidates={candidates} observed={atKill.Count} open={Count(KillEvidence.OpenByRobocopy)} notOpen={Count(KillEvidence.NotOpenByRobocopy)} absent={Count(KillEvidence.Absent)} unknown={Count(KillEvidence.Unknown)} toDelete={plan.Delete.Count} deleted={deleted} leftInPlace={plan.LeftInPlace.Count}"));
    }

    /// <summary>Explorer leaves an empty folder for each directory link robocopy skipped (docs/parity.md).</summary>
    private List<ErrorReported> CreateLinkFolders(IEnumerable<string> folders)
    {
        var errors = new List<ErrorReported>();
        foreach (var folder in folders.Distinct(WinPath.Comparer))
        {
            try
            {
                if (Services.FileSystem.KindOf(folder) == ItemKind.Missing)
                {
                    Directory.CreateDirectory(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The system message for the code, not ex.Message, which embeds the path.
                var code = Win32Code(ex);
                errors.Add(new ErrorReported(code, CreateFolderOperation, folder, code != 0 ? new System.ComponentModel.Win32Exception(code).Message : string.Empty));
            }
        }
        return errors;
    }

    /// <summary>
    /// Runs on the canceling thread while robocopy is suspended for the kill. For each file of
    /// the step not reported complete and not present before the step, records whether a file
    /// is there and whether robocopy holds it open. One listing per destination folder finds
    /// the files that exist, so only those are opened: robocopy's in-flight files, finished
    /// files whose lines are still in the pipe, and late arrivals.
    /// </summary>
    private void ObserveAtKill(StepLedger ledger, int stepIndex, int run, int processId)
    {
        List<string> candidates;
        lock (_lock)
        {
            if (run != _activeRun)
            {
                return;
            }
            var presence = _presence;
            candidates = ledger.UnfinishedFiles(stepIndex)
                .Select(f => f.DestinationPath)
                .Where(p => presence is null || !presence.Contains(WinPath.NormalizeForMatch(p)))
                .Distinct(WinPath.Comparer)
                .ToList();
        }

        var observed = new List<(string Path, KillObservation Observation)>(candidates.Count);
        foreach (var folder in candidates.GroupBy(WinPath.GetParent, WinPath.Comparer))
        {
            HashSet<string>? names = null;
            try
            {
                if (Services.FileSystem.List(folder.Key) is { } entries)
                {
                    names = new HashSet<string>(entries.Select(e => e.Name), WinPath.Comparer);
                }
            }
            catch (Exception)
            {
                // Unknown: every file of this folder is opened and asked about directly.
            }

            foreach (var path in folder)
            {
                var name = WinPath.GetFileName(path);
                if (names is not null && !names.Contains(name))
                {
                    observed.Add((path, new KillObservation(KillEvidence.Absent, default)));
                    continue;
                }
                KillObservation observation;
                try
                {
                    observation = ProcessNative.ObserveAtKill(path, processId);
                }
                catch (Exception)
                {
                    observation = default;
                }
                observed.Add((path, observation));
            }
        }

        lock (_lock)
        {
            foreach (var (path, observation) in observed)
            {
                _atKill[WinPath.NormalizeForMatch(path)] = observation;
            }
        }
    }

    /// <summary>Records which of the step's destinations exist right now (one listing per folder).</summary>
    private void RecordPresence(ExecutionStep step, CancellationToken token)
    {
        var wanted = new HashSet<string>(step.Files.Select(f => WinPath.NormalizeForMatch(f.DestinationPath)), WinPath.Comparer);
        var found = new List<string>();
        foreach (var folder in step.Files.Select(f => WinPath.GetParent(f.DestinationPath)).Distinct(WinPath.Comparer))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var entries = Services.FileSystem.List(folder);
                if (entries is null)
                {
                    continue;
                }
                foreach (var entry in entries)
                {
                    var path = WinPath.NormalizeForMatch(WinPath.Combine(folder, entry.Name));
                    if (wanted.Contains(path))
                    {
                        found.Add(path);
                    }
                }
            }
            catch (Exception)
            {
                // Unknown is treated as present: cancel cleanup then never deletes these
                // files. Reporting a new file as possibly damaged beats deleting a user's file.
                found.AddRange(wanted.Where(p => WinPath.Comparer.Equals(WinPath.GetParent(p), WinPath.NormalizeForMatch(folder))));
            }
        }
        lock (_lock)
        {
            if (_presence is { } presence)
            {
                foreach (var path in found)
                {
                    presence.Add(path);
                }
            }
        }
    }

    private void FailUnexpectedly()
    {
        try
        {
            lock (_lock)
            {
                if (JobStates.IsTerminal(_lifecycle.State))
                {
                    return;
                }
                _failureReason = UnexpectedFailureReason;
                _activeRun = 0;
                MoveLocked(JobState.Failed, SummaryLocked(JobState.Failed));
                ReleaseLocked();
            }
            DeliverNotices();
        }
        catch (Exception)
        {
            // Nothing is left that could report this; RunAsync must not throw.
        }
    }

    // ---------------------------------------------------------------- callbacks of a run

    private void OnRunLines(int run, IReadOnlyList<string> lines)
    {
        lock (_lock)
        {
            if (run != _activeRun)
            {
                return;
            }
        }
        BestEffort(sink =>
        {
            foreach (var line in lines)
            {
                sink.OutputLine(Id, line);
            }
        });
    }

    private void OnRunEvents(StepLedger ledger, int stepIndex, int run, IReadOnlyList<RobocopyEvent> events)
    {
        try
        {
            lock (_lock)
            {
                if (run != _activeRun)
                {
                    return;
                }
                var now = Services.Time.GetUtcNow();
                foreach (var robocopyEvent in events)
                {
                    var update = ledger.Apply(stepIndex, robocopyEvent);
                    if (update.Completed is { } completed)
                    {
                        FileCompletedLocked(completed, now);
                    }
                    if (update.Uncompleted is { } uncompleted)
                    {
                        // An ERROR arrived after the file's line (order under /MT is not
                        // guaranteed). JobProgress cannot subtract; the exact counts live here.
                        _doneFiles--;
                        _doneFileBytes -= uncompleted.Source.Size;
                    }
                    if (update.Error is { } error)
                    {
                        RecordErrorLocked(error);
                    }
                }
                Touch();
            }
        }
        catch (Exception ex)
        {
            // Not thrown into RobocopyRun's consumer: the step finishes, then the job fails.
            Interlocked.CompareExchange(ref _callbackFault, ex, null);
        }
    }

    private void OnRunBytes(int run, long bytes, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (run != _activeRun || _progress is null)
            {
                return;
            }
            // Finished steps count by their completed files only: earlier runs' read
            // counters include partial reads of files that failed.
            _progress.SetObservedBytes(_observedBase + bytes, at);
            Touch();
        }
    }

    // ---------------------------------------------------------------- helpers under the lock

    private void MoveLocked(JobState next, JobSummary? summary = null)
    {
        _lifecycle.MoveTo(next, Services.Time.GetUtcNow());
        _notices.Enqueue(new Notice(null, _lifecycle.History[^1], summary));
        Touch();
    }

    /// <summary>Running ⇄ Paused follows the gate. Called by pause, resume and the job loop alike, so whichever runs first wins and the rest find nothing to do.</summary>
    private void ReconcilePauseLocked()
    {
        var paused = _gate.IsPaused;
        switch (_lifecycle.State)
        {
            case JobState.Running when paused:
                MoveLocked(JobState.Paused);
                break;
            case JobState.Paused when !paused:
                MoveLocked(JobState.Running);
                // The paused time must not drag the rate down and inflate the ETA.
                _progress?.ResetRate();
                break;
        }
    }

    private bool CancelRequestedLocked(CancellationToken token) => _cancelRequested || token.IsCancellationRequested;

    private long DoneBytesLocked() =>
        Math.Min(_totalBytes, Math.Max(_doneFileBytes, _progress?.ObservedBytes ?? 0));

    private long CompletedBytesOfFinishedStepsLocked() => _parts.Sum(p => p.Ledger.CompletedBytesOfFinishedSteps);

    private void FileCompletedLocked(PlannedFile file, DateTimeOffset at)
    {
        _doneFiles++;
        _doneFileBytes += file.Source.Size;
        _progress?.FileCompleted(file.Source.Size, at);
    }

    /// <summary>The ledgers' late arrivals, read once at the end (the parts may be released after).</summary>
    private void RecordSkippedAppearedLocked()
    {
        foreach (var part in _parts)
        {
            foreach (var file in part.Ledger.SkippedLateArrivals)
            {
                _skippedAppeared++;
                if (_skippedAppearedPaths.Count < JobRecords.MaxRecordedErrors)
                {
                    _skippedAppearedPaths.Add(file.DestinationPath);
                }
            }
        }
    }

    private void RecordErrorLocked(ErrorReported error)
    {
        _totalErrors++;
        if (_errors.Count < JobRecords.MaxRecordedErrors)
        {
            _errors.Add(error);
        }
    }

    private JobSummary SummaryLocked(JobState final) =>
        new(Id, final, _doneFiles, _doneFileBytes, _errors.ToArray()) { TotalErrors = _totalErrors };

    /// <summary>
    /// "Try again (N)" counts retryable items, and the toast says how many items failed, so
    /// a DoneWithErrors job never shows 0: a step that could not run at all reported no
    /// per-file error but still failed.
    /// </summary>
    private int ErrorCountLocked(List<StepOutcome> outcomes)
    {
        var retryable = _parts.Sum(p => RobocopyRetryable(p).Count + p.Ledger.FailedInProcessSteps.Count);
        var broken = outcomes.Count(IsBroken);
        return Math.Max(_totalErrors, Math.Max(retryable, broken));
    }

    private void AddClaimsLocked(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            foreach (var file in step.Files)
            {
                _claimedFiles.Add(WinPath.NormalizeForMatch(file.DestinationPath));
            }
            switch (step.Step)
            {
                case RenameStep rename:
                    _claimedRoots.Add(WinPath.NormalizeForMatch(rename.Destination));
                    break;
                case KeepBothStep keepBoth:
                    _claimedFiles.Add(WinPath.NormalizeForMatch(keepBoth.Destination));
                    break;
                case DuplicateFileStep duplicate:
                    _claimedFiles.Add(WinPath.NormalizeForMatch(duplicate.Destination));
                    break;
            }
        }
    }

    /// <summary>
    /// Finished jobs stay in memory for the Jobs window; their plan and ledger can hold
    /// hundreds of bytes per file, so they are kept only while "Try again" can use them.
    /// Claims stay: they are the session-wide record cancel cleanup consults.
    /// </summary>
    private void ReleaseLocked()
    {
        var state = _lifecycle.State;
        var retryable = state == JobState.DoneWithErrors || (state == JobState.Canceled && _damagedOnCancel > 0);
        if (!retryable)
        {
            _parts.Clear();
            _plan = null;
        }
        _presence = null;
    }

    private void Touch() => Interlocked.Increment(ref _version);

    // ---------------------------------------------------------------- notices

    private void DeliverOnThreadPool() =>
        ThreadPool.QueueUserWorkItem(static job => job.DeliverNotices(), this, preferLocal: false);

    /// <summary>
    /// Delivers queued notices in order, outside the lock. One thread delivers at a time; a
    /// notice queued meanwhile is picked up by that thread's loop, so none is lost or reordered.
    /// </summary>
    private void DeliverNotices()
    {
        lock (_lock)
        {
            if (_delivering)
            {
                return;
            }
            _delivering = true;
        }

        var drained = false;
        try
        {
            while (true)
            {
                Notice notice;
                lock (_lock)
                {
                    if (_notices.Count == 0)
                    {
                        _delivering = false;
                        drained = true;
                        return;
                    }
                    notice = _notices.Dequeue();
                }
                Deliver(notice);
            }
        }
        finally
        {
            if (!drained)
            {
                lock (_lock)
                {
                    _delivering = false;
                }
            }
        }
    }

    private void Deliver(Notice notice)
    {
        if (notice.Created is { } description)
        {
            BestEffort(sink => sink.JobCreated(description));
        }
        if (notice.Change is { } change)
        {
            BestEffort(sink => sink.StateChanged(Id, change));
            if (notice.Summary is { } summary)
            {
                BestEffort(sink => sink.JobFinished(summary));
                // JobFinished is the sink's last call (IJobSink's documented order).
                BestEffort(sink => (sink as IDisposable)?.Dispose());
            }
            try
            {
                OnStateChanged();
            }
            catch (Exception)
            {
                // A listener's failure never changes the job's outcome.
            }
        }
    }

    /// <summary>Logging is best-effort and never changes a job's outcome.</summary>
    private void BestEffort(Action<IJobSink> write)
    {
        try
        {
            write(Start.Sink);
        }
        catch (Exception)
        {
            // A full disk or antivirus on the log file must not fail the paste.
        }
    }

    // ---------------------------------------------------------------- shell refresh

    private void NotifyShellAfterStep(PlanStep step, StepOutcome outcome)
    {
        try
        {
            switch (step)
            {
                case RobocopyStep { Recursive: true } tree:
                    ShellNotify.FolderChanged(WinPath.GetParent(tree.DestinationDirectory));
                    if (tree.Move)
                    {
                        ShellNotify.FolderChanged(WinPath.GetParent(tree.SourceDirectory));
                    }
                    break;
                case RobocopyStep files:
                    ShellNotify.FolderChanged(files.DestinationDirectory);
                    if (files.Move)
                    {
                        ShellNotify.FolderChanged(files.SourceDirectory);
                    }
                    break;
                case RenameStep rename when InProcessSucceeded(outcome):
                    ShellNotify.Renamed(rename.Source, rename.Destination, IsFolder(rename.Destination));
                    break;
                case KeepBothStep { Move: true } keepBoth when InProcessSucceeded(outcome):
                    ShellNotify.Renamed(keepBoth.Source, keepBoth.Destination, isFolder: false);
                    break;
                case KeepBothStep keepBoth:
                    ShellNotify.FolderChanged(WinPath.GetParent(keepBoth.Destination));
                    break;
                case DuplicateFileStep duplicate:
                    ShellNotify.FolderChanged(WinPath.GetParent(duplicate.Destination));
                    break;
            }
        }
        catch (Exception)
        {
            // A refresh hint only; Explorer catches up on its own for local volumes.
        }
    }

    private void NotifyShellFinal()
    {
        try
        {
            ShellNotify.FolderChanged(Start.Order.Destination);
            if (Start.Order.Verb == TransferVerb.Move)
            {
                foreach (var parent in Start.Order.Sources.Select(WinPath.GetParent).Distinct(WinPath.Comparer))
                {
                    ShellNotify.FolderChanged(parent);
                }
            }
        }
        catch (Exception)
        {
            // A refresh hint only.
        }
    }

    private bool IsFolder(string path) => Services.FileSystem.KindOf(path) is ItemKind.Directory or ItemKind.DirectoryLink;

    // ---------------------------------------------------------------- static helpers

    private static bool InProcessSucceeded(StepOutcome outcome) =>
        outcome.Failure is null && outcome.Errors.Count == 0 && !outcome.ReplanAsMove;

    private static bool IsBroken(StepOutcome outcome) => outcome.Failure is not null || outcome.ExitCode is { FatalError: true };

    private static List<(int StepIndex, PlannedFile File)> RobocopyRetryable(LedgerPart part) =>
        part.Ledger.Retryable
            .Where(r => r.StepIndex >= 0 && r.StepIndex < part.Plan.Steps.Count && part.Plan.Steps[r.StepIndex].Step is RobocopyStep)
            .ToList();

    private static ExecutionPlan? Merge(ExecutionPlan? first, ExecutionPlan? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }
        return new ExecutionPlan(
            [.. first.Steps, .. second.Steps],
            [.. first.PresentBeforeRun, .. second.PresentBeforeRun],
            [.. first.LinkFolders, .. second.LinkFolders],
            [.. first.Kept, .. second.Kept],
            [.. first.Issues, .. second.Issues]);
    }

    private static string SourceOf(PlanStep step) => step switch
    {
        RenameStep rename => rename.Source,
        KeepBothStep keepBoth => keepBoth.Source,
        DuplicateFileStep duplicate => duplicate.Source,
        RobocopyStep robocopy => robocopy.SourceDirectory,
        _ => string.Empty,
    };

    /// <summary>The Win32 code inside an HRESULT_FROM_WIN32, or 0 when the exception carries none.</summary>
    private static int Win32Code(Exception exception) =>
        (exception.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? exception.HResult & 0xFFFF : 0;

    /// <summary>
    /// The pipe name is on robocopy's command line and is not a secret; the nonce only keeps
    /// runs apart. A CSPRNG still, so a name cannot be predicted and squatted in advance.
    /// </summary>
    private static ulong NewPipeNonce()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    /// <summary>Blocking file-system work on a dedicated thread, never on the caller's.</summary>
    private static Task<T> RunBlockingAsync<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static Task RunBlockingAsync(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    /// <summary>
    /// Resumes on the thread pool after <paramref name="task"/> completes, whatever thread
    /// completed it. Without this, a task completed on the UI thread (the conflict dialog,
    /// the clipboard clear) would run the job's next file-system work inline on that thread.
    /// </summary>
    private static Task<T> OnThreadPool<T>(Task<T> task) =>
        task.ContinueWith(static t => t, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

    // ---------------------------------------------------------------- nested types

    /// <summary>One sink/event delivery: the job's creation, or a state change with the summary of a terminal one.</summary>
    private sealed record Notice(JobDescription? Created, StateChange? Change, JobSummary? Summary);

    private sealed record Cleanup(IReadOnlyList<string> LeftInPlace, List<(int StepIndex, PlannedFile File)>[] DamagedByPart);

    /// <summary>One executed plan with its ledger. Guarded by the job's lock, except <see cref="Plan"/>, which is immutable.</summary>
    private sealed class LedgerPart(ExecutionPlan plan)
    {
        public ExecutionPlan Plan { get; } = plan;

        public StepLedger Ledger { get; } = new(plan);

        /// <summary>Renames that crossed volumes after all, to run again as robocopy moves (worker thread only).</summary>
        public List<RenameStep> Replans { get; } = [];

        /// <summary>Files a cancel left possibly incomplete, for "Finish copying them".</summary>
        public List<(int StepIndex, PlannedFile File)> Damaged { get; } = [];
    }

    /// <summary>Synchronous on the scan thread, so no report can arrive after Scanning has ended.</summary>
    private sealed class ScanProgressReporter(Job job) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (job._lock)
            {
                if (job._lifecycle.State != JobState.Scanning)
                {
                    return;
                }
                job._totalFiles = value.Files;
                job._totalBytes = value.Bytes;
                job.Touch();
            }
        }
    }

    /// <summary>Tags every callback with its run, so a late callback of a finished run is ignored.</summary>
    private sealed class RunObserver(Job job, StepLedger ledger, int stepIndex, int run) : IRobocopyObserver
    {
        public void OnLines(IReadOnlyList<string> lines) => job.OnRunLines(run, lines);

        public void OnEvents(IReadOnlyList<RobocopyEvent> events) => job.OnRunEvents(ledger, stepIndex, run, events);

        public void OnBytesRead(long bytes, DateTimeOffset at) => job.OnRunBytes(run, bytes, at);

        public void OnSuspendedForCancel(int processId) => job.ObserveAtKill(ledger, stepIndex, run, processId);
    }
}
