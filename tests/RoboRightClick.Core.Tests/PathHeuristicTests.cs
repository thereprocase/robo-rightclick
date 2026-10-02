using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// The path heuristic on its own: <see cref="PathHeuristic.Scrub"/> for crash.log and
/// <see cref="PathHeuristic.IsPathFree"/> for failure text.
/// </summary>
public class PathHeuristicTests
{
    private static string Scrub(string text) => PathHeuristic.Scrub(text, CrashLog.MaxMessageChars);

    [Theory]
    [InlineData(@"Access to C:\Users\Ann\Tax Returns 2025\a.pdf was denied", "Returns")]
    [InlineData(@"Could not find D:\Secret Plans", "Plans")]
    [InlineData(@"Failed: C:\Users\Ann\My Project", "Project")]
    [InlineData(@"Copy of \\server\HR share\Pay Review failed", "Review")]
    [InlineData("Bad name /home/ann/Old Photos\u0085Holiday here", "Holiday")]
    public void An_unquoted_path_with_spaces_takes_the_rest_of_its_line_with_it(string message, string secret)
    {
        var scrubbed = Scrub(message);
        Assert.DoesNotContain(secret, scrubbed);
        Assert.EndsWith(PathHeuristic.Placeholder, scrubbed);
    }

    [Fact]
    public void Words_before_the_path_and_lines_after_it_are_kept()
    {
        // Win32 names cannot hold a C0 control character, so a line break ends any path.
        Assert.Equal("Access to [path] Next line kept", Scrub("Access to C:\\x\\Tax Returns\r\nNext line kept"));
        Assert.Equal("[path] kept", Scrub("C:\\x\u0001kept"));
    }

    [Theory]
    [InlineData("copied to D: failed", "D:")]
    [InlineData("C:file.txt is missing", "file.txt")]
    [InlineData("see e:report", "report")]
    public void A_drive_designator_marks_a_path(string message, string secret)
    {
        Assert.DoesNotContain(secret, Scrub(message));
        Assert.False(PathHeuristic.IsPathFree(message));
    }

    [Theory]
    [InlineData("run a<b now", "a<b")]
    [InlineData("run a>b now", "a>b")]
    [InlineData("run a|b now", "a|b")]
    public void Shell_metacharacters_mark_a_path(string message, string word)
    {
        Assert.DoesNotContain(word, Scrub(message));
        Assert.False(PathHeuristic.IsPathFree(message));
    }

    [Theory]
    [InlineData("Error: don't stop")]
    [InlineData("Ratio 3:4 is fine")]
    [InlineData("Operation is not valid due to the current state of the object.")]
    public void Ordinary_colons_and_apostrophes_are_not_paths(string message)
    {
        Assert.Equal(message, Scrub(message));
        Assert.True(PathHeuristic.IsPathFree(message));
    }

    [Theory]
    [InlineData("a\u0001b 'quoted\nname' c\u0085d\u007fe\tf")]
    [InlineData("\u0000\u0007\u001b[31m red")]
    public void No_control_character_reaches_the_log(string message)
    {
        Assert.DoesNotContain(Scrub(message), char.IsControl);
    }
}
