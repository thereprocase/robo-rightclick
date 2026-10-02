using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>Callbacks from one robocopy run, raised on the pipe-reader thread.</summary>
internal interface IRobocopyObserver
{
    /// <summary>Every raw output line, for the job's IJobSink (robocopy.log in normal mode).</summary>
    void OnLine(string line);

    /// <summary>Parsed events from <see cref="RobocopyOutputParser"/>, including the final flush.</summary>
    void OnEvent(RobocopyEvent robocopyEvent);

    /// <summary>Cumulative ReadTransferCount of this run, from <see cref="ProgressSampler"/>.</summary>
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
    /// 1. <see cref="RobocopyPipe.Create"/>. 2. Start robocopy (no shell, no window, stdout
    /// redirected and drained: it carries only a short header). 3. Put it in a
    /// kill-on-close job object so it cannot outlive the app. 4. Verify the pipe client
    /// PID. 5. Read lines into a parser, raising <see cref="IRobocopyObserver"/> callbacks;
    /// track the process with the sampler; follow <see cref="Pause"/> with
    /// NtSuspendProcess/NtResumeProcess. 6. On <paramref name="cancellationToken"/>: kill,
    /// wait for exit, flush the parser. Returns the outcome; a launch or pipe-trust failure
    /// is <see cref="StepOutcome.Failure"/>, never an exception.
    /// </summary>
    public Task<StepOutcome> RunAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
