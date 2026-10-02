using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>Shared services a job uses; one instance for the app.</summary>
/// <param name="ClearClipboardIfUnchanged">Posts to the UI thread (the clipboard owner window lives there).</param>
internal sealed record JobServices(
    IJobPrompts Prompts,
    FileSystemFacts FileSystem,
    ProgressSampler Sampler,
    TimeProvider Time,
    Func<uint, Task<bool>> ClearClipboardIfUnchanged);

/// <summary>Everything fixed when a job is created. Settings are captured here, so a later change (including the ephemeral toggle) affects only new jobs.</summary>
/// <param name="RetryPlan">Set for a "Try again" child: scanning and the conflict prompt are skipped.</param>
/// <param name="CutClipboardSequence">For a paste of cut data: the clipboard sequence number read at paste time.</param>
internal sealed record JobStart(
    Guid Id,
    Guid? ParentId,
    PasteRequest Request,
    ExecutionPlan? RetryPlan,
    Settings Settings,
    IJobSink Sink,
    uint? CutClipboardSequence,
    DateTimeOffset CreatedAt);

/// <summary>
/// One paste, from Queued to a terminal state. <see cref="RunAsync"/> runs on a
/// thread-pool thread; control methods and <see cref="Snapshot"/> may be called from any
/// thread and synchronize on one private lock that also guards the lifecycle, progress
/// and error list.
/// </summary>
/// <remarks>
/// Flow: Scanning (PastePlanner.Plan, then JobScanner.Scan) → AwaitingDecision when
/// there are conflicts and the policy is Ask (IJobPrompts) → Running: the steps of
/// ExecutionPlanner.Apply in order: RenameStep by InProcessCopier.Rename (an
/// ERROR_NOT_SAME_DEVICE item is re-run as a robocopy /MOVE step), DuplicateFileStep and
/// KeepBothStep by InProcessCopier.CopyFileAsync, RobocopyStep by RobocopyRun with
/// RobocopyArgs.Build(step, settings, policy, PipeNames.ForStep(...), excludedFiles).
/// Pause waits between steps and is forwarded into the running step. Cancel: kill,
/// then CancelCleanup.Select over the started steps' files and delete the selected
/// destinations if present. → Finalizing: create LinkFolders (empty, as Explorer leaves
/// them), clear the clipboard for a cut that ended Done, emit the JobSummary →
/// JobOutcome.FinalState. Every state change and output line also goes to the sink.
/// The job never deletes a source: only robocopy /MOV(E) and renames move anything.
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

    public JobState State => throw new NotImplementedException();

    /// <summary>Executed plan, available once Running; the input to RetryPlanner.</summary>
    public ExecutionPlan? Plan => throw new NotImplementedException();

    /// <summary>Per-file errors collected so far (robocopy and in-process).</summary>
    public IReadOnlyList<ErrorReported> Errors => throw new NotImplementedException();

    /// <summary>Raised from worker threads on state, progress and error changes. JobManager coalesces these.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs the whole lifecycle. Never throws: an unexpected exception ends the job Failed with the message recorded.</summary>
    public Task RunAsync() => throw new NotImplementedException();

    public void Pause() => throw new NotImplementedException();

    public void Resume() => throw new NotImplementedException();

    /// <summary>Valid in any non-terminal state; returns immediately, RunAsync finishes the cleanup.</summary>
    public void Cancel() => throw new NotImplementedException();

    /// <summary>The user has seen this job's errors (clears the attention state).</summary>
    public void Acknowledge() => throw new NotImplementedException();

    public JobSnapshot Snapshot() => throw new NotImplementedException();

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
