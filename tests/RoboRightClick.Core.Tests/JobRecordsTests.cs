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

    [Fact]
    public void LastState_survives_pathological_nesting()
    {
        Assert.Null(JobRecords.LastState(new string('[', 10_000)));
    }
}
