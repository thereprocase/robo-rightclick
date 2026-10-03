using System.Collections.Immutable;
using RoboRightClick.Core;
using RoboRightClick.Logging;

namespace RoboRightClick.Jobs;

/// <summary>
/// Owns every job of this session: creation, the start queue, control, in-memory
/// history and snapshots for the UI. Public methods are thread-safe. Events are raised
/// on the UI thread via the SynchronizationContext given at construction. The job list
/// lock is never held while calling into a job, the log store or an event handler.
/// </summary>
/// <remarks>
/// <para>Locks, in the only order they are ever taken: the scheduling lock (one queue pass
/// at a time), the snapshot-cache lock, a job's own lock. The job-list lock is a leaf: it
/// guards the list only. A job raises its events outside its lock, so the handler here may
/// take the scheduling lock without a cycle.</para>
/// <para>The job list is also published as a copy-on-write array, so the crash path
/// (<see cref="KillRunningProcesses"/>, <see cref="EphemeralJobsActive"/>) and claim checks
/// read it without any lock.</para>
/// </remarks>
internal sealed class JobManager : IDestinationClaims, IDisposable
{
    /// <summary>Finished jobs kept in memory for the Jobs window (both modes); oldest dropped first.</summary>
    public const int InMemoryHistoryLimit = 500;

    private readonly Lock _listLock = new();
    private readonly Lock _scheduleLock = new();
    private readonly Lock _snapshotLock = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly JobServices _services;

    /// <summary>Every job in history, in creation order. Replaced, never mutated, under <see cref="_listLock"/>.</summary>
    private Job[] _jobs = [];

    private int _activeCount;
    private int _ephemeralActiveCount;
    private volatile bool _ephemeralJobsThisSession;
    private volatile bool _pauseAll;
    private volatile bool _shuttingDown;

    // Snapshot cache: the job array it was built from, each job's version, and the result.
    private Job[] _cachedJobs = [];
    private long[] _cachedVersions = [];
    private IReadOnlyList<JobSnapshot> _cachedSnapshots = ImmutableArray<JobSnapshot>.Empty;

    public JobManager(SynchronizationContext ui, Func<Settings> currentSettings, JobServicesFactory services, JobLogStore logStore)
    {
        Ui = ui;
        CurrentSettings = currentSettings;
        ServicesFactory = services;
        LogStore = logStore;
        _services = services(this);
    }

    public SynchronizationContext Ui { get; }
    public Func<Settings> CurrentSettings { get; }
    public JobServicesFactory ServicesFactory { get; }
    public JobLogStore LogStore { get; }

    /// <summary>A job was created (the integration opens its progress window after a short delay).</summary>
    public event EventHandler<Guid>? Created;

    /// <summary>A job changed state. Progress is not an event: windows poll <see cref="Snapshots"/> while visible.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A job reached a terminal state; carries its final snapshot (for toasts).</summary>
    public event EventHandler<JobSnapshot>? Finished;

    /// <summary>Any job not yet seen to finish. Lock-free; errs towards "active" for the moment between a job ending and its event.</summary>
    public bool HasActiveJobs => Volatile.Read(ref _activeCount) > 0;

    /// <summary>
    /// For <see cref="App.CrashPolicy"/>: whether any non-terminal job is ephemeral. Lock-free
    /// (a volatile counter maintained on state changes), because it is read from a crashing
    /// thread that may hold any lock.
    /// </summary>
    public bool EphemeralJobsActive => Volatile.Read(ref _ephemeralActiveCount) > 0;

    /// <summary>
    /// For <see cref="App.CrashPolicy"/>'s crash log: whether any ephemeral job has been
    /// created in this session, finished or not, including ones since dropped from history.
    /// Set before such a job exists and never cleared, so it errs towards "yes". Lock-free.
    /// </summary>
    public bool EphemeralJobsThisSession => _ephemeralJobsThisSession;

    /// <summary>
    /// For <see cref="App.CrashPolicy"/>: best-effort, lock-free kill of every robocopy process
    /// the app started. Requests cancellation of every job without waiting; each robocopy
    /// also ends with the app through its kill-on-close job object.
    /// </summary>
    public void KillRunningProcesses()
    {
        foreach (var job in Volatile.Read(ref _jobs))
        {
            try
            {
                job.AbortWithoutWaiting();
            }
            catch (Exception)
            {
                // Best effort on a crashing process; the job object does the rest.
            }
        }
    }

    /// <summary>"Pause all" is on: running jobs are paused and new or queued jobs start paused.</summary>
    public bool PauseAllActive => _pauseAll;

    /// <summary>
    /// Creates a job with the current settings and logging mode and the sink from
    /// <see cref="JobSinks.For"/>(mode, LogStore.CreateSink). It starts when
    /// <see cref="JobQueuePolicy.WaitReason"/> over the active and older queued jobs'
    /// footprints says None; otherwise it waits in Queued with that reason, re-evaluated
    /// whenever any job changes state. Returns null, creating nothing, when
    /// <paramref name="cutClipboardSequence"/> equals that of a cut-paste that is still
    /// active (the same cut pasted twice; the caller shows
    /// <see cref="VerbRefusal.AlreadyBeingMoved"/>), and once shutdown has begun.
    /// </summary>
    public Guid? Enqueue(PasteOrder order, uint? cutClipboardSequence)
    {
        var settings = CurrentSettings();
        return Create(order, parentId: null, retryPlan: null, settings, settings.Logging, cutClipboardSequence, refuseRepeatedCut: true);
    }

    /// <summary>
    /// "Try again (N)": the parent's <see cref="Job.RetryPlan"/> as a child job with
    /// ParentId set and logging mode <see cref="JobSinks.ForDerivedJob"/>(parent, current).
    /// Returns null if there is nothing to retry, or when "Try again" was already used for
    /// this parent (<see cref="Job.RetriedBy"/>): a second child would repeat the same files over
    /// whatever the first child, or the user, has put there since. The parent is acknowledged
    /// either way. When the parent's robocopy paths could not be matched
    /// (<see cref="Job.PathsUnreliable"/>), or a Failed parent kept no per-file record (it failed
    /// before anything ran, or by an exception), this re-runs the whole paste instead
    /// (<see cref="Rerun"/>): it re-scans and asks about every file now present, rather than
    /// overwriting files whose state is unknown.
    /// </summary>
    public Guid? Retry(Guid parentId)
    {
        var parent = Acknowledged(parentId);
        if (parent is null || !parent.TryBeginRetry())
        {
            return null;
        }
        Guid? child = null;
        try
        {
            if (parent.PathsUnreliable)
            {
                child = Derive(parent, retryPlan: null);
            }
            else if (parent.RetryPlan() is { } plan)
            {
                child = Derive(parent, plan);
            }
            else if (parent.State == JobState.Failed)
            {
                child = Derive(parent, retryPlan: null);
            }
        }
        finally
        {
            parent.EndRetry(child);
            PostStateChanged();
        }
        return child;
    }

    /// <summary>The whole paste again as a new job (full re-scan), once per parent, same privacy rule as <see cref="Retry"/>.</summary>
    public Guid? Rerun(Guid parentId)
    {
        var parent = Acknowledged(parentId);
        if (parent is null || !parent.TryBeginRetry())
        {
            return null;
        }
        Guid? child = null;
        try
        {
            child = Derive(parent, retryPlan: null);
        }
        finally
        {
            parent.EndRetry(child);
            PostStateChanged();
        }
        return child;
    }

    /// <summary>Immutable snapshots, newest first; rebuilt only when something changed since the last call.</summary>
    /// <remarks>
    /// "Changed" is a new job list or a job whose <see cref="Job.Version"/> moved; only those
    /// jobs are snapshotted again. A version is read before its snapshot is taken, so a change
    /// racing the rebuild is seen as a change on the next call rather than lost.
    /// </remarks>
    public IReadOnlyList<JobSnapshot> Snapshots()
    {
        lock (_snapshotLock)
        {
            // Read inside the lock: a caller that read the list earlier and then waited for the
            // lock would otherwise cache an older list over a newer one.
            var jobs = Volatile.Read(ref _jobs);
            if (ReferenceEquals(jobs, _cachedJobs) && !VersionsChanged(jobs))
            {
                return _cachedSnapshots;
            }

            var previous = new Dictionary<Job, (long Version, JobSnapshot Snapshot)>(_cachedJobs.Length, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _cachedJobs.Length; i++)
            {
                previous[_cachedJobs[i]] = (_cachedVersions[i], _cachedSnapshots[_cachedJobs.Length - 1 - i]);
            }

            var versions = new long[jobs.Length];
            var snapshots = ImmutableArray.CreateBuilder<JobSnapshot>(jobs.Length);
            snapshots.Count = jobs.Length;
            for (var i = 0; i < jobs.Length; i++)
            {
                var job = jobs[i];
                var version = job.Version;
                versions[i] = version;
                snapshots[jobs.Length - 1 - i] = previous.TryGetValue(job, out var cached) && cached.Version == version
                    ? cached.Snapshot
                    : job.Snapshot();
            }

            _cachedJobs = jobs;
            _cachedVersions = versions;
            _cachedSnapshots = snapshots.MoveToImmutable();
            return _cachedSnapshots;
        }
    }

    public JobSnapshot? SnapshotOf(Guid jobId) => Find(jobId)?.Snapshot();

    public IReadOnlyList<ErrorReported> ErrorsOf(Guid jobId) => Find(jobId)?.Errors ?? [];

    /// <summary>Items refused with their reasons, for the error summary (the snapshot carries only the count and the first reason).</summary>
    public IReadOnlyList<PlanIssue> IssuesOf(Guid jobId) => Find(jobId)?.Issues ?? [];

    /// <summary>Destination files a cancel left partly replaced (<see cref="JobSnapshot.DamagedOnCancel"/> is the exact count).</summary>
    public IReadOnlyList<string> DamagedOf(Guid jobId) => Find(jobId)?.DamagedPaths ?? [];

    /// <summary>A Failed job's destinations that may hold partial data (<see cref="JobSnapshot.MayBeIncomplete"/> is the exact count).</summary>
    public IReadOnlyList<string> MayBeIncompleteOf(Guid jobId) => Find(jobId)?.MayBeIncompletePaths ?? [];

    /// <summary>Destinations skipped because their name appeared mid-copy (<see cref="JobSnapshot.SkippedAppeared"/> is the exact count).</summary>
    public IReadOnlyList<string> SkippedAppearedOf(Guid jobId) => Find(jobId)?.SkippedAppearedPaths ?? [];

    /// <summary>Log folder names of normal-mode jobs that have not finished, for <see cref="JobLogStore.Prune"/>.</summary>
    public IReadOnlySet<string> ActiveLogFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_listLock)
        {
            foreach (var entry in _entries.Values)
            {
                if (!entry.Finished && entry.Job.Start.Logging == LoggingMode.Normal)
                {
                    folders.Add(JobLogNames.FolderName(entry.Job.Start.CreatedAt, entry.Job.Id));
                }
            }
        }
        return folders;
    }

    /// <summary>Any other job of this session, running or in history, whose plan claims the normalized destination path.</summary>
    public bool ClaimedByOtherJob(Guid askingJob, string destinationPath)
    {
        foreach (var job in Volatile.Read(ref _jobs))
        {
            if (job.Id != askingJob && job.Claims(destinationPath))
            {
                return true;
            }
        }
        return false;
    }

    public void Pause(Guid jobId) => Find(jobId)?.Pause();

    public void Resume(Guid jobId) => Find(jobId)?.Resume();

    public void Cancel(Guid jobId) => Find(jobId)?.Cancel();

    /// <summary>Clears the job's attention state; raises <see cref="StateChanged"/> so the tray icon follows.</summary>
    public void Acknowledge(Guid jobId)
    {
        if (Find(jobId) is { } job)
        {
            job.Acknowledge();
            PostStateChanged();
        }
    }

    public void PauseAll()
    {
        _pauseAll = true;
        foreach (var job in Volatile.Read(ref _jobs))
        {
            job.Pause();
        }
        PostStateChanged();
    }

    public void ResumeAll()
    {
        _pauseAll = false;
        foreach (var job in Volatile.Read(ref _jobs))
        {
            job.Resume();
        }
        PostStateChanged();
    }

    /// <summary>
    /// Settings were reloaded: re-runs the start queue, so a raised maxConcurrentJobs starts
    /// waiting jobs now rather than at the next job state change. Captured job settings do not change.
    /// </summary>
    public void SettingsChanged()
    {
        Schedule();
        PostStateChanged();
    }

    /// <summary>
    /// Exit and sign-out path: cancel every active job and wait for cleanup, at most
    /// <paramref name="timeout"/>. Must be awaited with the UI message loop still pumping:
    /// jobs in Finalizing post to it. No job is created or started after this is called.
    /// </summary>
    public async Task CancelAllAndWaitAsync(TimeSpan timeout)
    {
        _shuttingDown = true;
        // Under the scheduling lock, so no pass is halfway through starting a job: every job
        // is either still Queued (canceled outright) or has its run recorded below.
        lock (_scheduleLock)
        {
            foreach (var job in Volatile.Read(ref _jobs))
            {
                job.Cancel();
            }
        }

        List<Task> runs;
        lock (_listLock)
        {
            runs = _entries.Values.Where(e => !e.Finished && e.Run is not null).Select(e => e.Run!).ToList();
        }
        await Task.WhenAny(Task.WhenAll(runs), Task.Delay(timeout, _services.Time)).ConfigureAwait(false);
    }

    /// <summary>After a normal-mode job finishes: prune off the UI thread with <see cref="ActiveLogFolders"/>.</summary>
    private void PruneLogsInBackground() =>
        _ = Task.Run(() =>
        {
            try
            {
                LogStore.Prune(ActiveLogFolders());
            }
            catch (Exception)
            {
                // Log write failures never fail anything; the next finish prunes again.
            }
        });

    /// <summary>Stops new jobs from being created or started. Running jobs are ended by <see cref="CancelAllAndWaitAsync"/>.</summary>
    public void Dispose()
    {
        _shuttingDown = true;
    }

    // ---------------------------------------------------------------- creation

    private Guid? Derive(Job parent, ExecutionPlan? retryPlan)
    {
        var settings = CurrentSettings();
        var mode = JobSinks.ForDerivedJob(parent.Start.Logging, settings.Logging);
        // The parent's clipboard sequence carries over: a retry of a cut that ends Done
        // clears the clipboard, and pasting the same cut while the retry runs is refused.
        return Create(parent.Start.Order, parent.Id, retryPlan, settings, mode, parent.Start.CutClipboardSequence, refuseRepeatedCut: false);
    }

    private Guid? Create(
        PasteOrder order,
        Guid? parentId,
        ExecutionPlan? retryPlan,
        Settings settings,
        LoggingMode mode,
        uint? cutClipboardSequence,
        bool refuseRepeatedCut)
    {
        if (_shuttingDown)
        {
            return null;
        }

        if (mode == LoggingMode.Ephemeral)
        {
            // Before the job holds any path in memory: a crash from here on writes no crash log.
            _ephemeralJobsThisSession = true;
        }

        var id = Guid.NewGuid();
        var createdAt = _services.Time.GetUtcNow();
        // Built before taking the list lock: the file sink only creates its folder on the
        // first write, and in ephemeral mode its factory is not even invoked.
        var sink = JobSinks.For(mode, () => LogStore.CreateSink(Job.DescriptionOf(id, order, createdAt)));
        var job = new Job(
            new JobStart(id, parentId, order, retryPlan, settings, mode, sink, cutClipboardSequence, _pauseAll, createdAt),
            _services);
        // Subscribed and announced before it is published: once in the list, a scheduling pass
        // on another thread may start it, and its first transition must reach this handler and
        // follow JobCreated in the sink. Nothing is delivered until it is registered.
        job.StateChanged += OnJobStateChanged;
        job.Announce();

        lock (_listLock)
        {
            if (refuseRepeatedCut
                && order.Verb == TransferVerb.Move
                && cutClipboardSequence is { } sequence
                && _entries.Values.Any(e => !e.Finished
                    && e.Job.Start.Order.Verb == TransferVerb.Move
                    && e.Job.Start.CutClipboardSequence == sequence))
            {
                return null;
            }
            _entries.Add(id, new Entry(job));
            _jobs = [.. _jobs, job];
            Interlocked.Increment(ref _activeCount);
            if (mode == LoggingMode.Ephemeral)
            {
                Interlocked.Increment(ref _ephemeralActiveCount);
            }
        }

        if (_pauseAll && !job.Start.StartPaused)
        {
            // "Pause all" was switched on while this job was being created.
            job.Pause();
        }
        else if (!_pauseAll && job.Start.StartPaused)
        {
            // "Resume all" ran between reading the flag and publishing this job, so its
            // pass missed it; without this it would stay paused with nothing to resume it.
            job.Resume();
        }
        job.DeliverInBackground();
        Ui.Post(_ => RaiseCreated(id), null);
        Schedule();
        PostStateChanged();
        return id;
    }

    private Job? Acknowledged(Guid parentId)
    {
        var parent = Find(parentId);
        if (parent is not null)
        {
            parent.Acknowledge();
            PostStateChanged();
        }
        return parent;
    }

    private Job? Find(Guid jobId)
    {
        lock (_listLock)
        {
            return _entries.TryGetValue(jobId, out var entry) ? entry.Job : null;
        }
    }

    // ---------------------------------------------------------------- queue and state changes

    /// <summary>
    /// One pass of <see cref="JobScheduler.Decide"/> over the current states. Passes are
    /// serialized; states are read one job at a time, which can only overstate how many hold
    /// a slot or scan (a job leaves those states on its own, but enters Scanning only here),
    /// and every later change runs another pass.
    /// </summary>
    private void Schedule()
    {
        lock (_scheduleLock)
        {
            var jobs = Volatile.Read(ref _jobs);
            var queued = new List<(Guid Id, JobFootprint Footprint)>();
            var queuedJobs = new Dictionary<Guid, Job>();
            var holding = new List<JobFootprint>();
            var scanning = 0;
            foreach (var job in jobs)
            {
                var state = job.State;
                if (state == JobState.Queued)
                {
                    queued.Add((job.Id, job.Footprint));
                    queuedJobs.Add(job.Id, job);
                }
                else if (JobQueuePolicy.HoldsSlot(state))
                {
                    holding.Add(job.Footprint);
                    if (state == JobState.Scanning)
                    {
                        scanning++;
                    }
                }
            }
            if (queued.Count == 0)
            {
                return;
            }

            var decisions = JobScheduler.Decide(queued, holding, holding.Count, scanning, CurrentSettings().MaxConcurrentJobs);
            foreach (var (id, wait) in decisions)
            {
                var job = queuedJobs[id];
                if (wait != JobWait.None || _shuttingDown)
                {
                    job.SetWait(wait);
                    continue;
                }
                var run = job.RunAsync();
                lock (_listLock)
                {
                    if (_entries.TryGetValue(id, out var entry) && entry.Run is null)
                    {
                        entry.Run = run;
                    }
                }
            }
        }
    }

    /// <summary>Raised by a job outside its lock, on a worker or thread-pool thread.</summary>
    private void OnJobStateChanged(object? sender, EventArgs e)
    {
        if (sender is not Job job)
        {
            return;
        }
        if (JobStates.IsTerminal(job.State))
        {
            OnJobFinished(job);
        }
        Schedule();
        PostStateChanged();
    }

    private void OnJobFinished(Job job)
    {
        lock (_listLock)
        {
            // A job is terminal once, but its snapshot can already be terminal while an
            // earlier change is still being delivered; count it once.
            if (!_entries.TryGetValue(job.Id, out var entry) || entry.Finished)
            {
                return;
            }
            entry.Finished = true;
            TrimHistoryLocked();
        }

        Interlocked.Decrement(ref _activeCount);
        if (job.Start.Logging == LoggingMode.Ephemeral)
        {
            Interlocked.Decrement(ref _ephemeralActiveCount);
        }

        var snapshot = job.Snapshot();
        Ui.Post(_ => RaiseFinished(snapshot), null);
        if (job.Start.Logging == LoggingMode.Normal)
        {
            PruneLogsInBackground();
        }
    }

    /// <summary>Drops the oldest finished jobs beyond <see cref="InMemoryHistoryLimit"/>; active jobs are never dropped.</summary>
    private void TrimHistoryLocked()
    {
        var finished = _jobs.Count(j => _entries[j.Id].Finished);
        if (finished <= InMemoryHistoryLimit)
        {
            return;
        }
        var drop = new HashSet<Guid>();
        foreach (var job in _jobs)
        {
            if (finished - drop.Count <= InMemoryHistoryLimit)
            {
                break;
            }
            if (_entries[job.Id].Finished)
            {
                drop.Add(job.Id);
            }
        }
        foreach (var id in drop)
        {
            _entries.Remove(id);
        }
        _jobs = _jobs.Where(j => !drop.Contains(j.Id)).ToArray();
    }

    private bool VersionsChanged(Job[] jobs)
    {
        for (var i = 0; i < jobs.Length; i++)
        {
            if (jobs[i].Version != _cachedVersions[i])
            {
                return true;
            }
        }
        return false;
    }

    private void PostStateChanged() => Ui.Post(static state => ((JobManager)state!).RaiseStateChanged(), this);

    private void RaiseCreated(Guid jobId) => Created?.Invoke(this, jobId);

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseFinished(JobSnapshot job) => Finished?.Invoke(this, job);

    /// <summary>A job and what the manager tracks about it. Guarded by the list lock.</summary>
    private sealed class Entry(Job job)
    {
        public Job Job { get; } = job;

        /// <summary>The lifecycle task, once started; awaited on exit.</summary>
        public Task? Run { get; set; }

        /// <summary>The manager has seen this job reach a terminal state.</summary>
        public bool Finished { get; set; }
    }
}

/// <summary>
/// Builds the <see cref="JobServices"/> for the manager's jobs. A factory because the
/// services include the manager itself (<see cref="IDestinationClaims"/>), which does not
/// exist yet when the composition root creates them.
/// </summary>
internal delegate JobServices JobServicesFactory(IDestinationClaims claims);
