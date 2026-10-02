using System.Text;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class Utf16LinesTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "unilog-mt32-mixed.utf16",
        "unilog-mt32-locked.utf16",
    };

    private static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The reference: decode everything at once, split on '\n', drop one trailing '\r'.</summary>
    private static List<string> Unsplit(byte[] bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var parts = text.Split('\n').Select(p => p.EndsWith('\r') ? p[..^1] : p).ToList();
        if (parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static List<string> Run(byte[] bytes, int chunkSize)
    {
        var splitter = new Utf16Lines();
        var lines = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, bytes.Length - offset);
            lines.AddRange(splitter.Feed(bytes.AsSpan(offset, size)));
        }

        if (splitter.Flush() is { } last)
        {
            lines.Add(last);
        }

        return lines;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_split_into_two_feeds_at_every_offset_matches_unsplit_decode(string fixture)
    {
        var bytes = Load(fixture);
        var expected = Unsplit(bytes);
        Assert.NotEmpty(expected);

        for (var offset = 1; offset <= 64; offset++)
        {
            var splitter = new Utf16Lines();
            var lines = new List<string>();
            lines.AddRange(splitter.Feed(bytes.AsSpan(0, offset)));
            lines.AddRange(splitter.Feed(bytes.AsSpan(offset)));
            if (splitter.Flush() is { } last)
            {
                lines.Add(last);
            }

            Assert.True(expected.SequenceEqual(lines), $"split at byte offset {offset}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_fed_in_fixed_chunks_of_every_size_matches_unsplit_decode(string fixture)
    {
        var bytes = Load(fixture);
        var expected = Unsplit(bytes);

        for (var size = 1; size <= 64; size++)
        {
            Assert.True(expected.SequenceEqual(Run(bytes, size)), $"chunk size {size}");
        }
    }

    [Fact]
    public void Surrogate_pair_split_across_feeds_is_decoded_whole()
    {
        var bytes = Encoding.Unicode.GetBytes("a 📁 b\n");
        // The pair occupies bytes 4..7 (after "a " as two code units); cut inside and around it.
        for (var cut = 3; cut <= 8; cut++)
        {
            var splitter = new Utf16Lines();
            var lines = new List<string>();
            lines.AddRange(splitter.Feed(bytes.AsSpan(0, cut)));
            lines.AddRange(splitter.Feed(bytes.AsSpan(cut)));
            Assert.Equal(["a 📁 b"], lines);
        }
    }

    [Fact]
    public void Crlf_and_lf_give_the_same_lines_and_only_one_trailing_cr_is_removed()
    {
        Assert.Equal(["one", "two"], Run(Encoding.Unicode.GetBytes("one\r\ntwo\r\n"), 7));
        Assert.Equal(["one", "two"], Run(Encoding.Unicode.GetBytes("one\ntwo\n"), 7));
        Assert.Equal(["one\r", "two"], Run(Encoding.Unicode.GetBytes("one\r\r\ntwo\n"), 7));
    }

    [Fact]
    public void Lines_are_not_trimmed_and_the_bom_is_left_for_the_parser()
    {
        var bytes = Encoding.Unicode.GetBytes("﻿\t  100\tC:\\a b \n");
        Assert.Equal(["﻿\t  100\tC:\\a b "], Run(bytes, 5));
    }

    [Fact]
    public void Blank_lines_are_kept()
    {
        Assert.Equal(["a", "", "b"], Run(Encoding.Unicode.GetBytes("a\n\nb\n"), 3));
    }

    [Fact]
    public void Line_over_the_cap_is_cut_and_the_rest_discarded_until_the_next_newline()
    {
        var overlong = new string('x', Utf16Lines.MaxLineChars + 500);
        var bytes = Encoding.Unicode.GetBytes(overlong + "\nshort\n" + new string('y', Utf16Lines.MaxLineChars) + "\n");

        foreach (var chunk in new[] { 4096, 7, 1_000_000 })
        {
            var lines = Run(bytes, chunk);
            Assert.Equal(3, lines.Count);
            Assert.Equal(Utf16Lines.MaxLineChars, lines[0].Length);
            Assert.All(lines[0], c => Assert.Equal('x', c));
            Assert.Equal("short", lines[1]);
            Assert.Equal(Utf16Lines.MaxLineChars, lines[2].Length);
        }
    }

    [Fact]
    public void Endless_line_without_a_newline_stays_bounded_and_flushes_the_cut_line()
    {
        var splitter = new Utf16Lines();
        var chunk = Encoding.Unicode.GetBytes(new string('z', 10_000));
        for (var i = 0; i < 100; i++)
        {
            Assert.Empty(splitter.Feed(chunk));
        }

        var last = splitter.Flush();
        Assert.NotNull(last);
        Assert.Equal(Utf16Lines.MaxLineChars, last.Length);
    }

    [Fact]
    public void Flush_returns_the_unterminated_final_line_once_and_null_when_nothing_is_pending()
    {
        var splitter = new Utf16Lines();
        Assert.Equal(["done"], splitter.Feed(Encoding.Unicode.GetBytes("done\npartial")));
        Assert.Equal("partial", splitter.Flush());
        Assert.Null(splitter.Flush());

        var terminated = new Utf16Lines();
        Assert.Single(terminated.Feed(Encoding.Unicode.GetBytes("whole\n")));
        Assert.Null(terminated.Flush());
    }
}
