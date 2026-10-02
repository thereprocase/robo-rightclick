using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// Opens and tracks the per-job <see cref="ProgressWindow"/>s. The integration forwards
/// <see cref="JobManager.Created"/> here; <see cref="UiPrompts"/> asks it for a job's window
/// to own that job's conflict dialog. UI thread only.
/// </summary>
internal sealed class ProgressWindowHost : IDisposable
{
    public ProgressWindowHost(JobManager jobs, Func<Settings> settings, JobsWindow jobsWindow)
    {
        Jobs = jobs;
        Settings = settings;
        JobsWindow = jobsWindow;
    }

    public JobManager Jobs { get; }

    public Func<Settings> Settings { get; }

    public JobsWindow JobsWindow { get; }

    /// <summary>
    /// A job was created: when showProgressWindow is on, open its window after the delay
    /// from Core's ProgressWindowPolicy unless the job already finished by then.
    /// </summary>
    public void JobCreated(Guid jobId) => throw new NotImplementedException();

    /// <summary>
    /// The job's progress window, opening it at once if it is not open yet (a conflict
    /// question must have an owner the user can see). Null when showProgressWindow is off.
    /// </summary>
    public Form? WindowFor(Guid jobId) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
