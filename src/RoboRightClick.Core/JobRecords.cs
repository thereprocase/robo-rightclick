using System.Globalization;
using System.Text;
using System.Text.Json;

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
    /// The format version written as "version" into job.json and every history.jsonl line.
    /// Records without it are version 1. Readers take the fields they know and ignore the
    /// rest, so a record from a newer version still reads; only rewriting one is refused
    /// (<see cref="IsNewerVersion"/>), because that would drop what this version does not know.
    /// </summary>
    public const int CurrentVersion = 1;

    public const string VersionKey = "version";

    /// <summary>
    /// job.json: indented JSON starting with "version", camelCase keys, timestamps as ISO 8601 UTC, states as
    /// camelCase names. Bounded by construction: states, commands (argument strings),
    /// summary and at most <see cref="MaxRecordedErrors"/> errors; never per-file data.
    /// </summary>
    public static string ToJson(JobRecord record)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber(VersionKey, CurrentVersion);

            w.WriteStartObject("job");
            w.WriteString("id", record.Job.Id);
            w.WriteString("verb", CamelCase(record.Job.Verb));
            WriteStrings(w, "sources", record.Job.Sources);
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

            w.WriteStartArray("commands");
            foreach (var command in record.Commands)
            {
                w.WriteStartObject();
                w.WriteString("arguments", command.Arguments);
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

    /// <summary>One history.jsonl line (no trailing newline): version, id, verb, sources, destination, created, finished, final state, counts, error count.</summary>
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
            WriteStrings(w, "sources", job.Sources);
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
    /// The last state recorded in a job.json, or null when the text is not a job record.
    /// At startup a non-terminal last state means the app ended mid-paste (sign-out, crash):
    /// some destination files may be incomplete, which the tray reports once.
    /// </summary>
    public static JobState? LastState(string jobJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(jobJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("states", out var states)
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
            if (last.ValueKind != JsonValueKind.Object
                || !last.TryGetProperty("state", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return ParseStateName(name.GetString());
        }
        catch (JsonException)
        {
            // A job.json cut off by a crash or power loss is not a job record.
            return null;
        }
    }

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
