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
    public void Auto_round_trips_as_the_word_auto()
    {
        var json = SettingsSerializer.Serialize(Settings.Default);
        Assert.Contains("\"threads\": \"auto\"", json, StringComparison.Ordinal);
        Assert.Equal(Settings.Default, SettingsSerializer.Parse(json).Settings);
    }
}
