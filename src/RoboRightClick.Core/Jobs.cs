namespace RoboRightClick.Core;

/// <summary>
/// Job lifecycle. Mirrors what Explorer's progress dialog shows: calculating,
/// the Replace/Skip prompt, running, paused, then an outcome.
/// </summary>
public enum JobState
{
    Queued,

    /// <summary>Explorer's "Calculating…": sizing the sources and checking for name conflicts.</summary>
    Scanning,

    /// <summary>Conflicts were found and the user has not chosen Replace / Skip / Decide yet.</summary>
    AwaitingDecision,
    Running,
    Paused,

    /// <summary>Post-copy work: clearing the clipboard after a cut, building the summary.</summary>
    Finalizing,
    Done,
    DoneWithErrors,
    Failed,
    Canceled,
}

public static class JobStates
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Queued] = [JobState.Scanning, JobState.Canceled],
        [JobState.Scanning] = [JobState.AwaitingDecision, JobState.Running, JobState.Done, JobState.Failed, JobState.Canceled],
        [JobState.AwaitingDecision] = [JobState.Running, JobState.Canceled],
        [JobState.Running] = [JobState.Paused, JobState.Finalizing, JobState.Failed, JobState.Canceled],
        [JobState.Paused] = [JobState.Running, JobState.Canceled],
        [JobState.Finalizing] = [JobState.Done, JobState.DoneWithErrors, JobState.Failed],
        [JobState.Done] = [],
        [JobState.DoneWithErrors] = [],
        [JobState.Failed] = [],
        [JobState.Canceled] = [],
    };

    public static bool CanTransition(JobState from, JobState to) => Allowed[from].Contains(to);

    public static bool IsTerminal(JobState state) => Allowed[state].Length == 0;
}

public sealed record StateChange(JobState State, DateTimeOffset At);

/// <summary>
/// The state record for one job. Time is passed in rather than read, so the
/// lifecycle stays deterministic and testable.
/// </summary>
public sealed class JobLifecycle
{
    private readonly List<StateChange> _history;

    public JobLifecycle(DateTimeOffset createdAt)
    {
        _history = [new StateChange(JobState.Queued, createdAt)];
    }

    public JobState State => _history[^1].State;

    public IReadOnlyList<StateChange> History => _history;

    public bool TryMoveTo(JobState next, DateTimeOffset at)
    {
        if (!JobStates.CanTransition(State, next))
        {
            return false;
        }
        _history.Add(new StateChange(next, at));
        return true;
    }

    public void MoveTo(JobState next, DateTimeOffset at)
    {
        if (!TryMoveTo(next, at))
        {
            throw new InvalidOperationException($"A job cannot move from {State} to {next}.");
        }
    }
}

/// <summary>
/// Byte and file counters for one job, plus a speed estimate over a short
/// sliding window so the ETA follows the current rate rather than the average.
/// </summary>
public sealed class JobProgress
{
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);

    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();

    public JobProgress(long totalBytes, long totalFiles)
    {
        TotalBytes = totalBytes;
        TotalFiles = totalFiles;
    }

    public long TotalBytes { get; }
    public long TotalFiles { get; }
    public long CompletedBytes { get; private set; }
    public long CompletedFiles { get; private set; }

    /// <summary>
    /// Bytes robocopy has read so far in this job, from its process I/O
    /// counters. Polling destination sizes does not work: robocopy allocates
    /// each file at full length before writing (observed 2026-10-02).
    /// </summary>
    public long ObservedBytes { get; private set; }

    /// <summary>
    /// Completed files are exact but arrive only when each file finishes;
    /// observed bytes move continuously. Whichever is further along wins.
    /// </summary>
    public long DoneBytes => Math.Min(TotalBytes, Math.Max(CompletedBytes, ObservedBytes));

    public void FileCompleted(long size, DateTimeOffset at)
    {
        CompletedFiles++;
        CompletedBytes += size;
        Sample(at);
    }

    public void SetObservedBytes(long bytes, DateTimeOffset at)
    {
        ObservedBytes = Math.Max(ObservedBytes, bytes);
        Sample(at);
    }

    /// <summary>Bytes per second over the last few seconds, or null before there is enough data.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }
            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = (last.At - first.At).TotalSeconds;
            return seconds <= 0 ? null : (last.Bytes - first.Bytes) / seconds;
        }
    }

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            var rate = BytesPerSecond;
            if (rate is null or <= 0)
            {
                return null;
            }
            return TimeSpan.FromSeconds((TotalBytes - DoneBytes) / rate.Value);
        }
    }

    private void Sample(DateTimeOffset at)
    {
        _samples.Enqueue((at, DoneBytes));
        while (_samples.Count > 2 && at - _samples.Peek().At > SpeedWindow)
        {
            _samples.Dequeue();
        }
    }
}
