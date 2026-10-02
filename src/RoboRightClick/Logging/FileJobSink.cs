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
    private const string TempSuffix = ".tmp";

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
            _states.Clear();
            _states.Add(new StateChange(JobState.Queued, job.CreatedAt));
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
            // JobCreated already recorded the initial Queued state.
            if (_states.Count == 0 || _states[^1] != change)
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
            _summary = summary;
            Guard(WriteJobJson);
            if (_job is not null)
            {
                // The terminal state change carries the finish time; the clock is only a fallback.
                var finishedAt = _states.Count > 0 && _states[^1].State == summary.FinalState
                    ? _states[^1].At
                    : DateTimeOffset.UtcNow;
                Store.AppendHistory(JobRecords.ToHistoryLine(_job, summary, finishedAt));
            }
            CloseLog();
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
        if (_logTruncated)
        {
            return;
        }
        var bytes = Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
        if (_logBytes + bytes > MaxRobocopyLogBytes)
        {
            OpenLog().WriteLine(TruncationLine);
            _logTruncated = true;
            return;
        }
        OpenLog().WriteLine(line);
        _logBytes += bytes;
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
        _log = new StreamWriter(stream, Utf8NoBom, LogBufferBytes);
        _flushTimer = new System.Threading.Timer(_ => FlushTick(), null, FlushInterval, FlushInterval);
        return _log;
    }

    private void FlushTick()
    {
        lock (_gate)
        {
            Guard(FlushLog);
        }
    }

    private void FlushLog() => _log?.Flush();

    private void CloseLog()
    {
        _closed = true;
        _flushTimer?.Dispose();
        _flushTimer = null;
        var log = _log;
        _log = null;
        if (log is null)
        {
            return;
        }
        Guard(log.Flush);
        Guard(log.Dispose);
    }
}
