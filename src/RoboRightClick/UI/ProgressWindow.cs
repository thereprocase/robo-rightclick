using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// The per-job counterpart of Explorer's copy dialog, so a paste always shows that it is
/// working. Opened by the tray about one second after a job is created (so tiny pastes do
/// not flash a window; timing from Core's ProgressWindowPolicy), when showProgressWindow
/// is on. Shows: title "Robo-Copy: 3 items → Archive" (ephemeral: "Robo-Copy job", no
/// names), state text including the queue reason (<see cref="JobSnapshot.Wait"/>) and
/// "Discovered N items (X GB)" while scanning, a progress bar, bytes and files done of
/// total, speed and ETA, and Pause/Resume, Cancel ("Canceling…" and disabled while
/// <see cref="JobSnapshot.CancelRequested"/>), "More details" (Jobs window). Polls
/// <see cref="JobManager.SnapshotOf"/> every 250 ms while visible. On Done it closes; on
/// DoneWithErrors, Failed or a damaging cancel it turns into the
/// <see cref="ErrorSummaryDialog"/> content instead of closing. It owns the job's
/// <see cref="ConflictDialog"/>, which is how that dialog comes to the front.
/// </summary>
internal sealed class ProgressWindow : Form
{
    public ProgressWindow(Guid jobId, JobManager jobs)
    {
        JobId = jobId;
        Jobs = jobs;
    }

    public Guid JobId { get; }

    public JobManager Jobs { get; }
}
