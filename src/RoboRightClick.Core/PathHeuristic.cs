using System.Text;

namespace RoboRightClick.Core;

/// <summary>
/// One heuristic for "this free text may carry a path or a file name", shared by
/// <see cref="FailureText"/> (which drops such text) and <see cref="CrashLog"/> (which
/// replaces the path-looking parts). Signs of a path: a separator, a drive designator,
/// quotes around a name (how .NET and Windows quote names), shell metacharacters, control
/// characters. It is a heuristic: a bare file name with no quotes or separators passes.
/// Losing detail is always the safe direction.
/// </summary>
internal static class PathHeuristic
{
    /// <summary>Longest free text <see cref="IsPathFree"/> passes; longer text is more likely to embed a name.</summary>
    public const int MaxPassedThroughLength = 300;

    /// <summary>What <see cref="Scrub"/> puts in place of each path-looking part.</summary>
    public const string Placeholder = "[path]";

    /// <summary>
    /// False for anything that could carry a path or a file name: separators, a drive
    /// designator, double quotes, a pair of single quotes, control characters, or text
    /// longer than <see cref="MaxPassedThroughLength"/>.
    /// </summary>
    public static bool IsPathFree(string text)
    {
        if (text.Length > MaxPassedThroughLength)
        {
            return false;
        }
        var singleQuotes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsPathCharacter(c) || IsDriveColon(text, i))
            {
                return false;
            }
            if (c is '\'' or '‘' or '’')
            {
                singleQuotes++;
            }
        }
        return singleQuotes < 2;
    }

    /// <summary>
    /// The text with every path-looking part replaced by <see cref="Placeholder"/>, on one
    /// line and at most <paramref name="maxLength"/> characters. First each quoted span
    /// (quotes included) goes, because .NET quotes the names in its messages and a quoted name
    /// may contain spaces. Then, line by line, the first whitespace-separated word that
    /// <see cref="IsPathFree"/> would refuse for its characters (a separator, a drive
    /// designator, a stray double quote, a shell metacharacter) and everything after it on
    /// that line: an unquoted path may contain spaces ("C:\Tax Returns 2025\a.pdf"), and
    /// nothing but a line break marks where it ends. The lines are joined with spaces.
    /// </summary>
    public static string Scrub(string text, int maxLength)
    {
        var unquoted = ReplaceQuotedSpans(text);
        var result = new StringBuilder(Math.Min(unquoted.Length, maxLength) + 16);
        foreach (var line in unquoted.Split(LineBreak))
        {
            foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (result.Length > 0)
                {
                    result.Append(' ');
                }
                if (LooksLikePath(word))
                {
                    result.Append(Placeholder);
                    break;
                }
                result.Append(word);
            }
        }
        if (result.Length > maxLength)
        {
            result.Length = Math.Max(0, maxLength - 1);
            result.Append('…');
        }
        return result.ToString();
    }

    /// <summary>
    /// What <see cref="ReplaceQuotedSpans"/> puts in place of a C0 control character. Win32
    /// file names cannot contain U+0001 to U+001F, so one ends any path; the C1 controls
    /// (U+007F to U+009F) are legal in NTFS names and become spaces instead, which keeps a
    /// path that contains one on its line.
    /// </summary>
    private const char LineBreak = '\n';

    private static bool IsPathCharacter(char c) =>
        c is '\\' or '/' or '"' or '“' or '”' or '<' or '>' or '|' || char.IsControl(c);

    /// <summary>A letter followed by ':' that is not the end of a longer word ("C:", but not "Error:").</summary>
    private static bool IsDriveColon(string text, int i) =>
        text[i] == ':' && i > 0 && char.IsAsciiLetter(text[i - 1]) && (i == 1 || !char.IsLetterOrDigit(text[i - 2]));

    private static bool LooksLikePath(string word)
    {
        for (var i = 0; i < word.Length; i++)
        {
            if (IsPathCharacter(word[i]) || IsDriveColon(word, i))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces each quoted span with the placeholder. An opening quote starts a word (so the
    /// apostrophe in "don't" is not one); its closing quote ends one (so "Bob's" inside a
    /// quoted name does not close it). A quote that never closes takes the rest of the text.
    /// C0 control characters become <see cref="LineBreak"/>, other control characters spaces,
    /// so no control character reaches the log and the lines and words split cleanly afterwards.
    /// </summary>
    private static string ReplaceQuotedSpans(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (ClosingQuotesFor(c) is { } closers && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var end = FindClosing(text, i + 1, closers);
                result.Append(Placeholder);
                if (end < 0)
                {
                    break;
                }
                i = end + 1;
                continue;
            }
            result.Append(c < ' ' ? LineBreak : char.IsControl(c) ? ' ' : c);
            i++;
        }
        return result.ToString();
    }

    private static string? ClosingQuotesFor(char opening) => opening switch
    {
        '"' or '“' or '”' => "\"”“",
        '\'' or '‘' or '’' => "'’‘",
        _ => null,
    };

    private static int FindClosing(string text, int from, string closers)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (closers.Contains(text[j]) && (j + 1 == text.Length || !char.IsLetterOrDigit(text[j + 1])))
            {
                return j;
            }
        }
        return -1;
    }
}
