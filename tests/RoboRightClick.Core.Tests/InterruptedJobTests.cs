using System.Text.Json;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// The startup check for jobs a previous run left mid-paste: reported once, then marked
/// terminal on disk; live jobs of the current run and newer-version records are not rewritten.
/// </summary>
public class InterruptedJobTests
{
    private static readonly Guid Id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextStart = Created.AddHours(1);
    private static readonly DateTimeOffset MarkedAt = NextStart.AddSeconds(3);

    private static string RecordEndingIn(JobState last, DateTimeOffset? createdAt = null)
    {
        var at = createdAt ?? Created;
        var job = new JobDescription(Id, TransferVerb.Move, [@"C:\src\a.txt"], @"D:\dest", at);
        var states = last == JobState.Queued
            ? new[] { new StateChange(JobState.Queued, at) }
            : [new StateChange(JobState.Queued, at), new StateChange(last, at.AddSeconds(1))];
        return JobRecords.ToJson(new JobRecord(job, states, [new CommandRecord("/MT:32", null)], null));
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Scanning)]
    [InlineData(JobState.AwaitingDecision)]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Paused)]
    [InlineData(JobState.Finalizing)]
    public void A_record_left_in_a_non_terminal_state_is_marked_and_reported(JobState last)
    {
        Assert.Equal(InterruptedCheck.MarkAndReport, JobRecords.CheckInterrupted(RecordEndingIn(last), NextStart));
    }

    [Theory]
    [InlineData(JobState.Done)]
    [InlineData(JobState.DoneWithErrors)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Canceled)]
    public void A_finished_record_is_left_alone(JobState last)
    {
        var json = RecordEndingIn(last);
        Assert.Equal(InterruptedCheck.None, JobRecords.CheckInterrupted(json, NextStart));
        Assert.Null(JobRecords.MarkInterrupted(json, MarkedAt));
    }

    [Fact]
    public void A_marked_record_is_not_reported_again()
    {
        // The point of marking: the notice appears on one start, not on every sign-in.
        var marked = JobRecords.MarkInterrupted(RecordEndingIn(JobState.Running), MarkedAt);
        Assert.NotNull(marked);
        Assert.Equal(InterruptedCheck.None, JobRecords.CheckInterrupted(marked, NextStart.AddDays(1)));
        Assert.Null(JobRecords.MarkInterrupted(marked, MarkedAt.AddDays(1)));
        Assert.Null(JobRecords.LastState(marked));
    }

    [Fact]
    public void Marking_appends_one_terminal_state_and_keeps_every_other_field()
    {
        var original = RecordEndingIn(JobState.Running);
        var marked = JobRecords.MarkInterrupted(original, MarkedAt)!;

        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(marked);
        var states = after.RootElement.GetProperty("states").EnumerateArray().ToList();
        Assert.Equal(3, states.Count);
        Assert.Equal("running", states[1].GetProperty("state").GetString());
        Assert.Equal(JobRecords.InterruptedStateName, states[2].GetProperty("state").GetString());
        Assert.Equal("2026-10-02T16:30:03.0000000Z", states[2].GetProperty("at").GetString());

        foreach (var property in before.RootElement.EnumerateObject().Where(p => p.Name != "states"))
        {
            Assert.Equal(property.Value.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""),
                after.RootElement.GetProperty(property.Name).GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        }
    }

    [Fact]
    public void Unknown_fields_of_a_record_of_this_version_survive_marking()
    {
        var json = """{"version":1,"extra":{"kept":[1,2]},"job":{"createdAt":"2026-10-02T15:30:00Z"},"states":[{"state":"running","at":"x"}]}""";
        var marked = JobRecords.MarkInterrupted(json, MarkedAt)!;
        using var doc = JsonDocument.Parse(marked);
        Assert.Equal(2, doc.RootElement.GetProperty("extra").GetProperty("kept")[1].GetInt32());
    }

    [Fact]
    public void A_job_created_by_the_running_process_is_not_touched()
    {
        // COM can deliver a right-click before the startup check runs; that job's record is
        // live and non-terminal, and marking it would report a paste that is still running.
        var processStart = Created;
        Assert.Equal(InterruptedCheck.CurrentRun, JobRecords.CheckInterrupted(RecordEndingIn(JobState.Running, Created), processStart));
        Assert.Equal(InterruptedCheck.CurrentRun, JobRecords.CheckInterrupted(RecordEndingIn(JobState.Queued, Created.AddMilliseconds(1)), processStart));
        Assert.Equal(InterruptedCheck.MarkAndReport, JobRecords.CheckInterrupted(RecordEndingIn(JobState.Queued, Created.AddTicks(-1)), processStart));
    }

    [Fact]
    public void The_start_time_comparison_is_in_utc()
    {
        // createdAt is written in UTC; a local-offset start time must compare by instant.
        var startInLocalOffset = new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.FromHours(-7)); // = Created
        Assert.Equal(InterruptedCheck.CurrentRun, JobRecords.CheckInterrupted(RecordEndingIn(JobState.Running), startInLocalOffset));
        Assert.Equal(InterruptedCheck.MarkAndReport, JobRecords.CheckInterrupted(RecordEndingIn(JobState.Running), startInLocalOffset.AddSeconds(1)));
    }

    [Fact]
    public void A_record_without_a_creation_time_is_from_an_earlier_run()
    {
        var json = """{"states":[{"state":"running"}]}""";
        Assert.Equal(InterruptedCheck.MarkAndReport, JobRecords.CheckInterrupted(json, NextStart));
    }

    [Theory]
    [InlineData("""{"version":2,"states":[{"state":"running"}]}""")]
    [InlineData("""{"version":"1","states":[{"state":"running"}]}""")]
    [InlineData("""{"version":0,"states":[{"state":"paused"}]}""")]
    public void A_newer_or_unreadable_version_is_reported_but_never_rewritten(string json)
    {
        Assert.Equal(InterruptedCheck.ReportOnly, JobRecords.CheckInterrupted(json, NextStart));
        Assert.Null(JobRecords.MarkInterrupted(json, MarkedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"states":[]}""")]
    [InlineData("""{"states":[{"state":"exploded"}]}""")]
    [InlineData("""{"states":"running"}""")]
    [InlineData("""{"states":[{"state":"running"}],"states":[{"state":"done"}]}""")]
    // JsonDocument reads the last duplicate (running) and passes it on; JsonObject then
    // refuses the duplicated key, and the file is left as it is.
    [InlineData("""{"states":[{"state":"done"}],"states":[{"state":"running"}]}""")]
    public void Anything_that_is_not_an_unfinished_record_is_never_rewritten(string json)
    {
        Assert.Null(JobRecords.MarkInterrupted(json, MarkedAt));
        Assert.NotEqual(InterruptedCheck.CurrentRun, JobRecords.CheckInterrupted(json, NextStart));
    }

    [Fact]
    public void A_truncated_record_is_not_reported()
    {
        var json = RecordEndingIn(JobState.Running);
        Assert.Equal(InterruptedCheck.None, JobRecords.CheckInterrupted(json[..(json.Length / 2)], NextStart));
        Assert.Null(JobRecords.MarkInterrupted(json[..^1], MarkedAt));
    }

    [Fact]
    public void Pathological_nesting_is_not_a_record()
    {
        var json = new string('[', 10_000);
        Assert.Equal(InterruptedCheck.None, JobRecords.CheckInterrupted(json, NextStart));
        Assert.Null(JobRecords.MarkInterrupted(json, MarkedAt));
    }
}
