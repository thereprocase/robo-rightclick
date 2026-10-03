using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoboRightClick.Core;

/// <summary>One robocopy command within a job, as job.json records it.</summary>
public sealed record CommandRecord(string Arguments, int? ExitCode);

/// <summary>What the startup check does with one job.json (<see cref="JobRecords.CheckInterrupted"/>).</summary>
public enum InterruptedCheck
{
    /// <summary>Nothing to report: not a job record, ended in a terminal state, or already marked interrupted.</summary>
    None,

    /// <summary>
    /// The app ended mid-paste: report it, and rewrite the record with
    /// <see cref="JobRecords.MarkInterrupted"/> so the next start does not report it again.
    /// </summary>
    MarkAndReport,

    /// <summary>The app ended mid-paste, but a newer version wrote the record: report it and leave the file as it is.</summary>
    ReportOnly,

    /// <summary>Created at or after the running process started: a live job of this run, not touched.</summary>
    CurrentRun,
}

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
    /// Sources written into job.json and each history line, at most: a selection can hold
    /// 250,000 paths, and job.json is rewritten on every state change. The total is written as
    /// "sourceCount"; the full list is in memory while the job lives, and the robocopy commands
    /// in robocopy.log name every folder.
    /// </summary>
    public const int MaxRecordedSources = 100;

    /// <summary>Characters of source paths written per record, at most (one path always fits, however long).</summary>
    public const int MaxRecordedSourceChars = 16_384;

    /// <summary>
    /// Commands kept in job.json, the most recent ones: a paste of 100,000 loose files runs a
    /// command per 24,000 characters of names. The total is written as "commandCount", and
    /// robocopy.log has every command line in full.
    /// </summary>
    public const int MaxRecordedCommands = 100;

    /// <summary>Characters of one command's arguments kept in job.json; the rest is cut and marked.</summary>
    public const int MaxRecordedArgumentChars = 2_048;

    /// <summary>history.jsonl is rotated once it holds this many lines.</summary>
    public const int HistoryRotateLines = 10_000;

    /// <summary>history.jsonl is also rotated once it reaches this size, whatever its line count.</summary>
    public const long HistoryRotateBytes = 8L * 1024 * 1024;

    /// <summary>Whether history.jsonl must be rotated before the next line is appended.</summary>
    public static bool ShouldRotateHistory(int lines, long bytes) => lines >= HistoryRotateLines || bytes >= HistoryRotateBytes;

    /// <summary>
    /// The sources a record lists: the first ones up to <see cref="MaxRecordedSources"/> and
    /// <see cref="MaxRecordedSourceChars"/>, never fewer than one when there is one.
    /// </summary>
    public static IReadOnlyList<string> RecordedSources(IReadOnlyList<string> sources)
    {
        var recorded = new List<string>(Math.Min(sources.Count, MaxRecordedSources));
        var chars = 0;
        foreach (var source in sources)
        {
            if (recorded.Count == MaxRecordedSources || (recorded.Count > 0 && chars + source.Length > MaxRecordedSourceChars))
            {
                break;
            }
            recorded.Add(source);
            chars += source.Length;
        }
        return recorded;
    }

    /// <summary>A command's arguments as job.json keeps them: cut at <see cref="MaxRecordedArgumentChars"/>, marked with "…".</summary>
    public static string RecordedArguments(string arguments) =>
        arguments.Length <= MaxRecordedArgumentChars ? arguments : arguments[..MaxRecordedArgumentChars] + "…";

    /// <summary>
    /// The format version written as "version" into job.json and every history.jsonl line.
    /// Records without it are version 1. Readers take the fields they know and ignore the
    /// rest, so a record from a newer version still reads; only rewriting one is refused
    /// (<see cref="IsNewerVersion"/>), because that would drop what this version does not know.
    /// </summary>
    public const int CurrentVersion = 1;

    public const string VersionKey = "version";

    /// <summary>
    /// job.json: indented JSON starting with "version", camelCase keys, timestamps as ISO 8601 UTC, states as
    /// camelCase names. Bounded by construction: the first sources (<see cref="RecordedSources"/>)
    /// with "sourceCount", states, the most recent <see cref="MaxRecordedCommands"/> commands
    /// with "commandCount" and their arguments cut (<see cref="RecordedArguments"/>), summary
    /// and at most <see cref="MaxRecordedErrors"/> errors. Readers ignore the count fields.
    /// </summary>
    /// <param name="commandCount">Commands the job ran in all; at least the commands passed.</param>
    public static string ToJson(JobRecord record, int? commandCount = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber(VersionKey, CurrentVersion);

            w.WriteStartObject("job");
            w.WriteString("id", record.Job.Id);
            w.WriteString("verb", CamelCase(record.Job.Verb));
            WriteStrings(w, "sources", RecordedSources(record.Job.Sources));
            w.WriteNumber("sourceCount", record.Job.Sources.Count);
            w.WriteString("destination", record.Job.Destination);
            w.WriteString("createdAt", Iso(record.Job.CreatedAt));
            w.WriteEndObject();

            w.WriteStartArray("states");
            foreach (var change in record.States)
            {
                w.WriteStartObject();
                w.WriteString("state", CamelCase(change.State));
                w.WriteString("at", Iso(change.At));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteNumber("commandCount", Math.Max(commandCount ?? 0, record.Commands.Count));
            w.WriteStartArray("commands");
            foreach (var command in record.Commands.Skip(Math.Max(0, record.Commands.Count - MaxRecordedCommands)))
            {
                w.WriteStartObject();
                w.WriteString("arguments", RecordedArguments(command.Arguments));
                if (command.ExitCode is { } code)
                {
                    w.WriteNumber("exitCode", code);
                }
                else
                {
                    w.WriteNull("exitCode");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (record.Summary is { } summary)
            {
                w.WriteStartObject("summary");
                w.WriteString("finalState", CamelCase(summary.FinalState));
                w.WriteNumber("completedFiles", summary.CompletedFiles);
                w.WriteNumber("completedBytes", summary.CompletedBytes);
                w.WriteNumber("totalErrors", TotalErrors(summary));
                w.WriteStartArray("errors");
                foreach (var error in summary.Errors.Take(MaxRecordedErrors))
                {
                    w.WriteStartObject();
                    w.WriteNumber("code", error.Code);
                    w.WriteString("operation", error.Operation);
                    w.WriteString("path", error.Path);
                    w.WriteString("message", error.Message);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("summary");
            }

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// One history.jsonl line (no trailing newline): version, id, verb, the first sources
    /// (<see cref="RecordedSources"/>) and "sourceCount", destination, created, finished, final
    /// state, counts, error count.
    /// </summary>
    public static string ToHistoryLine(JobDescription job, JobSummary summary, DateTimeOffset finishedAt)
    {
        using var stream = new MemoryStream();
        // Not indented, and the default encoder escapes control characters, so a path
        // containing a line break still yields exactly one line.
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber(VersionKey, CurrentVersion);
            w.WriteString("id", job.Id);
            w.WriteString("verb", CamelCase(job.Verb));
            WriteStrings(w, "sources", RecordedSources(job.Sources));
            w.WriteNumber("sourceCount", job.Sources.Count);
            w.WriteString("destination", job.Destination);
            w.WriteString("createdAt", Iso(job.CreatedAt));
            w.WriteString("finishedAt", Iso(finishedAt));
            w.WriteString("finalState", CamelCase(summary.FinalState));
            w.WriteNumber("completedFiles", summary.CompletedFiles);
            w.WriteNumber("completedBytes", summary.CompletedBytes);
            w.WriteNumber("errorCount", TotalErrors(summary));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// The state name the startup check appends to a record that ended mid-paste. It is not
    /// a <see cref="JobState"/>: no running job is ever in it, and <see cref="LastState"/>
    /// returns null for it. It is terminal on disk, so the record is reported only once.
    /// </summary>
    public const string InterruptedStateName = "interrupted";

    /// <summary>
    /// The last state recorded in a job.json, or null when the text is not a job record (or
    /// ends in <see cref="InterruptedStateName"/>). At startup a non-terminal last state means
    /// the app ended mid-paste (sign-out, crash, power loss): some destination files may be
    /// incomplete (<see cref="CheckInterrupted"/>).
    /// </summary>
    public static JobState? LastState(string jobJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(jobJson);
            return ParseStateName(LastStateName(doc.RootElement));
        }
        catch (JsonException)
        {
            // A job.json cut off by a crash or power loss is not a job record.
            return null;
        }
    }

    /// <summary>
    /// The startup decision for one job.json found on disk (normal mode only). A record whose
    /// last state is a non-terminal <see cref="JobState"/> was left by a process that ended
    /// mid-paste, unless the job was created at or after <paramref name="processStartedAt"/>:
    /// then it belongs to the running tray (COM may deliver a right-click before the check
    /// runs) and is not touched. A record of a newer or unreadable version is reported but
    /// never rewritten (<see cref="IsNewerVersion"/>).
    /// </summary>
    public static InterruptedCheck CheckInterrupted(string jobJson, DateTimeOffset processStartedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(jobJson);
            var root = doc.RootElement;
            if (ParseStateName(LastStateName(root)) is not { } last || JobStates.IsTerminal(last))
            {
                // Not a record, finished, or already marked interrupted.
                return InterruptedCheck.None;
            }
            if (CreatedAt(root) is { } created && created >= processStartedAt)
            {
                return InterruptedCheck.CurrentRun;
            }
            return IsNewerVersion(root) ? InterruptedCheck.ReportOnly : InterruptedCheck.MarkAndReport;
        }
        catch (JsonException)
        {
            return InterruptedCheck.None;
        }
    }

    /// <summary>
    /// The record with one more state, <see cref="InterruptedStateName"/> at
    /// <paramref name="at"/>, and every other field kept as it was. Null for a record that
    /// is not a non-terminal one of a known version: only what
    /// <see cref="CheckInterrupted"/> calls <see cref="InterruptedCheck.MarkAndReport"/> is
    /// ever rewritten (the start-time rule is the caller's, through that check).
    /// </summary>
    public static string? MarkInterrupted(string jobJson, DateTimeOffset at)
    {
        try
        {
            using (var doc = JsonDocument.Parse(jobJson))
            {
                if (ParseStateName(LastStateName(doc.RootElement)) is not { } last
                    || JobStates.IsTerminal(last)
                    || IsNewerVersion(doc.RootElement))
                {
                    return null;
                }
            }

            var root = (JsonObject)JsonNode.Parse(jobJson)!;
            ((JsonArray)root["states"]!).Add(new JsonObject
            {
                ["state"] = InterruptedStateName,
                ["at"] = Iso(at),
            });
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidCastException)
        {
            // JsonObject refuses a duplicated key that JsonDocument accepted; such a file was
            // not written by this app and is left as it is.
            return null;
        }
    }

    /// <summary>The "state" string of the last entry of "states", or null when the record has none.</summary>
    private static string? LastStateName(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("states", out var states)
            || states.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var count = states.GetArrayLength();
        if (count == 0)
        {
            return null;
        }
        var last = states[count - 1];
        return last.ValueKind == JsonValueKind.Object
            && last.TryGetProperty("state", out var name)
            && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
    }

    /// <summary>"job"."createdAt" as <see cref="ToJson"/> writes it, or null when missing or unreadable.</summary>
    private static DateTimeOffset? CreatedAt(JsonElement root) =>
        root.TryGetProperty("job", out var job)
        && job.ValueKind == JsonValueKind.Object
        && job.TryGetProperty("createdAt", out var created)
        && created.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            created.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;

    /// <summary>
    /// The record's "version": 1 when missing (records from before the field existed), null
    /// when present but not a positive integer (a damaged or foreign record).
    /// </summary>
    internal static int? RecordVersion(JsonElement root)
    {
        if (!root.TryGetProperty(VersionKey, out var version))
        {
            return 1;
        }
        return version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) && value >= 1
            ? value
            : null;
    }

    /// <summary>
    /// True when <paramref name="root"/> must not be rewritten by this version: its version
    /// is newer than <see cref="CurrentVersion"/>, or present but unreadable.
    /// </summary>
    internal static bool IsNewerVersion(JsonElement root) =>
        RecordVersion(root) is not { } version || version > CurrentVersion;

    // Exact match against the names ToJson writes. Enum.TryParse is too lenient for a file
    // that may be damaged: it accepts numbers, padding and comma lists that it ORs together,
    // so "running, paused" would read as DoneWithErrors and hide an interrupted job.
    private static JobState? ParseStateName(string? name)
    {
        foreach (var state in Enum.GetValues<JobState>())
        {
            if (string.Equals(name, CamelCase(state), StringComparison.Ordinal))
            {
                return state;
            }
        }
        return null;
    }

    // A summary built without TotalErrors still reports the errors it carries.
    private static long TotalErrors(JobSummary summary) => Math.Max(summary.TotalErrors, summary.Errors.Count);

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string CamelCase<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void WriteStrings(Utf8JsonWriter w, string property, IReadOnlyList<string> values)
    {
        w.WriteStartArray(property);
        foreach (var value in values)
        {
            w.WriteStringValue(value);
        }
        w.WriteEndArray();
    }
}
