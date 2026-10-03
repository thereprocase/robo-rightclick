namespace RoboRightClick.Core;

/// <summary>
/// An immutable view of one job, produced by the host's job engine under the job's lock
/// and handed to the UI thread. Everything the tray, the Jobs window and toasts show
/// comes from snapshots, never from live job objects.
/// </summary>
/// <param name="Sources">The top-level items pasted, as they came from the clipboard.</param>
/// <param name="Logging">The mode the job was created under; a later toggle does not change it.</param>
/// <param name="ErrorCount">
/// Errors the job reported (robocopy and in-process, link folders it could not create); see
/// <see cref="RefusedCount"/> for refusals. Not every error can be retried:
/// <see cref="RetryCount"/> is what "Try again" would repeat.
/// </param>
/// <param name="Acknowledged">
/// The user dealt with a DoneWithErrors/Failed/damaged-Canceled outcome: chose Skip, or
/// started Try again. Opening the summary and closing it without a choice does not count,
/// so the job keeps its attention state until the user decides.
/// </param>
public sealed record JobSnapshot(
    Guid Id,
    Guid? ParentId,
    TransferVerb Verb,
    IReadOnlyList<string> Sources,
    string Destination,
    JobState State,
    LoggingMode Logging,
    DateTimeOffset CreatedAt,
    long DoneBytes,
    long TotalBytes,
    long DoneFiles,
    long TotalFiles,
    double? BytesPerSecond,
    TimeSpan? Remaining,
    int ErrorCount,
    bool Acknowledged)
{
    /// <summary>Items refused at planning (into own subfolder, a drive root, a folder link). "Try again" cannot help these.</summary>
    public int RefusedCount { get; init; }

    /// <summary>The first refusal's reason: a fixed planner sentence that never contains a path.</summary>
    public string? RefusalReason { get; init; }

    /// <summary>
    /// Files a cancel left that may hold partial data: pre-existing files a replacing run was
    /// writing, and new files nothing proved robocopy was writing (cancel cleanup deletes
    /// only proven partial copies). Robocopy allocates full length first, so they may look
    /// whole.
    /// </summary>
    public int DamagedOnCancel { get; init; }

    /// <summary>Why a Failed job could not run, as a plain sentence with no path (from <see cref="FailureText"/>).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Cancel was requested and cleanup is still running ("Canceling…").</summary>
    public bool CancelRequested { get; init; }

    /// <summary>Why a Queued job has not started.</summary>
    public JobWait Wait { get; init; }

    /// <summary>The paste had nothing to do (every item already in place): no toast, no clipboard clear.</summary>
    public bool NoOp { get; init; }

    /// <summary>
    /// The job's pause latch is closed (Pause in any window, or Pause all) and it has not
    /// ended. Before Running this is the only sign of the pause ("Paused (waiting)"); once
    /// Running, the state follows it to Paused. Every window reads the same flag.
    /// </summary>
    public bool PauseRequested { get; init; }

    /// <summary>
    /// Files left alone because a file with the same name appeared at the destination after
    /// the scan (robocopy's skip flags kept it). Not an error and not retryable; the summary
    /// lists them so the user can check what is there.
    /// </summary>
    public int SkippedAppeared { get; init; }

    /// <summary>
    /// Files whose robocopy run started and then failed or died before finishing them, with
    /// something at their destination afterwards (<see cref="StepLedger.SuspectedPartials"/>),
    /// in any end state: a run that dies after other files completed leaves the job
    /// DoneWithErrors, and its in-flight files are still listed. For a cancel: those robocopy
    /// reported failing, and earlier steps' failures; the files the cancel itself interrupted
    /// are <see cref="DamagedOnCancel"/>.
    /// For a "Try again" child, in any end state, also the files the earlier paste left possibly
    /// incomplete that the user chose to keep (<see cref="FileConflict.SuspectedPartial"/>).
    /// Robocopy allocates each file at full length before writing it, so these can look
    /// complete while holding only part of the data.
    /// </summary>
    public int MayBeIncomplete { get; init; }

    /// <summary>
    /// What "Try again" would repeat, counted from the plan it would build
    /// (<see cref="RetryPlanner.CountOf"/>): files of robocopy steps, plus whole in-process steps.
    /// Fixed when the job ends; 0 before. One rule for the button's number and for whether there
    /// is a button at all, so an error nothing can repeat never offers "Try again".
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// The job's ledger lost track of robocopy's paths (<see cref="StepLedger.PathsUnreliable"/>):
    /// it cannot say which files failed. One of the facts <see cref="RetryRules.ActionFor"/>
    /// decides from.
    /// </summary>
    public bool PathsUnreliable { get; init; }

    /// <summary>
    /// "Try again" re-runs the whole paste with a new scan instead of repeating files
    /// (<see cref="RetryRules.ActionFor"/>). Derived, never set: the button's label and what the
    /// button does come from the same rule.
    /// </summary>
    public bool RetriesWholePaste => RetryRules.ActionFor(this) == RetryAction.WholePaste;

    /// <summary>
    /// How many of <see cref="MayBeIncomplete"/> are files the earlier paste may have left half
    /// written that the user chose to keep in this "Try again" child
    /// (<see cref="FileConflict.SuspectedPartial"/>). Nothing this job does repeats them, so the
    /// summary says how to replace them rather than pointing at "Try again".
    /// </summary>
    public int KeptIncomplete { get; init; }

    /// <summary>
    /// The job "Try again" started for this one. "Try again" is offered once: a second child
    /// would repeat the same files over whatever the first one, or the user, put there since.
    /// </summary>
    public Guid? RetriedBy { get; init; }

    /// <summary>
    /// The finished job left something for the user to deal with: errors, a failure, or (for a
    /// cancel) files that may be incomplete or failed before the cancel. A cancel is the user's
    /// own action, but it does not undo what went wrong before it.
    /// </summary>
    public bool OutcomeNeedsUser => State switch
    {
        JobState.DoneWithErrors or JobState.Failed => true,
        JobState.Canceled => DamagedOnCancel > 0 || MayBeIncomplete > 0 || ErrorCount > 0,
        JobState.Done => MayBeIncomplete > 0,
        _ => false,
    };

    public bool NeedsAttention =>
        State == JobState.AwaitingDecision
        || (!Acknowledged && OutcomeNeedsUser);
}

public enum TrayIconState
{
    Idle,
    Running,
    Paused,

    /// <summary>A conflict dialog is waiting, or a job ended with errors the user has not looked at.</summary>
    Attention,
}

/// <param name="Ephemeral">Current mode, drawn as a distinct tint on every icon state.</param>
public sealed record TrayStatus(TrayIconState Icon, bool Ephemeral, string Tooltip)
{
    /// <summary>NotifyIcon.Text throws above 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    public static TrayStatus Derive(IReadOnlyList<JobSnapshot> jobs, LoggingMode currentMode)
    {
        var ephemeral = currentMode == LoggingMode.Ephemeral;
        var active = jobs.Where(j => !JobStates.IsTerminal(j.State)).ToList();
        var attention = jobs.Count(j => j.NeedsAttention);
        var paused = active.Count(j => j.State == JobState.Paused);
        var moving = active.Count - paused;

        var icon = attention > 0 ? TrayIconState.Attention
            : moving > 0 ? TrayIconState.Running
            : paused > 0 ? TrayIconState.Paused
            : TrayIconState.Idle;

        var title = ephemeral ? AppInfo.Name + " (ephemeral)" : AppInfo.Name;
        var parts = new List<string>();
        if (active.Count > 0)
        {
            var jobsText = active.Count == 1 ? "1 job" : $"{active.Count} jobs";
            if (paused > 0)
            {
                jobsText += moving == 0 ? " paused" : $" ({paused} paused)";
            }
            parts.Add(jobsText);

            // A job being canceled no longer moves data; counting it would show a speed and
            // an ETA for work that will not happen.
            var running = active.Where(j => j.State == JobState.Running && !j.CancelRequested).ToList();
            var speed = running.Sum(j => j.BytesPerSecond ?? 0);
            if (speed > 0)
            {
                parts.Add(DisplayText.Speed(speed));
            }
            var remaining = running.Select(j => j.Remaining).Where(r => r is not null).Max();
            if (remaining is { } r)
            {
                parts.Add(DisplayText.Duration(r));
            }
        }
        if (attention > 0)
        {
            parts.Add(attention == 1 ? "1 needs attention" : $"{attention} need attention");
        }

        var tooltip = parts.Count == 0 ? title : title + "\n" + string.Join(" · ", parts);
        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..(MaxTooltipLength - 1)] + "…";
        }
        return new TrayStatus(icon, ephemeral, tooltip);
    }
}
