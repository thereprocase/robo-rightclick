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
    /// <summary>
    /// Errors kept in memory and in job.json per job. A tree with an ACL problem can produce
    /// one error per file; the rest are counted (<see cref="JobSummary.TotalErrors"/>) and
    /// remain in robocopy.log. Retry does not need them: it reads the ledger.
    /// </summary>
    public const int MaxRecordedErrors = 1_000;

    /// <summary>
    /// job.json: indented JSON, camelCase keys, timestamps as ISO 8601 UTC, states as
    /// camelCase names. Bounded by construction: states, commands (argument strings),
    /// summary and at most <see cref="MaxRecordedErrors"/> errors; never per-file data.
    /// </summary>
    public static string ToJson(JobRecord record) => throw new NotImplementedException();

    /// <summary>One history.jsonl line (no trailing newline): id, verb, sources, destination, created, finished, final state, counts, error count.</summary>
    public static string ToHistoryLine(JobDescription job, JobSummary summary, DateTimeOffset finishedAt) =>
        throw new NotImplementedException();

    /// <summary>
    /// The last state recorded in a job.json, or null when the text is not a job record.
    /// At startup a non-terminal last state means the app ended mid-paste (sign-out, crash):
    /// some destination files may be incomplete, which the tray reports once.
    /// </summary>
    public static JobState? LastState(string jobJson) => throw new NotImplementedException();
}
