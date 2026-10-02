using RoboRightClick.Core;

namespace RoboRightClick.Logging;

/// <summary>
/// Normal-mode log storage under %LOCALAPPDATA%\RoboRightClick. Nothing in this class
/// runs in ephemeral mode: <see cref="CreateSink"/> is only reached through
/// <see cref="JobSinks.For"/>, which does not call it then, and the tray hides
/// "Open logs". Thread-safe; history appends and pruning share one lock.
/// </summary>
internal sealed class JobLogStore
{
    public JobLogStore(AppPaths paths, Func<int> retentionJobs)
    {
        Paths = paths;
        RetentionJobs = retentionJobs;
    }

    public AppPaths Paths { get; }

    public Func<int> RetentionJobs { get; }

    /// <summary>The file sink for a new normal-mode job. Creates its folder lazily, on the first write.</summary>
    public IJobSink CreateSink(JobDescription job) => throw new NotImplementedException();

    /// <summary>Appends one <see cref="JobRecords.ToHistoryLine"/> line plus "\n" to history.jsonl.</summary>
    public void AppendHistory(string line) => throw new NotImplementedException();

    /// <summary>
    /// Deletes job folders chosen by <see cref="JobLogNames.SelectForPruning"/>(keep:
    /// RetentionJobs()). Called after each job finishes. Never touches anything not named
    /// like a job folder.
    /// </summary>
    public void Prune() => throw new NotImplementedException();

    /// <summary>The job's folder if it exists on disk (for "Open log"), else null.</summary>
    public string? FolderOf(DateTimeOffset createdAt, Guid jobId) => throw new NotImplementedException();
}
