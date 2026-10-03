using System.Globalization;
using System.Text.Json;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class JobRecordsTests
{
    private static readonly Guid Id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001");

    // A non-UTC offset on purpose: the formats must normalize to UTC.
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 15, 30, 0, TimeSpan.FromHours(-7));

    private static JobDescription Job() =>
        new(Id, TransferVerb.Move, [@"C:\src\a.txt", @"C:\src\日本語 📁"], @"D:\dest", Created);

    private static List<ErrorReported> Errors(int count) =>
        Enumerable.Range(0, count).Select(i => new ErrorReported(5, "Copying File", $@"C:\src\f{i}.bin", "Access is denied.")).ToList();

    private static JobRecord Record(JobSummary? summary) => new(
        Job(),
        [new StateChange(JobState.Queued, Created), new StateChange(JobState.Running, Created.AddSeconds(2))],
        [new CommandRecord("\"C:\\src\" \"D:\\dest\" /MT:32", 1), new CommandRecord("/E", null)],
        summary);

    [Fact]
    public void The_summary_keeps_the_files_to_check_so_they_survive_a_restart()
    {
        // A canceled copy that left 3 files possibly incomplete, and one that failed before it.
        var many = Enumerable.Range(0, 250).Select(i => $@"D:\dest\late{i}.txt").ToList();
        var summary = new JobSummary(Id, JobState.Canceled, 4, 400, [])
        {
            Damaged = new PathList([@"D:\dest\big.iso", @"D:\dest\b.bin", @"D:\dest\c.bin"], 3),
            MayBeIncomplete = new PathList([@"D:\dest\video.mp4"], 1),
            SkippedAppeared = new PathList(many, 1_234),
        };

        using var doc = JsonDocument.Parse(JobRecords.ToJson(Record(summary)));
        var s = doc.RootElement.GetProperty("summary");

        Assert.Equal(3, s.GetProperty("damaged").GetProperty("count").GetInt32());
        Assert.Equal(@"D:\dest\big.iso", s.GetProperty("damaged").GetProperty("paths")[0].GetString());
        Assert.Equal([@"D:\dest\video.mp4"], s.GetProperty("mayBeIncomplete").GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
        // Bounded like the sources: the first paths and the exact count.
        Assert.Equal(1_234, s.GetProperty("skippedAppeared").GetProperty("count").GetInt32());
        Assert.Equal(JobRecords.MaxRecordedPaths, s.GetProperty("skippedAppeared").GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public void A_clean_summary_writes_no_empty_lists_and_the_destination_reads_back()
    {
        var json = JobRecords.ToJson(Record(new JobSummary(Id, JobState.Done, 1, 1, [])));
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement.GetProperty("summary");

        Assert.False(s.TryGetProperty("damaged", out _));
        Assert.False(s.TryGetProperty("mayBeIncomplete", out _));
        Assert.False(s.TryGetProperty("skippedAppeared", out _));
        Assert.Equal(@"D:\dest", JobRecords.DestinationOf(json));
        Assert.Null(JobRecords.DestinationOf("{ \"job\": 3 }"));
        Assert.Null(JobRecords.DestinationOf("not json"));
    }

    [Fact]
    public void Json_round_trips_with_camel_case_keys_and_names()
    {
        var summary = new JobSummary(Id, JobState.DoneWithErrors, 12, 3_000_000_000, Errors(2)) { TotalErrors = 2 };
        using var doc = JsonDocument.Parse(JobRecords.ToJson(Record(summary)));
        var root = doc.RootElement;

        var job = root.GetProperty("job");
        Assert.Equal(Id, job.GetProperty("id").GetGuid());
        Assert.Equal("move", job.GetProperty("verb").GetString());
        Assert.Equal([@"C:\src\a.txt", @"C:\src\日本語 📁"], job.GetProperty("sources").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(@"D:\dest", job.GetProperty("destination").GetString());

        var states = root.GetProperty("states").EnumerateArray().ToList();
        Assert.Equal(["queued", "running"], states.Select(s => s.GetProperty("state").GetString()));

        var commands = root.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(2, commands.Count);
        Assert.Equal("\"C:\\src\" \"D:\\dest\" /MT:32", commands[0].GetProperty("arguments").GetString());
        Assert.Equal(1, commands[0].GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, commands[1].GetProperty("exitCode").ValueKind);

        var s = root.GetProperty("summary");
        Assert.Equal("doneWithErrors", s.GetProperty("finalState").GetString());
        Assert.Equal(12, s.GetProperty("completedFiles").GetInt64());
        Assert.Equal(3_000_000_000, s.GetProperty("completedBytes").GetInt64());
        Assert.Equal(2, s.GetProperty("totalErrors").GetInt32());
        var first = s.GetProperty("errors")[0];
        Assert.Equal(5, first.GetProperty("code").GetInt32());
        Assert.Equal("Copying File", first.GetProperty("operation").GetString());
        Assert.Equal(@"C:\src\f0.bin", first.GetProperty("path").GetString());
        Assert.Equal("Access is denied.", first.GetProperty("message").GetString());
    }

    [Fact]
    public void Json_is_indented_and_a_running_job_has_a_null_summary()
    {
        var json = JobRecords.ToJson(Record(null));
        Assert.Contains('\n', json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Timestamps_are_iso_8601_utc_with_a_Z_offset()
    {
        using var doc = JsonDocument.Parse(JobRecords.ToJson(Record(null)));
        var created = doc.RootElement.GetProperty("job").GetProperty("createdAt").GetString()!;
        Assert.Equal("2026-10-02T22:30:00.0000000Z", created);
        var at = doc.RootElement.GetProperty("states")[1].GetProperty("at").GetString()!;
        Assert.Equal("2026-10-02T22:30:02.0000000Z", at);
        Assert.Equal(
            Created.UtcDateTime,
            DateTimeOffset.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime);
    }

    [Fact]
    public void Json_keeps_at_most_the_recorded_error_cap_but_reports_the_true_total()
    {
        var count = JobRecords.MaxRecordedErrors + 500;
        var summary = new JobSummary(Id, JobState.DoneWithErrors, 0, 0, Errors(count)) { TotalErrors = count };
        using var doc = JsonDocument.Parse(JobRecords.ToJson(Record(summary)));
        var s = doc.RootElement.GetProperty("summary");
        Assert.Equal(JobRecords.MaxRecordedErrors, s.GetProperty("errors").GetArrayLength());
        Assert.Equal(count, s.GetProperty("totalErrors").GetInt32());
        // The first errors are the ones kept.
        Assert.Equal(@"C:\src\f0.bin", s.GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Exactly_the_cap_is_kept_whole()
    {
        var summary = new JobSummary(Id, JobState.DoneWithErrors, 0, 0, Errors(JobRecords.MaxRecordedErrors))
        {
            TotalErrors = JobRecords.MaxRecordedErrors,
        };
        using var doc = JsonDocument.Parse(JobRecords.ToJson(Record(summary)));
        Assert.Equal(JobRecords.MaxRecordedErrors, doc.RootElement.GetProperty("summary").GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public void History_line_is_one_compact_line_with_the_documented_fields()
    {
        var summary = new JobSummary(Id, JobState.Done, 7, 4096, []) { TotalErrors = 0 };
        var finished = Created.AddMinutes(3);
        var line = JobRecords.ToHistoryLine(Job(), summary, finished);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        Assert.Equal(Id, r.GetProperty("id").GetGuid());
        Assert.Equal("move", r.GetProperty("verb").GetString());
        Assert.Equal(2, r.GetProperty("sources").GetArrayLength());
        Assert.Equal(@"D:\dest", r.GetProperty("destination").GetString());
        Assert.Equal("2026-10-02T22:30:00.0000000Z", r.GetProperty("createdAt").GetString());
        Assert.Equal("2026-10-02T22:33:00.0000000Z", r.GetProperty("finishedAt").GetString());
        Assert.Equal("done", r.GetProperty("finalState").GetString());
        Assert.Equal(7, r.GetProperty("completedFiles").GetInt64());
        Assert.Equal(4096, r.GetProperty("completedBytes").GetInt64());
        Assert.Equal(0, r.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void History_line_stays_one_line_when_a_path_contains_line_breaks()
    {
        var job = Job() with { Sources = ["C:\\src\\a\r\nb\u2028c.txt"], Destination = "D:\\x\ny" };
        var summary = new JobSummary(Id, JobState.Failed, 0, 0, []) { TotalErrors = 3 };
        var line = JobRecords.ToHistoryLine(job, summary, Created);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("C:\\src\\a\r\nb\u2028c.txt", doc.RootElement.GetProperty("sources")[0].GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void History_error_count_is_the_total_not_the_capped_list()
    {
        var summary = new JobSummary(Id, JobState.DoneWithErrors, 0, 0, Errors(JobRecords.MaxRecordedErrors)) { TotalErrors = 25_000 };
        using var doc = JsonDocument.Parse(JobRecords.ToHistoryLine(Job(), summary, Created));
        Assert.Equal(25_000, doc.RootElement.GetProperty("errorCount").GetInt32());
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.AwaitingDecision)]
    [InlineData(JobState.DoneWithErrors)]
    [InlineData(JobState.Canceled)]
    public void LastState_reads_the_last_recorded_state_of_a_written_record(JobState last)
    {
        var record = Record(null) with
        {
            States = [new StateChange(JobState.Queued, Created), new StateChange(last, Created.AddSeconds(5))],
        };
        Assert.Equal(last, JobRecords.LastState(JobRecords.ToJson(record)));
    }

    [Fact]
    public void LastState_is_null_for_a_truncated_record()
    {
        var json = JobRecords.ToJson(Record(null));
        Assert.Null(JobRecords.LastState(json[..(json.Length / 2)]));
        Assert.Null(JobRecords.LastState(json[..^1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("\0\0\0\0")]
    [InlineData("[]")]
    [InlineData("[{\"state\":\"running\"}]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("\"running\"")]
    [InlineData("{}")]
    [InlineData("{\"states\":null}")]
    [InlineData("{\"states\":{}}")]
    [InlineData("{\"states\":[]}")]
    [InlineData("{\"states\":[1]}")]
    [InlineData("{\"states\":[{}]}")]
    [InlineData("{\"states\":[{\"state\":5}]}")]
    [InlineData("{\"states\":[{\"state\":\"exploded\"}]}")]
    [InlineData("{\"states\":[{\"state\":\"99\"}]}")]
    public void LastState_is_null_for_anything_that_is_not_a_job_record(string text)
    {
        Assert.Null(JobRecords.LastState(text));
    }

    // Enum.TryParse would accept each of these; the comma lists OR to a different state
    // ("running, paused" is 3 | 4 = DoneWithErrors), turning an interrupted job terminal.
    [Theory]
    [InlineData("running, paused")]
    [InlineData("queued,running")]
    [InlineData(" running")]
    [InlineData("running ")]
    [InlineData("Running")]
    [InlineData("RUNNING")]
    [InlineData("3")]
    [InlineData("-1")]
    public void LastState_accepts_only_the_exact_names_it_writes(string name)
    {
        Assert.Null(JobRecords.LastState("{\"states\":[{\"state\":\"" + name + "\"}]}"));
    }

    [Fact]
    public void An_unpaired_surrogate_in_a_path_does_not_throw()
    {
        // NTFS names are arbitrary UTF-16, so a real source path can hold a lone surrogate.
        // The formats must still be produced (lossily, as U+FFFD) rather than fail the sink.
        var job = Job() with { Sources = ["C:\\src\\a\uD800b", "C:\\src\\\uDC00"], Destination = "D:\\x\uD83D" };
        var summary = new JobSummary(Id, JobState.Done, 1, 1, [new ErrorReported(5, "Copying File", "C:\\\uD800", "denied")]);

        using var record = JsonDocument.Parse(JobRecords.ToJson(Record(summary) with { Job = job }));
        Assert.Equal("C:\\src\\a\uFFFDb", record.RootElement.GetProperty("job").GetProperty("sources")[0].GetString());

        var line = JobRecords.ToHistoryLine(job, summary, Created);
        Assert.DoesNotContain('\n', line);
        using var history = JsonDocument.Parse(line);
        Assert.Equal("D:\\x\uFFFD", history.RootElement.GetProperty("destination").GetString());
    }

    [Fact]
    public void LastState_survives_pathological_nesting()
    {
        Assert.Null(JobRecords.LastState(new string('[', 10_000)));
    }
}

/// <summary>job.json and history lines stay small whatever the selection or the number of commands.</summary>
public class JobRecordBoundsTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static JobDescription Job(int sources) => new(
        Guid.NewGuid(),
        TransferVerb.Copy,
        Enumerable.Range(0, sources).Select(i => $@"C:\photos\2026\IMG_{i:D6}.jpg").ToList(),
        @"D:\backup",
        Created);

    [Fact]
    public void A_huge_selection_records_its_first_sources_and_their_count()
    {
        var job = Job(250_000);
        var summary = new JobSummary(job.Id, JobState.Done, 250_000, 1, []);

        var json = JobRecords.ToJson(new JobRecord(job, [new StateChange(JobState.Queued, Created)], [], summary));
        var line = JobRecords.ToHistoryLine(job, summary, Created.AddHours(1));

        using var record = JsonDocument.Parse(json);
        var recorded = record.RootElement.GetProperty("job").GetProperty("sources").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(job.Sources.Take(JobRecords.MaxRecordedSources), recorded);
        Assert.Equal(250_000, record.RootElement.GetProperty("job").GetProperty("sourceCount").GetInt32());
        Assert.True(json.Length < 32 * 1024, $"job.json was {json.Length} characters");

        using var history = JsonDocument.Parse(line);
        Assert.Equal(JobRecords.MaxRecordedSources, history.RootElement.GetProperty("sources").GetArrayLength());
        Assert.Equal(250_000, history.RootElement.GetProperty("sourceCount").GetInt32());
        Assert.True(line.Length < 32 * 1024, $"history line was {line.Length} characters");
    }

    [Fact]
    public void Very_long_source_paths_stop_at_the_character_budget_but_one_always_fits()
    {
        var longPath = @"C:\" + new string('d', 30_000) + @"\file.txt";
        var sources = new[] { longPath, longPath, @"C:\short.txt" };

        Assert.Equal([longPath], JobRecords.RecordedSources(sources));
        Assert.Empty(JobRecords.RecordedSources([]));
    }

    [Fact]
    public void Job_json_keeps_the_most_recent_commands_cut_short_and_their_count()
    {
        var job = Job(1);
        var names = string.Join(' ', Enumerable.Range(0, 1_000).Select(i => $"\"IMG_{i:D6}.jpg\""));
        var commands = Enumerable.Range(0, 300).Select(i => new CommandRecord($"#{i:D3} \"C:\\photos\" \"D:\\backup\" {names}", i)).ToList();

        var json = JobRecords.ToJson(new JobRecord(job, [new StateChange(JobState.Queued, Created)], commands, null), commandCount: 300);

        using var record = JsonDocument.Parse(json);
        var written = record.RootElement.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(JobRecords.MaxRecordedCommands, written.Count);
        Assert.StartsWith("#200 ", written[0].GetProperty("arguments").GetString());
        Assert.Equal(299, written[^1].GetProperty("exitCode").GetInt32());
        Assert.All(written, c => Assert.True(c.GetProperty("arguments").GetString()!.Length <= JobRecords.MaxRecordedArgumentChars + 1));
        Assert.Equal(300, record.RootElement.GetProperty("commandCount").GetInt32());
        Assert.True(json.Length < 512 * 1024, $"job.json was {json.Length} characters");
    }

    [Theory]
    [InlineData(0, 0L, false)]
    [InlineData(9_999, 1_000_000L, false)]
    [InlineData(10_000, 1_000L, true)]
    [InlineData(10, 8L * 1024 * 1024, true)]
    public void History_rotates_by_lines_or_by_size(int lines, long bytes, bool rotate) =>
        Assert.Equal(rotate, JobRecords.ShouldRotateHistory(lines, bytes));

    [Fact]
    public void A_capped_record_still_reads_as_interrupted_and_can_be_marked()
    {
        var job = Job(5_000);
        var json = JobRecords.ToJson(new JobRecord(job, [new StateChange(JobState.Queued, Created), new StateChange(JobState.Running, Created)], [], null));

        Assert.Equal(InterruptedCheck.MarkAndReport, JobRecords.CheckInterrupted(json, Created.AddDays(1)));
        Assert.NotNull(JobRecords.MarkInterrupted(json, Created.AddDays(1)));
    }
}
