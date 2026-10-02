using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// Parser behavior against real robocopy /MT:32 /UNILOG output captured on
/// Windows build 26200 (docs/testlog.md, 2026-10-02, spikes/m0/robocopy-pipe.ps1).
/// </summary>
public class CapturedOutputTests
{
    private static List<RobocopyEvent> ParseFixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var text = Encoding.Unicode.GetString(bytes);
        var parser = new RobocopyOutputParser();
        var events = new List<RobocopyEvent>();
        foreach (var line in text.Split('\n'))
        {
            events.AddRange(parser.Feed(line));
        }
        events.AddRange(parser.Complete());
        return events;
    }

    [Fact]
    public void Mixed_tree_reports_every_file_with_exact_unicode_names()
    {
        var files = ParseFixture("unilog-mt32-mixed.utf16").OfType<FileReported>().ToList();

        // 5 named files + locked.bin + 300 small + 3 large.
        Assert.Equal(309, files.Count);
        Assert.Contains(new FileReported(4096, @"C:\m0\cap-src\emoji 📁 file.bin"), files);
        Assert.Contains(new FileReported(4096, @"C:\m0\cap-src\日本語.txt"), files);
        Assert.Contains(new FileReported(4096, @"C:\m0\cap-src\ünïcødé.txt"), files);
        Assert.Equal(3, files.Count(f => f.Size == 536_870_912));
        Assert.Equal(3L * 536_870_912 + 6 * 4096 + 300 * 1024, files.Sum(f => f.Size));
    }

    [Fact]
    public void Mixed_tree_has_no_errors_or_unrecognized_lines()
    {
        var events = ParseFixture("unilog-mt32-mixed.utf16");
        Assert.Empty(events.OfType<ErrorReported>());
        Assert.Empty(events.OfType<OtherOutput>());
    }

    [Fact]
    public void Locked_file_is_an_error_and_is_not_counted_as_copied()
    {
        var events = ParseFixture("unilog-mt32-locked.utf16");

        var error = Assert.Single(events.OfType<ErrorReported>());
        Assert.Equal(32, error.Code);
        Assert.Equal("Copying File", error.Operation);
        Assert.Equal(@"C:\m0\cap-src\locked.bin", error.Path);
        Assert.Equal("The process cannot access the file because it is being used by another process.", error.Message);

        var copied = Assert.Single(events.OfType<FileReported>());
        Assert.Equal(@"C:\m0\cap-src\plain.txt", copied.Path);

        Assert.Equal("ERROR: RETRY LIMIT EXCEEDED.", Assert.Single(events.OfType<OtherOutput>()).Text);
    }
}
