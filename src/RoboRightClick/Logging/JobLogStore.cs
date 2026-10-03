using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Logging;

/// <summary>
/// Normal-mode log storage under %LOCALAPPDATA%\RoboRightClick. Nothing in this class
/// writes in ephemeral mode: <see cref="CreateSink"/> is only reached through
/// <see cref="JobSinks.For"/>, which does not call it then, and the tray hides
/// "Open logs". Thread-safe; history appends, pruning and deletion share one lock.
/// Constructing it does no I/O (it runs before the COM class objects are registered).
/// </summary>
internal sealed class JobLogStore
{
    /// <summary>history.jsonl is rotated to history.1.jsonl (replacing it) past this many lines, or this size (<see cref="JobRecords.ShouldRotateHistory"/>).</summary>
    public const int HistoryRotateLines = JobRecords.HistoryRotateLines;

    private const string RotatedHistoryFileName = "history.1.jsonl";
    private const string TempSuffix = AppPaths.TempSuffix;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private Exception? _firstFailure;

    // Counted once, on the first append, so a rotation check does not re-read the file.
    private int _historyLines = -1;
    private long _historyBytes;

    // history.jsonl ends without "\n": an append was cut short (disk full, crash). The next
    // line starts with one so the partial record does not swallow a complete one.
    private bool _historyEndsMidLine;

    public JobLogStore(AppPaths paths, Func<int> retentionJobs)
    {
        Paths = paths;
        RetentionJobs = retentionJobs;
    }

    /// <summary>
    /// The first I/O failure this store or its sinks swallowed, kept only in memory so a
    /// diagnostics view can show it. Later failures are dropped: one cause is enough, and
    /// a full disk would otherwise repeat the same error for every line.
    /// </summary>
    public Exception? FirstFailure => Volatile.Read(ref _firstFailure);

    internal void RecordFailure(Exception failure) =>
        Interlocked.CompareExchange(ref _firstFailure, failure, null);

    public AppPaths Paths { get; }

    public Func<int> RetentionJobs { get; }

    /// <summary>The file sink for a new normal-mode job. Creates its folder lazily, on the first write.</summary>
    public IJobSink CreateSink(JobDescription job) => new FileJobSink(this, Paths.JobFolder(job.CreatedAt, job.Id));

    /// <summary>Appends one <see cref="JobRecords.ToHistoryLine"/> line plus "\n" to history.jsonl, rotating as above.</summary>
    public void AppendHistory(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDirectory);
                if (_historyLines < 0)
                {
                    var exists = File.Exists(Paths.HistoryFile);
                    _historyLines = exists ? File.ReadLines(Paths.HistoryFile).Count() : 0;
                    _historyBytes = exists ? new FileInfo(Paths.HistoryFile).Length : 0;
                    _historyEndsMidLine = exists && EndsMidLine(Paths.HistoryFile);
                }
                if (JobRecords.ShouldRotateHistory(_historyLines, _historyBytes))
                {
                    File.Move(Paths.HistoryFile, RotatedHistoryFile(), overwrite: true);
                    _historyLines = 0;
                    _historyBytes = 0;
                    _historyEndsMidLine = false;
                }
                var text = (_historyEndsMidLine ? "\n" : "") + line + "\n";
                File.AppendAllText(Paths.HistoryFile, text, Utf8NoBom);
                _historyEndsMidLine = false;
                _historyLines++;
                _historyBytes += Utf8NoBom.GetByteCount(text);
            }
            catch (Exception ex)
            {
                // Forget the count and the tail: a failed append may have written part of the
                // line, so both are read again next time.
                _historyLines = -1;
                RecordFailure(ex);
            }
        }
    }

    /// <summary>
    /// Deletes job folders chosen by <see cref="JobLogNames.SelectForPruning"/>(names,
    /// RetentionJobs(), <paramref name="activeFolders"/>). Enumerates directory names only.
    /// A folder that is a reparse point is removed as a link, never followed. Called off the
    /// UI thread after each normal-mode job finishes.
    /// </summary>
    public void Prune(IReadOnlySet<string> activeFolders)
    {
        lock (_gate)
        {
            int keep;
            try
            {
                // The setting comes from outside; a failure reading it must not escape into
                // the job manager's worker.
                keep = Math.Max(0, RetentionJobs());
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                return;
            }
            DeleteJobFolders(keep, activeFolders);
        }
    }

    /// <summary>Number of job folders on disk (for the "delete existing logs?" question).</summary>
    public int CountJobFolders()
    {
        try
        {
            return JobFolderNames().Count(JobLogNames.IsJobFolderName);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return 0;
        }
    }

    /// <summary>
    /// "Delete all job logs" (Settings, and when ephemeral mode is turned on): every job
    /// folder except <paramref name="activeFolders"/>, and the history files.
    /// </summary>
    public void DeleteAll(IReadOnlySet<string> activeFolders)
    {
        lock (_gate)
        {
            DeleteJobFolders(keep: 0, activeFolders);
            DeleteFile(Paths.HistoryFile);
            DeleteFile(RotatedHistoryFile());
            _historyLines = -1;
        }
    }

    /// <summary>
    /// Finds the jobs a previous run left mid-paste (crash, power loss, a session end that
    /// outran cleanup) and returns how many, for the one startup toast. Each such job.json is
    /// rewritten through <see cref="FileJobSink.WriteJobRecord"/> with a terminal
    /// "interrupted" state (<see cref="JobRecords.MarkInterrupted"/>), so the next start does
    /// not report it again. Normal mode only: the tray never calls this in ephemeral mode.
    /// </summary>
    /// <param name="processStartedAt">
    /// Taken before the COM class objects were registered: a job created at or after it
    /// belongs to this run (<see cref="InterruptedCheck.CurrentRun"/>) and is not touched.
    /// </param>
    /// <param name="activeFolders">
    /// Asked once the folder names are listed; a second guard for live jobs, in case the clock
    /// moved backwards since <paramref name="processStartedAt"/>.
    /// </param>
    public int MarkInterrupted(DateTimeOffset processStartedAt, Func<IReadOnlySet<string>> activeFolders, TimeProvider time)
    {
        var count = 0;
        try
        {
            var names = JobFolderNames().Where(JobLogNames.IsJobFolderName).ToList();
            var active = activeFolders();
            foreach (var name in names.Where(n => !active.Contains(n)))
            {
                try
                {
                    if (MarkIfInterrupted(WinPath.Combine(Paths.JobsDirectory, name), processStartedAt, time))
                    {
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    RecordFailure(ex);
                }
            }
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
        return count;
    }

    /// <summary>True when the folder's job is one a previous run left mid-paste (counted whether or not the rewrite succeeds).</summary>
    private bool MarkIfInterrupted(string folder, DateTimeOffset processStartedAt, TimeProvider time)
    {
        // Under the store's lock, so pruning cannot delete the folder between the read and
        // the replace and leave a stray job.json.tmp behind.
        lock (_gate)
        {
            // A link planted in the logs folder is never followed.
            if (!Directory.Exists(folder) || IsReparsePoint(folder))
            {
                return false;
            }
            var record = WinPath.Combine(folder, AppPaths.JobRecordFileName);
            if (!File.Exists(record))
            {
                return false;
            }
            var json = File.ReadAllText(record, Encoding.UTF8);
            switch (JobRecords.CheckInterrupted(json, processStartedAt))
            {
                case InterruptedCheck.MarkAndReport:
                    try
                    {
                        if (JobRecords.MarkInterrupted(json, time.GetUtcNow()) is { } marked)
                        {
                            FileJobSink.WriteJobRecord(folder, marked);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Still reported now; the next start finds it again.
                        RecordFailure(ex);
                    }
                    return true;
                case InterruptedCheck.ReportOnly:
                    // A newer version's record is never rewritten, so it is reported on
                    // every start of this (older) version.
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>The job's folder if it exists on disk (for "Open log"), else null.</summary>
    public string? FolderOf(DateTimeOffset createdAt, Guid jobId)
    {
        try
        {
            var folder = Paths.JobFolder(createdAt, jobId);
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return null;
        }
    }

    private static bool EndsMidLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return false;
        }
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private string RotatedHistoryFile() => WinPath.Combine(Paths.DataDirectory, RotatedHistoryFileName);

    /// <summary>Names only: nothing inside a job folder is opened while choosing what to delete.</summary>
    private IEnumerable<string> JobFolderNames() =>
        Directory.Exists(Paths.JobsDirectory)
            ? Directory.EnumerateDirectories(Paths.JobsDirectory).Select(path => WinPath.GetFileName(path))
            : [];

    private void DeleteJobFolders(int keep, IReadOnlySet<string> activeFolders)
    {
        IReadOnlyList<string> doomed;
        try
        {
            doomed = JobLogNames.SelectForPruning(JobFolderNames().ToList(), keep, activeFolders);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return;
        }
        foreach (var name in doomed)
        {
            // One stubborn folder (a file open in an editor) must not stop the rest.
            try
            {
                DeleteJobFolder(WinPath.Combine(Paths.JobsDirectory, name));
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }
    }

    private static void DeleteJobFolder(string folder)
    {
        if (IsReparsePoint(folder))
        {
            // Non-recursive Delete on a link removes the link and never its target.
            Directory.Delete(folder, recursive: false);
            return;
        }
        var record = WinPath.Combine(folder, AppPaths.JobRecordFileName);
        File.Delete(record);
        // A temp file survives only if the app died between writing and replacing.
        File.Delete(record + TempSuffix);
        File.Delete(WinPath.Combine(folder, AppPaths.RobocopyLogFileName));
        // Non-recursive: a folder holding anything else is not ours, so this throws and it stays.
        Directory.Delete(folder, recursive: false);
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }
}
