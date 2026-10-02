namespace RoboRightClick.Core;

/// <summary>What one executed step produced.</summary>
/// <param name="CompletedSources">Source paths the step finished (robocopy FileReported, or an in-process copy/rename).</param>
/// <param name="ExitCode">Robocopy's exit code; null for in-process steps.</param>
/// <param name="Failure">Set when the step could not run at all: launch failure, an untrusted pipe client, an exception.</param>
public sealed record StepOutcome(
    IReadOnlyList<string> CompletedSources,
    IReadOnlyList<ErrorReported> Errors,
    RobocopyExitCode? ExitCode,
    string? Failure);

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

public static class RetryPlanner
{
    /// <summary>
    /// The child job for "Try again (N)": only the failed sources, grouped into robocopy
    /// steps by (source folder, destination folder), non-recursive, same Move flag as the
    /// original, chunked like <see cref="PastePlanner"/>. Policy is Replace: a failed
    /// file's destination was either new or one the user already chose to overwrite, and
    /// robocopy pre-allocates full-length files, so a partial copy must not be mistaken
    /// for a finished one. Failed in-process steps are repeated as they were. Returns
    /// null when none of <paramref name="failedSources"/> belongs to the plan.
    /// </summary>
    public static ExecutionPlan? ForFailures(ExecutionPlan original, IEnumerable<string> failedSources) =>
        throw new NotImplementedException();
}
