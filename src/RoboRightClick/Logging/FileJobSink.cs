using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Logging;

/// <summary>
/// Normal-mode sink for one job: jobs\&lt;folder&gt;\job.json (rewritten from a
/// <see cref="JobRecord"/> on each state change and at the end, via a temp file and
/// File.Replace, never on progress) and robocopy.log (UTF-8 copy of the pipe output,
/// written by the app, one header line per command, through a 64 KB buffered writer
/// flushed about once a second, on state changes and at the end). robocopy.log stops at
/// <see cref="MaxRobocopyLogBytes"/> with one "truncated" line. On <see cref="JobFinished"/>
/// it appends the history line; pruning is the manager's job. Called from the job thread
/// and the run's consumer thread; serialized by one lock. Write failures (disk full, folder
/// deleted) are swallowed after the first one is recorded in memory: logging must never
/// fail a copy.
/// </summary>
internal sealed class FileJobSink : IJobSink, IDisposable
{
    public const long MaxRobocopyLogBytes = 50L * 1024 * 1024;

    private const int LogBufferBytes = 64 * 1024;
    private const string TruncationLine = "# output truncated at 50 MB";
    private const string TempSuffix = AppPaths.TempSuffix;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly List<StateChange> _states = [];
    private readonly List<CommandRecord> _commands = [];
    private JobDescription? _job;
    private JobSummary? _summary;
    private bool _folderReady;
    private StreamWriter? _log;
    private System.Threading.Timer? _flushTimer;
    private long _logBytes;
    private bool _logTruncated;

    // After the first open or write failure robocopy.log is abandoned: retrying would throw
    // once per output line on a full disk, and a writer that lost a buffer mid-file would
    // leave a silent gap in the log.
    private bool _logFailed;
    private bool _flushArmed;
    private bool _closed;

    public FileJobSink(JobLogStore store, string jobFolder)
    {
        Store = store;
        JobFolder = jobFolder;
    }

    public JobLogStore Store { get; }

    public string JobFolder { get; }

    public void JobCreated(JobDescription job)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _job = job;
            // Every job starts Queued at its creation time (JobLifecycle does the same); the
            // manager may or may not report that first state itself.
            if (_states.Count == 0 || _states[0].State != JobState.Queued)
            {
                _states.Insert(0, new StateChange(JobState.Queued, job.CreatedAt));
            }
            Guard(WriteJobJson);
        }
    }

    public void StateChanged(Guid jobId, StateChange change)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            // The state machine has no self-transitions, so a repeat of the last state is the
            // initial Queued that JobCreated already recorded (possibly with another timestamp).
            if (_states.Count == 0 || _states[^1].State != change.State)
            {
                _states.Add(change);
            }
            Guard(WriteJobJson);
            Guard(FlushLog);
        }
    }

    public void CommandStarted(Guid jobId, string arguments)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _commands.Add(new CommandRecord(arguments, null));
            Guard(() => WriteLogLine("# robocopy " + arguments));
        }
    }

    public void OutputLine(Guid jobId, string line)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            Guard(() => WriteLogLine(line));
        }
    }

    public void CommandFinished(Guid jobId, int exitCode)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            if (_commands.Count > 0)
            {
                _commands[^1] = _commands[^1] with { ExitCode = exitCode };
            }
            Guard(WriteJobJson);
        }
    }

    public void JobFinished(JobSummary summary)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            try
            {
                _summary = summary;
                // job.json must end in the final state even if the terminal StateChanged has not
                // arrived (it would be dropped after close): a record whose last state is not
                // terminal is reported at the next start as a paste the app died in.
                if (_states.Count == 0 || _states[^1].State != summary.FinalState)
                {
                    _states.Add(new StateChange(summary.FinalState, DateTimeOffset.UtcNow));
                }
                Guard(WriteJobJson);
                if (_job is { } job)
                {
                    // The terminal state change carries the finish time.
                    var finishedAt = _states[^1].At;
                    Guard(() => Store.AppendHistory(JobRecords.ToHistoryLine(job, summary, finishedAt)));
                }
            }
            finally
            {
                // Release the file handle and the timer whatever happened above.
                CloseLog();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseLog();
        }
    }

    // Callers hold _gate. Logging must never fail a copy, so every failure ends here.
    private void Guard(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            Store.RecordFailure(ex);
        }
    }

    private void EnsureFolder()
    {
        if (!_folderReady)
        {
            Directory.CreateDirectory(JobFolder);
            _folderReady = true;
        }
    }

    private void WriteJobJson()
    {
        if (_job is null)
        {
            return;
        }
        EnsureFolder();
        var json = JobRecords.ToJson(new JobRecord(_job, _states.ToArray(), _commands.ToArray(), _summary));
        var target = WinPath.Combine(JobFolder, AppPaths.JobRecordFileName);
        var temp = target + TempSuffix;
        // Write beside the target, then swap: a crash leaves the previous complete file.
        File.WriteAllText(temp, json, Utf8NoBom);
        if (File.Exists(target))
        {
            File.Replace(temp, target, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, target);
        }
    }

    private void WriteLogLine(string line)
    {
        if (_logTruncated || _logFailed)
        {
            return;
        }
        try
        {
            // StreamWriter.WriteLine appends Environment.NewLine, which is ASCII.
            var bytes = Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
            if (_logBytes + bytes > MaxRobocopyLogBytes)
            {
                OpenLog().WriteLine(TruncationLine);
                _logTruncated = true;
            }
            else
            {
                OpenLog().WriteLine(line);
                _logBytes += bytes;
            }
            ArmFlush();
        }
        catch
        {
            AbandonLog();
            throw;
        }
    }

    private StreamWriter OpenLog()
    {
        if (_log is not null)
        {
            return _log;
        }
        EnsureFolder();
        var stream = new FileStream(
            WinPath.Combine(JobFolder, AppPaths.RobocopyLogFileName),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        try
        {
            _log = new StreamWriter(stream, Utf8NoBom, LogBufferBytes);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        // One-shot, re-armed by the next write: an idle or paused job has no timer running,
        // and a sink that is never finished is not kept alive by a periodic callback.
        _flushTimer = new System.Threading.Timer(_ => FlushTick(), null, Timeout.Infinite, Timeout.Infinite);
        return _log;
    }

    private void ArmFlush()
    {
        if (!_flushArmed && _flushTimer is not null)
        {
            _flushArmed = true;
            _flushTimer.Change(FlushInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushTick()
    {
        lock (_gate)
        {
            _flushArmed = false;
            Guard(FlushLog);
        }
    }

    private void FlushLog()
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            _log.Flush();
        }
        catch
        {
            AbandonLog();
            throw;
        }
    }

    // Callers hold _gate. Stops all further log writes; the caller records the cause.
    private void AbandonLog()
    {
        _logFailed = true;
        DisposeLog();
    }

    private void CloseLog()
    {
        _closed = true;
        if (_log is not null)
        {
            Guard(FlushLog);
        }
        DisposeLog();
    }

    private void DisposeLog()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        _flushArmed = false;
        var log = _log;
        _log = null;
        if (log is null)
        {
            return;
        }
        try
        {
            log.Dispose();
        }
        catch (Exception ex)
        {
            // Dispose flushes; after a failure that flush fails again. The handle is released
            // regardless, so the failure only needs recording.
            Store.RecordFailure(ex);
        }
    }
}
