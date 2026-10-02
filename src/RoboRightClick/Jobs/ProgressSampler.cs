using Microsoft.Win32.SafeHandles;

namespace RoboRightClick.Jobs;

/// <summary>
/// One PeriodicTimer loop for the whole app that reads GetProcessIoCounters for every
/// tracked robocopy every <see cref="Interval"/>. Destination sizes cannot be used: robocopy
/// allocates each file at full length first (testlog 2026-10-02).
/// </summary>
/// <remarks>
/// A tracked run is skipped while its <see cref="PauseGate"/> is closed, so a suspended
/// process does not drag the rate down. GetProcessIoCounters still succeeds on an exited
/// process whose handle is open, so exit is not detected here: disposing the tracking
/// handle stops it. Dispose blocks until a callback already in flight has returned, so no
/// sample from a finished run can reach the job after the job moved on to its next step.
/// </remarks>
internal sealed class ProgressSampler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(333);

    private readonly object _gate = new();
    private readonly List<Tracked> _tracked = [];
    private readonly CancellationTokenSource _stop = new();
    private PeriodicTimer? _timer;
    private bool _disposed;

    public ProgressSampler(TimeProvider time, TimeSpan interval)
    {
        Time = time;
        Interval = interval;
    }

    public TimeProvider Time { get; }

    public TimeSpan Interval { get; }

    /// <summary>
    /// Starts sampling <paramref name="process"/>; the callback gets this run's cumulative
    /// ReadTransferCount on the sampler's thread. Dispose the result to stop (see remarks).
    /// </summary>
    public IDisposable Track(SafeProcessHandle process, PauseGate pause, Action<long, DateTimeOffset> onReadBytes)
    {
        var run = new Tracked(this, process, pause, onReadBytes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tracked.Add(run);
            if (_timer is null)
            {
                // Started on first use: an app with no running job has no timer ticking.
                _timer = new PeriodicTimer(Interval, Time);
                _ = Task.Run(() => LoopAsync(_timer, _stop.Token));
            }
        }

        return run;
    }

    public void Dispose()
    {
        Tracked[] remaining;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            remaining = [.. _tracked];
            _tracked.Clear();
        }

        _stop.Cancel();
        _timer?.Dispose();
        foreach (var run in remaining)
        {
            run.Stop();
        }
    }

    private async Task LoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Tracked[] runs;
                lock (_gate)
                {
                    runs = [.. _tracked];
                }

                foreach (var run in runs)
                {
                    run.Sample();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Sampler disposed.
        }
        catch (ObjectDisposedException)
        {
            // Timer disposed between the cancel and the wait.
        }
    }

    private void Remove(Tracked run)
    {
        lock (_gate)
        {
            _tracked.Remove(run);
        }
    }

    private sealed class Tracked(ProgressSampler owner, SafeProcessHandle process, PauseGate pause, Action<long, DateTimeOffset> callback) : IDisposable
    {
        // Held while sampling and while stopping, so Stop cannot return with a callback running.
        private readonly object _runLock = new();
        private bool _stopped;

        public void Sample()
        {
            lock (_runLock)
            {
                if (_stopped || pause.IsPaused)
                {
                    return;
                }

                if (!ProcessNative.GetProcessIoCounters(process, out var counters))
                {
                    return;
                }

                try
                {
                    callback((long)counters.ReadTransferCount, owner.Time.GetUtcNow());
                }
                catch (Exception)
                {
                    // A faulty observer must not end sampling for every other run.
                }
            }
        }

        public void Stop()
        {
            lock (_runLock)
            {
                _stopped = true;
            }
        }

        public void Dispose()
        {
            owner.Remove(this);
            Stop();
        }
    }
}
