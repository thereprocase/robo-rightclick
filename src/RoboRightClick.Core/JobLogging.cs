using System.Globalization;

namespace RoboRightClick.Core;

public sealed record JobDescription(
    Guid Id,
    TransferVerb Verb,
    IReadOnlyList<string> Sources,
    string Destination,
    DateTimeOffset CreatedAt);

/// <param name="Errors">At most <see cref="JobRecords.MaxRecordedErrors"/>; the full count is <see cref="TotalErrors"/>.</param>
public sealed record JobSummary(
    Guid Id,
    JobState FinalState,
    long CompletedFiles,
    long CompletedBytes,
    IReadOnlyList<ErrorReported> Errors)
{
    public int TotalErrors { get; init; }
}

/// <summary>
/// Everything about a job that could ever be persisted flows through this
/// interface. Ephemeral mode is enforced by composing <see cref="NullJobSink"/>,
/// so no other code path needs to know which mode is active.
/// </summary>
public interface IJobSink
{
    void JobCreated(JobDescription job);
    void StateChanged(Guid jobId, StateChange change);
    void CommandStarted(Guid jobId, string arguments);
    void OutputLine(Guid jobId, string line);

    /// <summary>Robocopy's exit code for the command most recently started (job.json records it).</summary>
    void CommandFinished(Guid jobId, int exitCode);
    void JobFinished(JobSummary summary);
}

public sealed class NullJobSink : IJobSink
{
    public static readonly NullJobSink Instance = new();

    private NullJobSink()
    {
    }

    public void JobCreated(JobDescription job) { }
    public void StateChanged(Guid jobId, StateChange change) { }
    public void CommandStarted(Guid jobId, string arguments) { }
    public void OutputLine(Guid jobId, string line) { }
    public void CommandFinished(Guid jobId, int exitCode) { }
    public void JobFinished(JobSummary summary) { }
}

public static class JobSinks
{
    /// <summary>
    /// The logging mode for a job derived from another one ("Try again", a re-run, a
    /// re-plan): ephemeral wins in both directions. A child of an ephemeral job must not
    /// write its parent's paths to disk because the user switched back to normal since,
    /// and a child created while ephemeral mode is on follows the current promise.
    /// </summary>
    public static LoggingMode ForDerivedJob(LoggingMode parent, LoggingMode current) =>
        parent == LoggingMode.Ephemeral || current == LoggingMode.Ephemeral ? LoggingMode.Ephemeral : LoggingMode.Normal;

    /// <summary>
    /// Picks the sink for a new job. The file sink factory is not even invoked
    /// in ephemeral mode, so nothing that could create a file is constructed.
    /// </summary>
    public static IJobSink For(LoggingMode mode, Func<IJobSink> createFileSink) => mode switch
    {
        LoggingMode.Ephemeral => NullJobSink.Instance,
        LoggingMode.Normal => createFileSink(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>Names and prunes per-job log folders ("20261002-153000-1a2b3c4d").</summary>
public static class JobLogNames
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    public static string FolderName(DateTimeOffset createdAt, Guid id) =>
        createdAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-" + id.ToString("N")[..8];

    public static bool IsJobFolderName(string name)
    {
        if (name.Length != TimestampFormat.Length + 9 || name[TimestampFormat.Length] != '-')
        {
            return false;
        }
        var stamp = name[..TimestampFormat.Length];
        var suffix = name[(TimestampFormat.Length + 1)..];
        return DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && suffix.All(char.IsAsciiHexDigitLower);
    }

    /// <summary>
    /// Folders to delete so that at most <paramref name="keep"/> finished jobs remain.
    /// Anything not named like a job folder is never selected, so a stray user file in the
    /// logs directory is left alone. Folders of jobs still running are never selected and
    /// do not count towards <paramref name="keep"/>: a long job is the oldest folder, and
    /// pruning it would delete its live log.
    /// </summary>
    public static IReadOnlyList<string> SelectForPruning(
        IEnumerable<string> folderNames,
        int keep,
        IReadOnlySet<string>? activeFolders = null)
    {
        var jobs = folderNames
            .Where(IsJobFolderName)
            .Where(n => activeFolders is null || !activeFolders.Contains(n))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();
        return jobs.Skip(Math.Max(0, keep)).ToList();
    }
}
