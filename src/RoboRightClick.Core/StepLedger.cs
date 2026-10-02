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
/// nothing for this job and retry falls back to whole steps, because a mismatch means the
/// ledger can no longer tell finished files from partial ones.</para>
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
/// </remarks>
public sealed class StepLedger
{
    public StepLedger(ExecutionPlan plan)
    {
        Plan = plan;
    }

    public ExecutionPlan Plan { get; }

    /// <summary>A reported path matched nothing planned; see the remarks.</summary>
    public bool PathsUnreliable => throw new NotImplementedException();

    public void StepStarted(int stepIndex) => throw new NotImplementedException();

    public LedgerUpdate Apply(int stepIndex, RobocopyEvent robocopyEvent) => throw new NotImplementedException();

    /// <summary>An in-process step finished its file (rename, duplicate, keep-both).</summary>
    public void InProcessCompleted(int stepIndex) => throw new NotImplementedException();

    /// <param name="killedByCancel">The app killed the run because the user canceled.</param>
    public void StepFinished(int stepIndex, RobocopyExitCode? exitCode, bool killedByCancel) =>
        throw new NotImplementedException();

    /// <summary>Normalized source paths reported complete, for cancel cleanup.</summary>
    public IReadOnlyCollection<string> CompletedSources => throw new NotImplementedException();

    /// <summary>Planned files of the robocopy steps that started: cancel cleanup's candidates.</summary>
    public IReadOnlyList<PlannedFile> StartedRobocopyFiles => throw new NotImplementedException();

    /// <summary>Bytes of completed files in steps that have finished: the base for observed bytes.</summary>
    public long CompletedBytesOfFinishedSteps => throw new NotImplementedException();

    /// <summary>Files "Try again" should repeat; empty for a job whose paths were unreliable (retry whole steps instead).</summary>
    public IReadOnlyList<(int StepIndex, PlannedFile File)> Retryable => throw new NotImplementedException();

    /// <summary>In-process steps that failed (not counting <see cref="StepOutcome.ReplanAsMove"/>).</summary>
    public IReadOnlyList<int> FailedInProcessSteps => throw new NotImplementedException();

    /// <summary>Files left alone because their name appeared at the destination during the run.</summary>
    public IReadOnlyList<PlannedFile> SkippedLateArrivals => throw new NotImplementedException();
}
