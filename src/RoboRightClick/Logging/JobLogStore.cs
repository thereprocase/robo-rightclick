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
    /// <summary>history.jsonl is rotated to history.1.jsonl (replacing it) past this many lines.</summary>
    public const int HistoryRotateLines = 10_000;

    public JobLogStore(AppPaths paths, Func<int> retentionJobs)
    {
        Paths = paths;
        RetentionJobs = retentionJobs;
    }

    public AppPaths Paths { get; }

    public Func<int> RetentionJobs { get; }

    /// <summary>The file sink for a new normal-mode job. Creates its folder lazily, on the first write.</summary>
    public IJobSink CreateSink(JobDescription job) => throw new NotImplementedException();

    /// <summary>Appends one <see cref="JobRecords.ToHistoryLine"/> line plus "\n" to history.jsonl, rotating as above.</summary>
    public void AppendHistory(string line) => throw new NotImplementedException();

    /// <summary>
    /// Deletes job folders chosen by <see cref="JobLogNames.SelectForPruning"/>(names,
    /// RetentionJobs(), <paramref name="activeFolders"/>). Enumerates directory names only.
    /// A folder that is a reparse point is removed as a link, never followed. Called off the
    /// UI thread after each normal-mode job finishes.
    /// </summary>
    public void Prune(IReadOnlySet<string> activeFolders) => throw new NotImplementedException();

    /// <summary>Number of job folders on disk (for the "delete existing logs?" question).</summary>
    public int CountJobFolders() => throw new NotImplementedException();

    /// <summary>
    /// "Delete all job logs" (Settings, and when ephemeral mode is turned on): every job
    /// folder except <paramref name="activeFolders"/>, and the history files.
    /// </summary>
    public void DeleteAll(IReadOnlySet<string> activeFolders) => throw new NotImplementedException();

    /// <summary>
    /// Job folders whose job.json ends in a non-terminal state (<see cref="JobRecords.LastState"/>):
    /// the app ended mid-paste. Checked once at startup in normal mode.
    /// </summary>
    public int CountInterrupted() => throw new NotImplementedException();

    /// <summary>The job's folder if it exists on disk (for "Open log"), else null.</summary>
    public string? FolderOf(DateTimeOffset createdAt, Guid jobId) => throw new NotImplementedException();
}
