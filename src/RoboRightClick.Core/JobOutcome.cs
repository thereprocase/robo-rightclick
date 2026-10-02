namespace RoboRightClick.Core;

/// <summary>What one executed step produced.</summary>
/// <param name="CompletedSources">Source paths the step finished (robocopy FileReported, or an in-process copy/rename).</param>
/// <param name="ExitCode">Robocopy's exit code; null for in-process steps.</param>
/// <param name="Failure">Set when the step could not run at all: launch failure, an untrusted pipe client, an exception.</param>
public sealed record StepOutcome(
    IReadOnlyList<string> CompletedSources,
    IReadOnlyList<ErrorReported> Errors,
    RobocopyExitCode? ExitCode,
    string? Failure)
{
    /// <summary>
    /// A rename failed with ERROR_NOT_SAME_DEVICE: the planner's same-volume answer was
    /// wrong (a mount point, for example). Not an error: the job re-plans the item as a
    /// robocopy move, so this never counts towards DoneWithErrors.
    /// </summary>
    public bool ReplanAsMove { get; init; }
}

public static class JobOutcome
{
    /// <summary>
    /// The terminal state after Finalizing. Failed is reserved for jobs where nothing
    /// could run: per-file errors are DoneWithErrors, which is what offers "Try again".
    /// </summary>
    /// <param name="planIssues">Items refused at planning (into own subfolder, a drive root); Explorer reports these as errors.</param>
    public static JobState FinalState(IReadOnlyList<StepOutcome> outcomes, bool canceled, int planIssues)
    {
        if (canceled)
        {
            return JobState.Canceled;
        }

        var anyCompleted = outcomes.Any(o => o.CompletedSources.Count > 0);
        var anyBroken = outcomes.Any(o => o.Failure is not null || o.ExitCode is { FatalError: true });
        if (anyBroken && !anyCompleted)
        {
            return JobState.Failed;
        }

        var anyErrors = anyBroken
            || planIssues > 0
            || outcomes.Any(o => o.Errors.Count > 0 || o.ExitCode is { SomeCopiesFailed: true });
        return anyErrors ? JobState.DoneWithErrors : JobState.Done;
    }
}

/// <summary>
/// Plain-language reasons for a Failed job (<see cref="JobSnapshot.FailureReason"/>). Built
/// only from Windows error codes, robocopy operation names and system messages, never from
/// a path, so the text is safe in every mode.
/// </summary>
public static class FailureText
{
    /// <summary>
    /// One sentence for the first thing that broke: a launch failure, an untrusted pipe
    /// client, robocopy exit 16 with its first ERROR ("Access to the destination folder was
    /// denied (error 5)."), or a fatal exit with no ERROR line ("Robocopy stopped with a
    /// fatal error (exit code 16)."). Common codes get their own wording: 2/3 not found, 5
    /// access denied, 32 in use, 53/67 network path not found, 112 disk full, 1314 privilege.
    /// </summary>
    public static string Describe(StepOutcome outcome) => throw new NotImplementedException();
}

public static class RetryPlanner
{
    /// <summary>
    /// The child job for "Try again (N)": the files <see cref="StepLedger.Retryable"/>
    /// returned, as non-recursive robocopy steps grouped by (source folder, destination
    /// folder), with the original step's Move flag, chunked like <see cref="PastePlanner"/>.
    /// Policy is Replace: each of these files either failed with an error robocopy reported
    /// (so robocopy touched it) or was in flight when its run died, and robocopy pre-allocates
    /// full-length files, so a partial copy must not be mistaken for a finished one.
    /// Failed in-process steps (rename, duplicate, keep-both) are repeated as they were.
    /// <see cref="ExecutionPlan.PresentBeforeRun"/> carries over from the original, and
    /// <see cref="ExecutionPlan.LinkFolders"/> and Kept are empty. Returns null when
    /// <paramref name="retryable"/> is empty.
    /// </summary>
    public static ExecutionPlan? ForFailures(
        ExecutionPlan original,
        IReadOnlyList<(int StepIndex, PlannedFile File)> retryable,
        IReadOnlyList<int> failedInProcessSteps) =>
        throw new NotImplementedException();
}
