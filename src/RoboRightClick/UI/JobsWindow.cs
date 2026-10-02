using RoboRightClick.Jobs;
using RoboRightClick.Logging;

namespace RoboRightClick.UI;

/// <summary>
/// One row per job (newest first): verb, sources summary, destination, state (with
/// "Canceling…", "Paused (waiting)" and the queue reason), progress bar, bytes done/total,
/// files done/total, speed, ETA, error count. Row actions: pause, resume, cancel, try again
/// / show errors (<see cref="ErrorSummaryDialog"/>), open destination, open log (normal-mode
/// jobs whose folder exists). Selecting a job AwaitingDecision brings its conflict dialog to
/// the front. A filter shows only jobs needing attention (toast click). Rows of ephemeral
/// jobs show names (memory only). All text from <see cref="Core.DisplayText"/>. Single
/// instance, reused and activated by the tray; hides rather than closes. While visible, a
/// 250 ms WinForms timer pulls <see cref="JobManager.Snapshots"/> and updates rows in place
/// (BeginUpdate/EndUpdate); hidden, it does no work.
/// </summary>
internal sealed class JobsWindow : Form
{
    public JobsWindow(JobManager jobs, JobLogStore logStore)
    {
        Jobs = jobs;
        LogStore = logStore;
    }

    public JobManager Jobs { get; }

    public JobLogStore LogStore { get; }

    /// <summary>Show, restore and bring to front; optionally select a job, or show only jobs needing attention.</summary>
    public void ShowJobs(Guid? select = null, bool attentionOnly = false) => throw new NotImplementedException();
}
