using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class CrashLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 8, 30, 0, TimeSpan.FromHours(-7));

    private static CrashFacts Facts(params CrashLayer[] layers) => new(At, "1.0.0-beta.1", "10.0.26200", UiThread: true, layers);

    [Theory]
    [InlineData(LoggingMode.Normal, false, true)]
    [InlineData(LoggingMode.Normal, true, false)]
    [InlineData(LoggingMode.Ephemeral, false, false)]
    [InlineData(LoggingMode.Ephemeral, true, false)]
    public void Written_only_in_normal_mode_with_no_ephemeral_job_this_session(LoggingMode mode, bool ephemeralJobs, bool expected)
    {
        Assert.Equal(expected, CrashLog.MayWrite(mode, ephemeralJobs));
    }

    [Fact]
    public void Not_written_when_the_mode_is_unknown()
    {
        // No tray (install, uninstall, CLI), or the question failed at crash time.
        Assert.False(CrashLog.MayWrite(null, ephemeralJobsThisSession: false));
    }

    [Theory]
    // A tray that fails while starting has no job manager yet: config.json decides.
    [InlineData(null, false, true)]
    [InlineData("""{ "logging": "normal" }""", false, true)]
    [InlineData("""{ "threads": 4 }""", false, true)]
    [InlineData("""{ "logging": "ephemeral" }""", false, false)]
    [InlineData("""{ "version": 2, "logging": "ephemeral", "future": 1 }""", false, false)]
    // Unreadable: it may say ephemeral.
    [InlineData(null, true, false)]
    [InlineData("""{ "logging": "ephemeral" """, false, false)]
    [InlineData("[]", false, false)]
    public void A_failed_start_is_logged_only_when_config_json_says_normal_mode(string? configText, bool readFailed, bool expected)
    {
        Assert.Equal(expected, CrashLog.MayWrite(CrashLog.ModeFromConfig(configText, readFailed), ephemeralJobsThisSession: false));
    }

    [Fact]
    public void Paths_live_in_the_data_folder()
    {
        var paths = AppPaths.From(@"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming");
        Assert.Equal(@"C:\Users\u\AppData\Local\RoboRightClick\crash.log", paths.CrashLogFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\RoboRightClick\crash.1.log", paths.RotatedCrashLogFile);
    }

    [Fact]
    public void Uninstall_deletes_both_crash_logs()
    {
        var paths = AppPaths.From(@"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming");
        foreach (var running in new[] { true, false })
        {
            var plan = UninstallPlan.For(paths, [], running);
            Assert.Contains(paths.CrashLogFile, plan.Files);
            Assert.Contains(paths.RotatedCrashLogFile, plan.Files);
        }
    }

    [Theory]
    [InlineData(0, 300_000, false)]
    [InlineData(1, 100, false)]
    [InlineData(CrashLog.RotateAtBytes - 100, 100, false)]
    [InlineData(CrashLog.RotateAtBytes - 100, 101, true)]
    [InlineData(CrashLog.RotateAtBytes, 1, true)]
    [InlineData(CrashLog.RotateAtBytes * 4, 1, true)]
    public void Rotates_before_the_log_would_pass_256_KB(long existing, long entry, bool rotate)
    {
        Assert.Equal(256 * 1024, CrashLog.RotateAtBytes);
        Assert.Equal(rotate, CrashLog.ShouldRotate(existing, entry));
    }

    [Fact]
    public void An_entry_carries_type_message_stack_version_utc_time_and_os_build()
    {
        var entry = CrashLog.Format(Facts(new CrashLayer("System.InvalidOperationException", "Operation is not valid.", "   at A.B()\r\n   at C.D()")));

        Assert.StartsWith("=== 2026-10-02T15:30:00.0000000Z | RoboRightClick 1.0.0-beta.1 | Windows 10.0.26200 | thread UI\n", entry);
        Assert.Contains("System.InvalidOperationException: Operation is not valid.\n", entry);
        Assert.Contains("   at A.B()\n   at C.D()\n", entry);
        Assert.DoesNotContain('\r', entry);
        Assert.EndsWith("\n\n", entry);
    }

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\Ann\Secret Plans\budget.xlsx'.", "Secret")]
    [InlineData(@"Could not find a part of the path 'D:\dest\x'.", "dest")]
    [InlineData(@"Access to the path '\\server\share\HR\salaries.txt' is denied.", "salaries")]
    [InlineData(@"The file ""C:\a b\c.txt"" exists.", "c.txt")]
    [InlineData(@"Unquoted C:\Users\Ann\notes.txt here", "notes")]
    [InlineData(@"Bad name in /home/ann/report.pdf", "report")]
    [InlineData("Line one\r\nE:\\x\\y.txt", "y.txt")]
    [InlineData("The process cannot access the file 'Bob's taxes 2025.pdf' because it is being used", "taxes")]
    [InlineData("Unterminated 'quote with C-name inside", "C-name")]
    [InlineData("“Curly quoted name.docx” failed", "Curly")]
    public void Path_looking_parts_of_a_message_are_scrubbed(string message, string secret)
    {
        var entry = CrashLog.Format(Facts(new CrashLayer("System.IO.IOException", message, null)));
        var messageLine = entry.Split('\n')[1];

        Assert.DoesNotContain(secret, messageLine);
        Assert.Contains(PathHeuristic.Placeholder, messageLine);
    }

    [Theory]
    [InlineData("Operation is not valid due to the current state of the object.")]
    [InlineData("Value cannot be null. (Parameter 'items')")]
    [InlineData("Index was outside the bounds of the array.")]
    [InlineData("Error: don't stop")]
    public void Path_free_messages_keep_their_words(string message)
    {
        var scrubbed = PathHeuristic.Scrub(message, CrashLog.MaxMessageChars);
        foreach (var word in new[] { "Operation", "null", "bounds", "don't" }.Where(message.Contains))
        {
            Assert.Contains(word, scrubbed);
        }
    }

    [Fact]
    public void The_scrubbed_message_is_one_bounded_line()
    {
        var scrubbed = PathHeuristic.Scrub(string.Join("\n", Enumerable.Repeat("word", 2_000)), CrashLog.MaxMessageChars);
        Assert.DoesNotContain('\n', scrubbed);
        Assert.True(scrubbed.Length <= CrashLog.MaxMessageChars);
    }

    [Fact]
    public void Inner_exceptions_follow_the_outer_one_and_are_scrubbed_too()
    {
        Exception thrown;
        try
        {
            try
            {
                throw new FileNotFoundException(@"Could not find file 'C:\private\inner.txt'.");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Outer failure.", inner);
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var layers = CrashLog.LayersOf(thrown);
        Assert.Equal(["System.InvalidOperationException", "System.IO.FileNotFoundException"], layers.Select(l => l.TypeName));
        Assert.NotNull(layers[0].StackTrace);

        var entry = CrashLog.Format(Facts([.. layers]));
        Assert.Contains("--- inner: System.IO.FileNotFoundException: Could not find file [path]", entry);
        Assert.DoesNotContain("private", entry);
        Assert.Contains(nameof(Inner_exceptions_follow_the_outer_one_and_are_scrubbed_too), entry);
    }

    [Fact]
    public void Every_exception_of_an_aggregate_is_listed_up_to_the_cap()
    {
        var aggregate = new AggregateException(Enumerable.Range(0, 20).Select(i => new InvalidOperationException($"failure {i}")));
        var layers = CrashLog.LayersOf(aggregate);
        Assert.Equal(CrashLog.MaxLayers, layers.Count);
        Assert.Equal("System.AggregateException", layers[0].TypeName);
        Assert.Equal("failure 0", layers[1].Message);
    }

    [Fact]
    public void An_entry_is_bounded_well_below_the_rotation_size()
    {
        var hugeStack = string.Join("\n", Enumerable.Repeat("   at Some.Very.Long.Namespace.Type.Method(String a, Int32 b) 日本語", 20_000));
        var layers = Enumerable.Range(0, 20).Select(_ => new CrashLayer("X", new string('m', 50_000), hugeStack)).ToArray();
        var entry = CrashLog.Format(Facts(layers));

        Assert.True(entry.Length <= CrashLog.MaxEntryChars, $"{entry.Length} chars");
        Assert.True(Encoding.UTF8.GetByteCount(entry) < CrashLog.RotateAtBytes);
        Assert.EndsWith("\n\n", entry);
    }

    [Fact]
    public void A_header_with_control_characters_stays_on_one_line()
    {
        var entry = CrashLog.Format(new CrashFacts(At, "1.0\r\n=== fake", "10.0\n1", UiThread: false, [new CrashLayer("T\nU", "m", null)]));
        var lines = entry.Split('\n');
        Assert.EndsWith("thread worker", lines[0]);
        Assert.StartsWith("T U: m", lines[1]);
        // A version string cannot start a forged entry of its own.
        Assert.Single(lines, l => l.StartsWith("===", StringComparison.Ordinal));
    }
}
