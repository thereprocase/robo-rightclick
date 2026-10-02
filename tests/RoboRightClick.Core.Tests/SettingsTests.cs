using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_match_the_install_contract()
    {
        var d = Settings.Default;
        Assert.True(d.AutoThreads);
        Assert.Equal(32, d.Threads);
        Assert.Equal(0, d.Retries);
        Assert.Equal(0, d.RetryWaitSeconds);
        Assert.Equal(ConflictPolicy.Ask, d.ConflictDefault);
        Assert.Equal(0, d.MaxConcurrentJobs);
        Assert.Equal(LoggingMode.Normal, d.Logging);
        Assert.Equal(ExtraArgs.None, d.ExtraArgs);
        Assert.True(d.ShowProgressWindow);
        Assert.Equal("Ctrl+Shift+V", d.PasteHotkey?.Format());
    }

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        var s = Settings.Default with
        {
            AutoThreads = false,
            Threads = 16,
            Retries = 3,
            RetryWaitSeconds = 2,
            ConflictDefault = ConflictPolicy.KeepNewer,
            MaxConcurrentJobs = 2,
            Logging = LoggingMode.Ephemeral,
            LogRetentionJobs = 7,
            StartWithWindows = false,
            NotifyOnComplete = false,
            ShowProgressWindow = false,
            ExtraArgs = new ExtraArgs("/J", "/Z /IORATE:50M"),
            PasteHotkey = HotkeySpec.Parse("Ctrl+F9").Spec,
        };

        var result = SettingsSerializer.Parse(SettingsSerializer.Serialize(s));

        Assert.Empty(result.Problems);
        Assert.Equal(s, result.Settings);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        var result = SettingsSerializer.Parse("""
            {
              // fewer threads for the NAS
              "threads": 8,
              "logging": "ephemeral",
            }
            """);

        Assert.Empty(result.Problems);
        Assert.False(result.Settings.AutoThreads);
        Assert.Equal(8, result.Settings.Threads);
        Assert.Equal(LoggingMode.Ephemeral, result.Settings.Logging);
    }

    [Fact]
    public void A_bad_field_falls_back_alone()
    {
        var result = SettingsSerializer.Parse("""{ "threads": 500, "conflictDefault": "skip", "logging": "loud" }""");

        Assert.True(result.Settings.AutoThreads);
        Assert.Equal(32, result.Settings.Threads);
        Assert.Equal(ConflictPolicy.Skip, result.Settings.ConflictDefault);
        Assert.Equal(LoggingMode.Normal, result.Settings.Logging);
        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void Numeric_enum_values_are_rejected()
    {
        var result = SettingsSerializer.Parse("""{ "logging": "1" }""");
        Assert.Equal(LoggingMode.Normal, result.Settings.Logging);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Forbidden_extra_args_are_dropped_with_a_problem()
    {
        var result = SettingsSerializer.Parse("""{ "extraArgs": { "copy": "/MIR", "move": "/Z" } }""");

        Assert.Equal(new ExtraArgs(string.Empty, "/Z"), result.Settings.ExtraArgs);
        Assert.Contains(result.Problems, p => p.Contains("/MIR"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void Unreadable_config_yields_defaults(string json)
    {
        var result = SettingsSerializer.Parse(json);
        Assert.Equal(Settings.Default, result.Settings);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Unknown_keys_are_reported_not_fatal()
    {
        var result = SettingsSerializer.Parse("""{ "theads": 8 }""");
        Assert.Equal(Settings.Default, result.Settings);
        Assert.Contains(result.Problems, p => p.Contains("theads"));
    }
}
