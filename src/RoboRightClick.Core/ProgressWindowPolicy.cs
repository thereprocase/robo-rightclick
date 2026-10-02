namespace RoboRightClick.Core;

/// <summary>What a job's progress window does after a snapshot.</summary>
public enum ProgressWindowAction
{
    /// <summary>The job is still going: keep showing progress.</summary>
    Stay,

    /// <summary>Nothing left for the user to see: close the window.</summary>
    Close,

    /// <summary>The outcome needs the user: the window turns into the error summary.</summary>
    ShowSummary,
}

/// <summary>
/// When a per-job progress window opens and what it does when the job ends. Explorer's
/// copy dialog appears only for operations that take a moment, so tiny pastes do not flash
/// a window; and it closes on success but stays to report errors.
/// </summary>
public static class ProgressWindowPolicy
{
    /// <summary>How long after a job is created its window opens.</summary>
    public static readonly TimeSpan OpenDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether to open the window once <see cref="OpenDelay"/> has passed. A job that is
    /// gone or already finished gets no window: its outcome reaches the user through the
    /// toast (errors always notify) and the tray icon's attention state.
    /// </summary>
    /// <param name="atDelay">The job's snapshot when the delay ran out; null if the job no longer exists.</param>
    /// <param name="showProgressWindow">The showProgressWindow setting at that moment.</param>
    public static bool ShouldOpen(JobSnapshot? atDelay, bool showProgressWindow) =>
        showProgressWindow && atDelay is not null && !JobStates.IsTerminal(atDelay.State);

    /// <summary>
    /// Done, a no-op and a plain cancel close the window. DoneWithErrors, Failed and a cancel
    /// that left replaced files partly written show the summary, because each one needs a
    /// "Try again" or "Skip" from the user. Non-terminal states stay.
    /// </summary>
    public static ProgressWindowAction OnTerminal(JobSnapshot job) => job.State switch
    {
        JobState.DoneWithErrors or JobState.Failed => ProgressWindowAction.ShowSummary,
        JobState.Canceled when job.DamagedOnCancel > 0 => ProgressWindowAction.ShowSummary,
        JobState.Done or JobState.Canceled => ProgressWindowAction.Close,
        _ => ProgressWindowAction.Stay,
    };
}
