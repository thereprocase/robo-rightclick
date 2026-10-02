namespace RoboRightClick.Core;

/// <summary>One robocopy command within a job, as job.json records it.</summary>
public sealed record CommandRecord(string Arguments, int? ExitCode);

/// <summary>Everything job.json holds; accumulated by the host's FileJobSink from IJobSink calls.</summary>
public sealed record JobRecord(
    JobDescription Job,
    IReadOnlyList<StateChange> States,
    IReadOnlyList<CommandRecord> Commands,
    JobSummary? Summary);

/// <summary>
/// The on-disk formats of normal-mode logging. Pure text in, text out: only the host's
/// FileJobSink writes them, and ephemeral mode never constructs that sink.
/// </summary>
public static class JobRecords
{
    /// <summary>job.json: indented JSON, camelCase keys, timestamps as ISO 8601 UTC, states as camelCase names.</summary>
    public static string ToJson(JobRecord record) => throw new NotImplementedException();

    /// <summary>One history.jsonl line (no trailing newline): id, verb, sources, destination, created, finished, final state, counts, error count.</summary>
    public static string ToHistoryLine(JobDescription job, JobSummary summary, DateTimeOffset finishedAt) =>
        throw new NotImplementedException();
}
