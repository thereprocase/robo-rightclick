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
    public IDisposable Track(SafeProcessHandle process, PauseGate pause, Action<long, DateTimeOffset> onReadBytes) =>
        throw new NotImplementedException();

    public void Dispose()
    {
    }
}
