using System.Text;

namespace RoboRightClick.Core;

/// <summary>
/// Incremental UTF-16LE line splitter for robocopy's /UNILOG output. Bytes arrive in
/// arbitrary chunks: a read can end in the middle of a code unit or a surrogate pair, so one
/// <see cref="Decoder"/> lives for the whole stream and carries that state between feeds.
/// Not thread-safe; one instance per stream.
/// </summary>
/// <remarks>
/// Lines split on '\n' and lose one trailing '\r'. They are otherwise untouched: the parser
/// depends on leading tabs, and any byte-order mark is for the parser to ignore. A line
/// longer than <see cref="MaxLineChars"/> is cut there and the rest is discarded up to the
/// next newline, so a peer that never sends '\n' cannot grow memory without bound.
/// </remarks>
public sealed class Utf16Lines
{
    /// <summary>Robocopy's longest real line is a 32K-character path plus a few fields.</summary>
    public const int MaxLineChars = 64 * 1024;

    private readonly Decoder _decoder = Encoding.Unicode.GetDecoder();
    private readonly StringBuilder _line = new();
    private char[] _chars = new char[1024];
    private bool _discarding;

    /// <summary>Decodes <paramref name="bytes"/> and returns the lines it completes (possibly none).</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        var needed = Encoding.Unicode.GetMaxCharCount(bytes.Length);
        if (_chars.Length < needed)
        {
            _chars = new char[needed];
        }

        var count = _decoder.GetChars(bytes, _chars, flush: false);
        var rest = _chars.AsSpan(0, count);
        while (rest.Length > 0)
        {
            var newline = rest.IndexOf('\n');
            var piece = newline < 0 ? rest : rest[..newline];
            Append(piece);
            if (newline < 0)
            {
                break;
            }

            lines.Add(TakeLine());
            rest = rest[(newline + 1)..];
        }

        return lines;
    }

    /// <summary>The final line when the stream ended without a newline; null when nothing is pending.</summary>
    public string? Flush()
    {
        // A dangling odd byte or lone high surrogate becomes U+FFFD here rather than vanishing.
        var count = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars, flush: true);
        Append(_chars.AsSpan(0, count));

        if (_line.Length == 0 && !_discarding)
        {
            return null;
        }

        return TakeLine();
    }

    private void Append(ReadOnlySpan<char> piece)
    {
        if (_discarding || piece.Length == 0)
        {
            return;
        }

        var room = MaxLineChars - _line.Length;
        if (piece.Length > room)
        {
            _line.Append(piece[..room]);
            _discarding = true;
            return;
        }

        _line.Append(piece);
    }

    private string TakeLine()
    {
        if (!_discarding && _line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        var text = _line.ToString();
        _line.Clear();
        _discarding = false;
        return text;
    }
}
