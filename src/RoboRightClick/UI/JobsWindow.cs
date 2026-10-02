using RoboRightClick.Jobs;
using RoboRightClick.Logging;

namespace RoboRightClick.UI;

/// <summary>
/// One row per job (newest first): verb, sources summary, destination, state, progress
/// bar, bytes done/total, files done/total, speed, ETA, error count. Row actions: pause,
/// resume, cancel, try again / show errors (<see cref="ErrorSummaryDialog"/>), open
/// destination, open log (normal-mode jobs whose folder exists). All text from
/// <see cref="Core.DisplayText"/>. Single instance, reused and activated by the tray;
/// hides rather than closes. While visible, a 250 ms WinForms timer pulls
/// <see cref="JobManager.Snapshot"/>; hidden, it does no work.
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

    /// <summary>Show, restore and bring to front; optionally select a job (toast click).</summary>
    public void ShowJobs(Guid? select = null) => throw new NotImplementedException();
}
