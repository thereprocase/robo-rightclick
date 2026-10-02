using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// Opens and tracks the per-job <see cref="ProgressWindow"/>s. The integration forwards
/// <see cref="JobManager.Created"/> here; <see cref="UiPrompts"/> asks it for a job's window
/// to own that job's conflict dialog. UI thread only.
/// </summary>
/// <remarks>
/// One window per job, tracked by job id; a window the user closed is forgotten, so a later
/// conflict question opens a fresh one. Each job waits for <see cref="ProgressWindowPolicy.OpenDelay"/>
/// on its own WinForms timer (UI thread, no locking), and the setting is read again when the
/// delay ends, so switching showProgressWindow off also stops windows already pending.
/// </remarks>
internal sealed class ProgressWindowHost : IDisposable
{
    private readonly Dictionary<Guid, ProgressWindow> _windows = [];
    private readonly Dictionary<Guid, System.Windows.Forms.Timer> _pending = [];
    private bool _disposed;

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
    /// from Core's ProgressWindowPolicy, unless the job ended cleanly by then. A job that
    /// already ended with errors gets its window too, opening straight into the summary.
    /// </summary>
    public void JobCreated(Guid jobId)
    {
        if (_disposed || !Settings().ShowProgressWindow || _pending.ContainsKey(jobId) || _windows.ContainsKey(jobId))
        {
            return;
        }
        var timer = new System.Windows.Forms.Timer { Interval = (int)ProgressWindowPolicy.OpenDelay.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            StopPending(jobId);
            if (!_disposed
                && !_windows.ContainsKey(jobId)
                && ProgressWindowPolicy.ShouldOpen(Jobs.SnapshotOf(jobId), Settings().ShowProgressWindow))
            {
                Open(jobId);
            }
        };
        _pending[jobId] = timer;
        timer.Start();
    }

    /// <summary>
    /// The job's progress window, opening it at once if it is not open yet (a conflict
    /// question must have an owner the user can see). Null when showProgressWindow is off.
    /// </summary>
    public Form? WindowFor(Guid jobId)
    {
        if (_disposed)
        {
            return null;
        }
        if (_windows.TryGetValue(jobId, out var open) && !open.IsDisposed)
        {
            return open;
        }
        if (!Settings().ShowProgressWindow)
        {
            return null;
        }
        StopPending(jobId);
        return Open(jobId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var id in _pending.Keys.ToList())
        {
            StopPending(id);
        }
        foreach (var window in _windows.Values.ToList())
        {
            if (!window.IsDisposed)
            {
                window.Close();
            }
        }
        _windows.Clear();
    }

    private ProgressWindow Open(Guid jobId)
    {
        var window = new ProgressWindow(jobId, Jobs);
        window.MoreDetails += (_, id) => JobsWindow.ShowJobs(id);
        window.FormClosed += (_, _) =>
        {
            if (_windows.TryGetValue(jobId, out var current) && ReferenceEquals(current, window))
            {
                _windows.Remove(jobId);
            }
        };
        _windows[jobId] = window;
        window.Show();
        return window;
    }

    private void StopPending(Guid jobId)
    {
        if (_pending.Remove(jobId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
        }
    }
}
