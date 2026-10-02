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
/// RobocopyArgs.Build(step, settings, policy, PipeNames.ForStep(id, index, CSPRNG nonce)).
/// Events go through the ledger; observed bytes = ledger.CompletedBytesOfFinishedSteps +
/// this run's read counter, from callbacks of the current run only. ShellNotify after each
/// step. A closed gate before Running moves straight on to Paused; the loop waits on the
/// gate between steps and before Finalizing; JobProgress.ResetRate on resume.</para>
/// <para>Cancel (ignored once Finalizing): CancelRequested in the snapshot at once; kill
/// and wait; then CancelCleanup.Select(ledger.StartedRobocopyFiles, ledger.CompletedSources,
/// presence set, move, File.Exists on sources, Claims.ClaimedByOtherJob), skipped entirely
/// when ledger.PathsUnreliable. Each delete opens the file with
/// FILE_FLAG_OPEN_REPARSE_POINT and refuses a reparse point; LeftInPlace becomes
/// <see cref="JobSnapshot.DamagedOnCancel"/>.</para>
/// <para>Finalizing: create LinkFolders empty, clear the clipboard for a cut that ended
/// Done with at least one item moved, ShellNotify, emit the JobSummary (errors capped at
/// <see cref="JobRecords.MaxRecordedErrors"/>), then JobOutcome.FinalState. A Failed job's
/// <see cref="JobSnapshot.FailureReason"/> comes from <see cref="FailureText.Describe"/>.
/// Any unexpected exception, in any state, ends the job Failed.</para>
/// <para>The job never deletes a source: only robocopy /MOV(E) and renames move anything.</para>
/// </remarks>
internal sealed class Job
{
    public Job(JobStart start, JobServices services)
    {
        Start = start;
        Services = services;
    }

    public JobStart Start { get; }

    public JobServices Services { get; }

    public Guid Id => Start.Id;

    public JobFootprint Footprint => JobFootprint.Of(Start.Order);

    public JobState State => throw new NotImplementedException();

    /// <summary>Executed plan, available once Running; the input to RetryPlanner.</summary>
    public ExecutionPlan? Plan => throw new NotImplementedException();

    /// <summary>Per-file errors kept so far (first <see cref="JobRecords.MaxRecordedErrors"/>).</summary>
    public IReadOnlyList<ErrorReported> Errors => throw new NotImplementedException();

    /// <summary>Files "Try again" would repeat, and failed in-process steps (from the ledger).</summary>
    public ExecutionPlan? RetryPlan() => throw new NotImplementedException();

    /// <summary>Destinations this job planned, for <see cref="IDestinationClaims"/>.</summary>
    public bool Claims(string destinationPath) => throw new NotImplementedException();

    /// <summary>Raised from worker threads on state changes only; progress is pulled through <see cref="Snapshot"/>.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Runs the whole lifecycle. Never throws: an unexpected exception ends the job Failed with the message recorded.</summary>
    public Task RunAsync() => throw new NotImplementedException();

    /// <summary>Latched in any non-terminal state (see <see cref="PauseGate"/>).</summary>
    public void Pause() => throw new NotImplementedException();

    public void Resume() => throw new NotImplementedException();

    /// <summary>Valid until Finalizing, ignored after; returns immediately, RunAsync finishes the cleanup.</summary>
    public void Cancel() => throw new NotImplementedException();

    /// <summary>The user has seen this job's errors or damage (clears the attention state).</summary>
    public void Acknowledge() => throw new NotImplementedException();

    public JobSnapshot Snapshot() => throw new NotImplementedException();

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
