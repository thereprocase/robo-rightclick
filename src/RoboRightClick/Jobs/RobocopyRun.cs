using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>Callbacks from one robocopy run, raised on the run's consumer thread.</summary>
internal interface IRobocopyObserver
{
    /// <summary>A batch of raw output lines, for the job's IJobSink (robocopy.log in normal mode).</summary>
    void OnLines(IReadOnlyList<string> lines);

    /// <summary>Parsed events from <see cref="RobocopyOutputParser"/> for the same batch, including the final flush.</summary>
    void OnEvents(IReadOnlyList<RobocopyEvent> events);

    /// <summary>This run's cumulative ReadTransferCount, from <see cref="ProgressSampler"/>.</summary>
    void OnBytesRead(long bytes, DateTimeOffset at);
}

/// <summary>
/// One robocopy process for one <see cref="RobocopyStep"/>: pipe, process, parser,
/// sampling, pause and kill. One instance per run; not reusable.
/// </summary>
internal sealed class RobocopyRun : IDisposable
{
    /// <summary>Absolute path: never resolve robocopy through PATH or the working directory.</summary>
    public static string RobocopyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");

    public RobocopyRun(string arguments, string pipeName, PauseGate pause, ProgressSampler sampler, IRobocopyObserver observer)
    {
        Arguments = arguments;
        PipeName = pipeName;
        Pause = pause;
        Sampler = sampler;
        Observer = observer;
    }

    public string Arguments { get; }
    public string PipeName { get; }
    public PauseGate Pause { get; }
    public ProgressSampler Sampler { get; }
    public IRobocopyObserver Observer { get; }

    /// <summary>
    /// 1. Refuse (Failure) if <see cref="RobocopyArgs.CommandLineLength"/> exceeds
    /// <see cref="RobocopyArgs.MaxCommandLineLength"/>. 2. <see cref="RobocopyPipe.Create"/>.
    /// 3. Start robocopy: no shell, no window, WorkingDirectory = System32, stdout redirected
    /// and drained (it carries only a short header). 4. Put it in a kill-on-close job object
    /// so it cannot outlive the app. 5. Subscribe to <see cref="PauseGate.Changed"/>, then
    /// read <see cref="PauseGate.IsPaused"/> and suspend at once if closed, so no pause is
    /// lost. 6. <see cref="RobocopyPipe.WaitForRobocopyAsync"/>. 7. A reader loop moves line
    /// batches into a bounded channel; a consumer parses them and raises the observer
    /// callbacks, so a slow sink never stalls the pipe and robocopy behind it. Track the
    /// process with the sampler. 8. On <paramref name="cancellationToken"/>: kill, wait for
    /// exit. 9. In every case, await process exit AND reader end-of-output, then
    /// <see cref="RobocopyOutputParser.Complete(bool)"/> with processEndedNormally false when
    /// killed or when the exit code is negative, then build the outcome. A launch or
    /// pipe-trust failure is <see cref="StepOutcome.Failure"/>, never an exception.
    /// </summary>
    public Task<StepOutcome> RunAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
