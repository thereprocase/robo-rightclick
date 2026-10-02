namespace RoboRightClick.Jobs;

/// <summary>
/// A job's pause switch, shared by everything that runs inside the job. Robocopy steps
/// suspend their process when it closes; in-process copies block in their progress
/// callback; the job loop waits on it between steps and before Finalizing. Thread-safe.
/// </summary>
/// <remarks>
/// <para>Pause is a latch, valid in any non-terminal state: pausing during Queued, Scanning or
/// AwaitingDecision closes the gate, and the job enters Running and immediately moves to
/// Paused. "Pause all" closes every job's gate and makes new jobs start with a closed gate
/// until "Resume all". A consumer must subscribe to <see cref="Changed"/> before reading
/// <see cref="IsPaused"/>, so a change between the two is never lost.</para>
/// <para><see cref="Changed"/> is raised outside the gate's lock and never out of order:
/// notifications are delivered one at a time, each carrying the state current at delivery,
/// and consecutive notifications always differ. A Pause racing a Resume on two threads
/// therefore cannot leave a consumer believing the gate is closed when it is open, which
/// would strand a suspended robocopy. A quick pause-resume pair may be delivered as nothing
/// at all, because the net state did not change. Handlers must still be idempotent: one that
/// subscribed and then read <see cref="IsPaused"/> may next be told the value it already read.</para>
/// <para>Both waits return normally when their token fires, without throwing: the blocking
/// form runs inside CopyFileEx's native progress callback, where an exception would end the
/// process. Callers check the token afterwards.</para>
/// </remarks>
internal sealed class PauseGate
{
    private readonly Lock _lock = new();

    /// <summary>Open = set. The blocking wait uses it; MRES spins briefly and allocates no kernel handle unless asked for one.</summary>
    private readonly ManualResetEventSlim _open = new(initialState: true);

    /// <summary>Completed while open; replaced on each pause and completed by the next resume.</summary>
    private TaskCompletionSource _resumed = CreateCompleted();

    private bool _paused;

    /// <summary>The last value handed to <see cref="Changed"/> (guarded by <see cref="_lock"/>).</summary>
    private bool _lastNotified;

    /// <summary>A thread is delivering notifications (guarded by <see cref="_lock"/>).</summary>
    private bool _notifying;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>
    /// Raised after a change, on the thread of one of the callers that changed it, outside
    /// the gate's lock (see the remarks for ordering), so a running robocopy step can
    /// suspend or resume its process.
    /// </summary>
    public event EventHandler<bool>? Changed;

    public void Pause()
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }
            _paused = true;
            _open.Reset();
            // Continuations run on the thread pool, not inline on the thread that resumes
            // (often the UI thread), so job code never runs on the caller of Resume.
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        DeliverNotifications();
    }

    public void Resume()
    {
        TaskCompletionSource resumed;
        lock (_lock)
        {
            if (!_paused)
            {
                return;
            }
            _paused = false;
            resumed = _resumed;
            _open.Set();
        }
        resumed.TrySetResult();
        DeliverNotifications();
    }

    /// <summary>Completes immediately when open; otherwise when resumed, or (normally, without throwing) when canceled.</summary>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task resumed;
        lock (_lock)
        {
            if (!_paused)
            {
                return Task.CompletedTask;
            }
            resumed = _resumed.Task;
        }
        return cancellationToken.CanBeCanceled ? WaitOrCancelAsync(resumed, cancellationToken) : resumed;
    }

    /// <summary>
    /// Blocking form for CopyFileEx's progress callback, which runs on the copying thread.
    /// Returns when open or when the token fires; never throws on cancel (see the remarks).
    /// </summary>
    public void WaitWhilePaused(CancellationToken cancellationToken)
    {
        try
        {
            _open.Wait(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller checks the token and returns PROGRESS_CANCEL.
        }
    }

    private static async Task WaitOrCancelAsync(Task resumed, CancellationToken cancellationToken)
    {
        try
        {
            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceled while paused: the caller checks the token.
        }
    }

    /// <summary>
    /// Delivers the current state until the last delivered value matches it. Only one thread
    /// delivers at a time; a change made meanwhile is picked up by that thread's next loop,
    /// so no change is lost and no stale value is delivered after a newer one.
    /// </summary>
    private void DeliverNotifications()
    {
        lock (_lock)
        {
            if (_notifying)
            {
                return;
            }
            _notifying = true;
        }

        var delivered = false;
        try
        {
            while (true)
            {
                bool current;
                lock (_lock)
                {
                    current = _paused;
                    if (current == _lastNotified)
                    {
                        _notifying = false;
                        delivered = true;
                        return;
                    }
                    _lastNotified = current;
                }
                OnChanged(current);
            }
        }
        finally
        {
            if (!delivered)
            {
                // A handler threw. Let the next change deliver again rather than stall forever.
                lock (_lock)
                {
                    _notifying = false;
                }
            }
        }
    }

    private static TaskCompletionSource CreateCompleted()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    private void OnChanged(bool paused) => Changed?.Invoke(this, paused);
}
