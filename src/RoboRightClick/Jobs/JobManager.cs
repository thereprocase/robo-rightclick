using RoboRightClick.Core;
using RoboRightClick.Logging;

namespace RoboRightClick.Jobs;

/// <summary>
/// Owns every job of this session: creation, the start queue, control, in-memory
/// history and snapshots for the UI. Public methods are thread-safe. Events are raised
/// on the UI thread via the SynchronizationContext given at construction.
/// </summary>
internal sealed class JobManager : IDisposable
{
    /// <summary>Finished jobs kept in memory for the Jobs window (both modes); oldest dropped first.</summary>
    public const int InMemoryHistoryLimit = 500;

    public JobManager(SynchronizationContext ui, Func<Settings> currentSettings, JobServices services, JobLogStore logStore)
    {
        Ui = ui;
        CurrentSettings = currentSettings;
        Services = services;
        LogStore = logStore;
    }

    public SynchronizationContext Ui { get; }
    public Func<Settings> CurrentSettings { get; }
    public JobServices Services { get; }
    public JobLogStore LogStore { get; }

    /// <summary>Something changed. Coalesced to at most ~4 per second; listeners pull <see cref="Snapshot"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>A job reached a terminal state; carries its final snapshot (for toasts).</summary>
    public event EventHandler<JobSnapshot>? Finished;

    public bool HasActiveJobs => throw new NotImplementedException();

    /// <summary>
    /// Creates a job with the current settings and the sink from
    /// <see cref="JobSinks.For"/>(settings.Logging, LogStore.CreateSink), then starts it if
    /// <see cref="JobQueuePolicy.FreeSlots"/> allows, otherwise leaves it Queued.
    /// </summary>
    public Guid Enqueue(PasteRequest request, uint? cutClipboardSequence) => throw new NotImplementedException();

    /// <summary>
    /// "Try again (N)": <see cref="RetryPlanner.ForFailures"/> over the parent's plan and
    /// errors, as a child job with ParentId set. Returns null if there is nothing to retry.
    /// The parent is acknowledged either way.
    /// </summary>
    public Guid? Retry(Guid parentId) => throw new NotImplementedException();

    public IReadOnlyList<JobSnapshot> Snapshot() => throw new NotImplementedException();

    public IReadOnlyList<ErrorReported> ErrorsOf(Guid jobId) => throw new NotImplementedException();

    public void Pause(Guid jobId) => throw new NotImplementedException();
    public void Resume(Guid jobId) => throw new NotImplementedException();
    public void Cancel(Guid jobId) => throw new NotImplementedException();
    public void Acknowledge(Guid jobId) => throw new NotImplementedException();
    public void PauseAll() => throw new NotImplementedException();
    public void ResumeAll() => throw new NotImplementedException();

    /// <summary>Exit path: cancel every active job and wait for their cleanup to finish.</summary>
    public Task CancelAllAndWaitAsync() => throw new NotImplementedException();

    public void Dispose()
    {
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void RaiseFinished(JobSnapshot job) => Finished?.Invoke(this, job);
}
