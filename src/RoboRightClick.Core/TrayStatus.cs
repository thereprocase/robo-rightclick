namespace RoboRightClick.Core;

/// <summary>
/// An immutable view of one job, produced by the host's job engine under the job's lock
/// and handed to the UI thread. Everything the tray, the Jobs window and toasts show
/// comes from snapshots, never from live job objects.
/// </summary>
/// <param name="Logging">The mode the job was created under; a later toggle does not change it.</param>
/// <param name="Acknowledged">The user has seen a DoneWithErrors/Failed outcome (opened it in Jobs or chose Skip).</param>
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
    public bool NeedsAttention =>
        State == JobState.AwaitingDecision
        || (!Acknowledged && State is JobState.DoneWithErrors or JobState.Failed);
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

            var running = active.Where(j => j.State == JobState.Running).ToList();
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
