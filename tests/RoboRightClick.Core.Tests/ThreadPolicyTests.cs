using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class ThreadPolicyTests
{
    private static readonly Settings Auto = Settings.Default;

    [Theory]
    [InlineData(DriveMedium.SolidState, DriveMedium.SolidState, false, 32)]
    [InlineData(DriveMedium.SolidState, DriveMedium.Network, false, 32)]
    [InlineData(DriveMedium.Network, DriveMedium.Network, false, 32)]
    [InlineData(DriveMedium.SolidState, DriveMedium.Rotational, false, 8)]
    [InlineData(DriveMedium.Rotational, DriveMedium.SolidState, false, 8)]
    [InlineData(DriveMedium.Rotational, DriveMedium.Network, false, 8)]
    [InlineData(DriveMedium.Rotational, DriveMedium.Rotational, false, 8)]
    [InlineData(DriveMedium.Rotational, DriveMedium.Rotational, true, 4)]
    [InlineData(DriveMedium.Unknown, DriveMedium.SolidState, false, 32)]
    [InlineData(DriveMedium.Unknown, DriveMedium.Rotational, false, 8)]
    public void Auto_uses_the_slower_end(DriveMedium source, DriveMedium destination, bool sameDisk, int expected) =>
        Assert.Equal(expected, ThreadPolicy.Resolve(Auto, source, destination, sameDisk));

    [Fact]
    public void Solid_state_and_network_keep_the_documented_MT32()
    {
        // The install default promised robocopy /MT:32; auto must still give exactly that
        // wherever no spinning disk is involved.
        Assert.Equal(32, ThreadPolicy.Resolve(Auto, DriveMedium.SolidState, DriveMedium.SolidState, sameDisk: true));
        Assert.Equal(32, ThreadPolicy.Resolve(Auto, DriveMedium.Unknown, DriveMedium.Unknown, sameDisk: false));
    }

    [Fact]
    public void A_fixed_count_wins_on_every_medium()
    {
        var fixedCount = Settings.Default with { AutoThreads = false, Threads = 64 };
        foreach (var source in Enum.GetValues<DriveMedium>())
        {
            foreach (var destination in Enum.GetValues<DriveMedium>())
            {
                Assert.Equal(64, ThreadPolicy.Resolve(fixedCount, source, destination, sameDisk: true));
            }
        }
    }

    [Fact]
    public void Every_resolved_count_is_a_valid_robocopy_MT_value()
    {
        foreach (var source in Enum.GetValues<DriveMedium>())
        {
            foreach (var destination in Enum.GetValues<DriveMedium>())
            {
                foreach (var sameDisk in new[] { false, true })
                {
                    var n = ThreadPolicy.Resolve(Auto, source, destination, sameDisk);
                    Assert.InRange(n, Settings.MinThreads, Settings.MaxThreads);
                }
            }
        }
    }

    [Theory]
    [InlineData("""{ "threads": "auto" }""", true, 32)]
    [InlineData("""{ "threads": "AUTO" }""", true, 32)]
    [InlineData("""{ "threads": 12 }""", false, 12)]
    [InlineData("""{ }""", true, 32)]
    public void Config_threads_is_auto_or_a_count(string json, bool auto, int threads)
    {
        var result = SettingsSerializer.Parse(json);
        Assert.Empty(result.Problems);
        Assert.Equal(auto, result.Settings.AutoThreads);
        Assert.Equal(threads, result.Settings.Threads);
    }

    [Theory]
    [InlineData("""{ "threads": "fast" }""")]
    [InlineData("""{ "threads": 0 }""")]
    [InlineData("""{ "threads": 129 }""")]
    [InlineData("""{ "threads": true }""")]
    public void Bad_threads_fall_back_to_auto_with_a_problem(string json)
    {
        var result = SettingsSerializer.Parse(json);
        Assert.True(result.Settings.AutoThreads);
        Assert.Contains(result.Problems, p => p.Contains("threads", StringComparison.Ordinal));
    }

    [Fact]
    public void A_classification_failure_never_reaches_the_job_and_uses_the_fallback()
    {
        var choice = ThreadPolicy.Choose(Auto, () => throw new InvalidOperationException("volume query failed"));
        Assert.Equal(ThreadPolicy.Fallback, choice.Threads);
        Assert.True(choice.ClassificationFailed);
        Assert.Null(choice.Drives);
        Assert.Contains($" /MT:{ThreadPolicy.Fallback} ", RobocopyArgs.Build(Step, choice.ApplyTo(Auto), ConflictPolicy.Ask, "p"));
    }

    [Fact]
    public void A_fixed_count_never_classifies()
    {
        var fixedCount = Settings.Default with { AutoThreads = false, Threads = 64 };
        var choice = ThreadPolicy.Choose(fixedCount, () => throw new InvalidOperationException("must not be called"));
        Assert.Equal(64, choice.Threads);
        Assert.False(choice.ClassificationFailed);
        Assert.Null(choice.Drives);
    }

    [Theory]
    [InlineData(DriveMedium.Rotational, DriveMedium.Rotational, true, 4)]
    [InlineData(DriveMedium.SolidState, DriveMedium.Rotational, false, 8)]
    [InlineData(DriveMedium.Network, DriveMedium.SolidState, false, 32)]
    public void The_chosen_count_is_the_MT_value_robocopy_receives(DriveMedium source, DriveMedium destination, bool sameDisk, int expected)
    {
        var choice = ThreadPolicy.Choose(Auto, () => new DrivePair(source, destination, sameDisk));
        Assert.Equal(expected, choice.Threads);
        var args = RobocopyArgs.Build(Step, choice.ApplyTo(Auto), ConflictPolicy.Ask, "p");
        Assert.Contains($" /MT:{expected} ", args);
        Assert.Single(args.Split(' '), a => a.StartsWith("/MT:", StringComparison.Ordinal));
    }

    [Fact]
    public void Applying_a_choice_changes_only_the_thread_count()
    {
        var settings = Settings.Default with { Retries = 3, RetryWaitSeconds = 7 };
        var applied = new ThreadChoice(8, new DrivePair(DriveMedium.Rotational, DriveMedium.SolidState, false), false).ApplyTo(settings);
        Assert.Equal(settings with { AutoThreads = false, Threads = 8 }, applied);
    }

    [Theory]
    [InlineData(DriveMedium.Rotational, DriveMedium.Rotational, true, "# threads /MT:4 (auto: source rotational, destination rotational, same disk)")]
    [InlineData(DriveMedium.SolidState, DriveMedium.Network, false, "# threads /MT:32 (auto: source solid-state, destination network)")]
    [InlineData(DriveMedium.Unknown, DriveMedium.Rotational, false, "# threads /MT:8 (auto: source unknown, destination rotational)")]
    public void The_log_line_names_the_count_and_both_media(DriveMedium source, DriveMedium destination, bool sameDisk, string expected) =>
        Assert.Equal(expected, ThreadPolicy.Choose(Auto, () => new DrivePair(source, destination, sameDisk)).LogLine);

    [Fact]
    public void The_log_line_says_why_when_nothing_was_classified()
    {
        Assert.Equal(
            "# threads /MT:32 (auto: drives could not be classified, default used)",
            ThreadPolicy.Choose(Auto, () => throw new IOException()).LogLine);
        Assert.Equal(
            "# threads /MT:12 (fixed in settings)",
            ThreadPolicy.Choose(Settings.Default with { AutoThreads = false, Threads = 12 }, () => default).LogLine);
    }

    [Fact]
    public void The_log_line_never_carries_a_path()
    {
        // Drives are described by medium only, so the line can be quoted in a bug report or
        // the test log without exposing a drive letter or share name.
        foreach (var source in Enum.GetValues<DriveMedium>())
        {
            foreach (var destination in Enum.GetValues<DriveMedium>())
            {
                var line = ThreadPolicy.Choose(Auto, () => new DrivePair(source, destination, true)).LogLine;
                Assert.DoesNotContain(":\\", line, StringComparison.Ordinal);
                Assert.DoesNotContain("\\\\", line, StringComparison.Ordinal);
                Assert.Matches("^# threads /MT:[0-9]+ \\([a-z ,:-]+\\)$", line);
            }
        }
    }

    private static readonly RobocopyStep Step = new(@"C:\src", @"D:\dst", ["a.txt"], Recursive: false, Move: false);

    [Fact]
    public void Auto_round_trips_as_the_word_auto()
    {
        var json = SettingsSerializer.Serialize(Settings.Default);
        Assert.Contains("\"threads\": \"auto\"", json, StringComparison.Ordinal);
        Assert.Equal(Settings.Default, SettingsSerializer.Parse(json).Settings);
    }
}
