using System.Text.Json;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// Downgrade safety: files carry a format version, older builds read what they know from a
/// newer file and never save over it.
/// </summary>
public class SchemaVersionTests
{
    private static readonly Guid Id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Config_is_written_with_the_current_version()
    {
        using var doc = JsonDocument.Parse(SettingsSerializer.Serialize(Settings.Default));
        Assert.Equal(SettingsSerializer.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, SettingsSerializer.CurrentVersion);
    }

    [Fact]
    public void Config_without_a_version_is_version_1_and_reports_nothing()
    {
        var result = SettingsSerializer.Parse("""{ "threads": 8 }""");
        Assert.Equal(1, result.Version);
        Assert.False(result.WrittenByNewerVersion);
        Assert.Empty(result.Problems);
        Assert.True(SettingsSerializer.MayOverwrite("""{ "threads": 8 }"""));
    }

    [Fact]
    public void Config_of_the_current_version_round_trips_without_problems()
    {
        var text = SettingsSerializer.Serialize(Settings.Default with { Retries = 2 });
        var result = SettingsSerializer.Parse(text);
        Assert.Empty(result.Problems);
        Assert.Equal(SettingsSerializer.CurrentVersion, result.Version);
        Assert.Equal(2, result.Settings.Retries);
        Assert.True(SettingsSerializer.MayOverwrite(text));
    }

    [Fact]
    public void Config_from_a_newer_version_loads_known_fields_and_reports_once()
    {
        var newer = $$"""
            {
              "version": {{SettingsSerializer.CurrentVersion + 1}},
              "threads": 16,
              "logging": "ephemeral",
              "someFutureSetting": { "a": 1 },
              "anotherOne": true
            }
            """;
        var result = SettingsSerializer.Parse(newer);

        Assert.True(result.WrittenByNewerVersion);
        Assert.False(result.Unreadable);
        Assert.Equal(16, result.Settings.Threads);
        Assert.False(result.Settings.AutoThreads);
        Assert.Equal(LoggingMode.Ephemeral, result.Settings.Logging);

        // One line about the version, not one per field the newer version added.
        var problem = Assert.Single(result.Problems);
        Assert.Contains("newer version", problem);
        Assert.DoesNotContain("someFutureSetting", problem);
    }

    [Fact]
    public void No_save_may_overwrite_a_config_from_a_newer_version()
    {
        var newer = """{ "version": 2, "threads": "auto" }""";
        Assert.False(SettingsSerializer.MayOverwrite(newer));
        Assert.False(SettingsSerializer.MayOverwrite("""{ "version": 2147483647 }"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "version": "2" }""")]
    [InlineData("""{ "version": 0 }""")]
    [InlineData("""{ "version": -3 }""")]
    [InlineData("""{ "version": 2.5 }""")]
    public void Anything_but_a_newer_version_number_may_be_overwritten(string? existing)
    {
        // Missing files, unreadable files (kept as .bad first) and damaged version fields
        // are this version's to repair; only an integer above the current version is not.
        Assert.True(SettingsSerializer.MayOverwrite(existing));
    }

    [Theory]
    [InlineData("""{ "version": "2" }""")]
    [InlineData("""{ "version": 0 }""")]
    [InlineData("""{ "version": null, "threads": 4 }""")]
    public void A_damaged_version_field_falls_back_like_any_field(string text)
    {
        var result = SettingsSerializer.Parse(text);
        Assert.False(result.WrittenByNewerVersion);
        Assert.Equal(SettingsSerializer.CurrentVersion, result.Version);
    }

    [Fact]
    public void Unknown_settings_are_still_reported_in_a_file_of_this_version()
    {
        var result = SettingsSerializer.Parse("""{ "version": 1, "theads": 8 }""");
        Assert.Contains(result.Problems, p => p.Contains("theads"));
    }

    [Fact]
    public void Job_json_and_history_lines_carry_the_current_version()
    {
        Assert.Equal(1, JobRecords.CurrentVersion);
        using var record = JsonDocument.Parse(JobRecords.ToJson(Record()));
        Assert.Equal(JobRecords.CurrentVersion, record.RootElement.GetProperty("version").GetInt32());

        var summary = new JobSummary(Id, JobState.Done, 1, 1, []);
        using var line = JsonDocument.Parse(JobRecords.ToHistoryLine(Job(), summary, Created));
        Assert.Equal(JobRecords.CurrentVersion, line.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("""{"states":[{"state":"running","at":"2026-10-02T00:00:00Z"}]}""")]
    [InlineData("""{"version":1,"states":[{"state":"running"}]}""")]
    [InlineData("""{"version":7,"future":{"x":[1,2]},"states":[{"state":"running","at":"x","extra":true}],"more":null}""")]
    public void LastState_reads_records_with_a_missing_or_newer_version_and_unknown_fields(string json)
    {
        Assert.Equal(JobState.Running, JobRecords.LastState(json));
    }

    [Theory]
    [InlineData("""{"states":[]}""", false)]
    [InlineData("""{"version":1,"states":[]}""", false)]
    [InlineData("""{"version":2,"states":[]}""", true)]
    [InlineData("""{"version":"1","states":[]}""", true)]
    [InlineData("""{"version":0,"states":[]}""", true)]
    [InlineData("""{"version":1.5,"states":[]}""", true)]
    public void Only_records_of_a_known_version_count_as_rewritable(string json, bool newer)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(newer, JobRecords.IsNewerVersion(doc.RootElement));
    }

    private static JobDescription Job() => new(Id, TransferVerb.Copy, [@"C:\src\a.txt"], @"D:\dest", Created);

    private static JobRecord Record() => new(Job(), [new StateChange(JobState.Queued, Created)], [], null);
}
