using Microsoft.Win32.SafeHandles;

namespace RoboRightClick.Jobs;

/// <summary>
/// One timer for the whole app that reads GetProcessIoCounters for every running
/// robocopy every <see cref="Interval"/>. Destination sizes cannot be used: robocopy
/// allocates each file at full length first (testlog 2026-10-02).
/// </summary>
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
    /// Starts sampling <paramref name="process"/>; the callback gets cumulative
    /// ReadTransferCount on a thread-pool thread. Dispose the result to stop. A failed read
    /// (the process just exited) skips that tick silently.
    /// </summary>
    public IDisposable Track(SafeProcessHandle process, Action<long, DateTimeOffset> onReadBytes) =>
        throw new NotImplementedException();

    public void Dispose()
    {
    }
}
