using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>Batches the reader may have queued before it waits for the consumer; bounds memory when the sink is slow.</summary>
    private const int ChannelCapacity = 64;

    private readonly object _pauseLock = new();
    private int _used;
    private int _disposed;
    private volatile bool _killed;
    private bool _suspended;
    private bool _pauseClosed;
    private Process? _process;
    private SafeFileHandle? _job;
    private RobocopyPipe? _pipe;

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
    public async Task<StepOutcome> RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new InvalidOperationException("A RobocopyRun runs once.");
        }

        if (RobocopyArgs.CommandLineLength(RobocopyPath, Arguments) > RobocopyArgs.MaxCommandLineLength)
        {
            return Failed("The robocopy command line was too long to run.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new StepOutcome([], [], null, null);
        }

        try
        {
            _pipe = RobocopyPipe.Create(PipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Most likely the name is already taken: someone else owns the pipe robocopy would write to.
            return Failed("The output channel for robocopy could not be created safely.");
        }

        _job = CreateKillOnCloseJob();
        if (_job is null)
        {
            return Failed("Robocopy could not be started inside a job object.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(RobocopyPath, Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            },
        };
        // Stdout carries only a short header, but an undrained pipe would stall robocopy.
        process.OutputDataReceived += static (_, _) => { };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            var code = ex is Win32Exception w ? w.NativeErrorCode : 0;
            return Failed($"Robocopy could not be started (error {code}).");
        }

        _process = process;
        process.BeginOutputReadLine();

        if (!ProcessNative.AssignProcessToJobObject(_job, process.SafeHandle))
        {
            // Without the job the app could not guarantee robocopy dies with it; stop now.
            Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            return Failed("Robocopy could not be started inside a job object.");
        }

        using var killOnCancel = cancellationToken.Register(Kill);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = process.WaitForExitAsync();
        _ = exited.ContinueWith(
            _ =>
            {
                try
                {
                    // A robocopy that died before connecting must not leave the pipe wait hanging.
                    waitCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            TaskScheduler.Default);

        // Subscribe, then read the flag: a pause between the two cannot be lost.
        Pause.Changed += OnPauseChanged;
        if (Pause.IsPaused)
        {
            ApplyPause(true);
        }

        var parser = new RobocopyOutputParser();
        var completed = new List<string>();
        var errors = new List<ErrorReported>();
        var channel = Channel.CreateBounded<IReadOnlyList<string>>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        var consumer = Task.Run(() => ConsumeAsync(channel.Reader, parser, completed, errors));
        var reader = Task.Run(() => ReadAsync(channel.Writer, process.Id, waitCts.Token));
        var tracking = Sampler.Track(process.SafeHandle, Pause, Observer.OnBytesRead);

        try
        {
            await exited.ConfigureAwait(false);
        }
        finally
        {
            // Stop sampling before the job moves on, so no sample from this run arrives late.
            tracking.Dispose();
            Pause.Changed -= OnPauseChanged;
        }

        var readerFault = await reader.ConfigureAwait(false);
        await consumer.ConfigureAwait(false);

        var exitCode = process.ExitCode;
        var finalEvents = parser.Complete(processEndedNormally: !_killed && exitCode >= 0);
        Collect(finalEvents, completed, errors);
        RaiseEvents(finalEvents);

        var failure = readerFault ? "The output from robocopy could not be read." : null;
        return new StepOutcome(completed, errors, new RobocopyExitCode(exitCode), failure);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Pause.Changed -= OnPauseChanged;
        lock (_pauseLock)
        {
            // No suspend or resume may touch the process handle after this point.
            _pauseClosed = true;
        }

        // Closing the job kills a robocopy that is somehow still alive.
        _job?.Dispose();
        _pipe?.Dispose();
        _process?.Dispose();
    }

    /// <summary>Drains the pipe into the channel. Returns true when the pipe could not be read (robocopy is then killed).</summary>
    private async Task<bool> ReadAsync(ChannelWriter<IReadOnlyList<string>> writer, int robocopyPid, CancellationToken waitToken)
    {
        var pipe = _pipe!;
        try
        {
            await pipe.WaitForRobocopyAsync(robocopyPid, waitToken).ConfigureAwait(false);
            await foreach (var batch in pipe.ReadLineBatchesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Robocopy exited or was canceled before a trusted client connected: the exit code decides.
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Nobody is reading the pipe any more, and robocopy would block on it: stop it.
            Kill();
            return true;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(
        ChannelReader<IReadOnlyList<string>> reader,
        RobocopyOutputParser parser,
        List<string> completed,
        List<ErrorReported> errors)
    {
        await foreach (var batch in reader.ReadAllAsync().ConfigureAwait(false))
        {
            var events = new List<RobocopyEvent>();
            foreach (var line in batch)
            {
                events.AddRange(parser.Feed(line));
            }

            // Collected before the observer runs: the outcome must not depend on the observer behaving.
            Collect(events, completed, errors);
            try
            {
                Observer.OnLines(batch);
                Observer.OnEvents(events);
            }
            catch (Exception)
            {
                // A faulty sink must not stop the drain: a full pipe would stall robocopy for good.
            }
        }
    }

    private void RaiseEvents(IReadOnlyList<RobocopyEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            Observer.OnEvents(events);
        }
        catch (Exception)
        {
            // See ConsumeAsync: the outcome is already collected.
        }
    }

    private static void Collect(IReadOnlyList<RobocopyEvent> events, List<string> completed, List<ErrorReported> errors)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case FileReported file:
                    completed.Add(file.Path);
                    break;
                case ErrorReported error:
                    errors.Add(error);
                    break;
            }
        }
    }

    private void Kill()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                // Not a kill: its output is complete and the held file line is valid.
                return;
            }

            _killed = true;
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Exited between the check and the kill.
        }
    }

    private void OnPauseChanged(object? sender, bool paused) => ApplyPause(paused);

    /// <summary>
    /// NtSuspendProcess counts: two suspends need two resumes. The flag keeps the pair
    /// balanced however often the gate flips.
    /// </summary>
    private void ApplyPause(bool paused)
    {
        lock (_pauseLock)
        {
            if (_pauseClosed || _process is null || paused == _suspended)
            {
                return;
            }

            var handle = _process.SafeHandle;
            var status = paused ? ProcessNative.NtSuspendProcess(handle) : ProcessNative.NtResumeProcess(handle);
            if (status >= 0)
            {
                _suspended = paused;
            }
        }
    }

    private static SafeFileHandle? CreateKillOnCloseJob()
    {
        var job = ProcessNative.CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            return null;
        }

        var limits = new ProcessNative.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = { LimitFlags = ProcessNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var size = (uint)Marshal.SizeOf<ProcessNative.JobObjectExtendedLimitInformation>();
        if (!ProcessNative.SetInformationJobObject(job, ProcessNative.JobObjectExtendedLimitInformationClass, in limits, size))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    private static StepOutcome Failed(string reason) => new([], [], null, reason);
}
