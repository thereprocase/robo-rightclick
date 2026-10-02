namespace RoboRightClick.Core;

public enum ToastKind
{
    Info,
    Warning,
    Error,
}

public sealed record Toast(string Title, string Body, ToastKind Kind);

/// <summary>
/// Notification text. Windows keeps toast text in the notification center database,
/// so a job created in ephemeral mode gets text with no file or folder names at all
/// (product invariant 2). The job's own mode decides, not the current one.
/// </summary>
public static class ToastText
{
    public const string EphemeralBody = "Open Jobs from the tray icon for details.";

    /// <summary>
    /// The completion toast, or null for none. notifyOnComplete silences only clean
    /// finishes: errors need the user's "Try again / Skip", so they always notify.
    /// A cancel was the user's own action and gets no toast.
    /// </summary>
    public static Toast? ForFinished(JobSnapshot job, bool notifyOnComplete)
    {
        if (job.State is not (JobState.Done or JobState.DoneWithErrors or JobState.Failed))
        {
            return null;
        }
        if (job.State == JobState.Done && !notifyOnComplete)
        {
            return null;
        }

        var kind = job.State switch
        {
            JobState.Done => ToastKind.Info,
            JobState.DoneWithErrors => ToastKind.Warning,
            _ => ToastKind.Error,
        };

        if (job.Logging == LoggingMode.Ephemeral)
        {
            var title = job.State switch
            {
                JobState.Done => "Job finished",
                JobState.DoneWithErrors => "Job finished with errors",
                _ => "Job failed",
            };
            return new Toast(title, EphemeralBody, kind);
        }

        var verb = job.Verb == TransferVerb.Move ? "Move" : "Copy";
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;
        return job.State switch
        {
            JobState.Done => new Toast(
                $"{verb} finished",
                $"{DisplayText.Items(job.DoneFiles)} ({DisplayText.Bytes(job.DoneBytes)}) to {into}",
                kind),
            JobState.DoneWithErrors => new Toast(
                $"{verb} finished with errors",
                $"{DisplayText.Items(job.ErrorCount)} could not be {(job.Verb == TransferVerb.Move ? "moved" : "copied")} to {into}. Open Jobs to try again.",
                kind),
            _ => new Toast($"{verb} failed", $"Nothing was {(job.Verb == TransferVerb.Move ? "moved" : "copied")} to {into}. Open Jobs for details.", kind),
        };
    }

    /// <summary>Path-free in every mode: setting names only.</summary>
    public static Toast ForSettingsProblems(int problemCount) => new(
        "Settings problem",
        problemCount == 1
            ? "One setting in config.json was invalid; its default is used."
            : $"{problemCount} settings in config.json were invalid; their defaults are used.",
        ToastKind.Warning);
}
