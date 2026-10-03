namespace RoboRightClick.Core;

/// <summary>
/// A semantic version (semver.org precedence) for deciding whether an install is older,
/// the same or newer than the running exe: 1.0.0-beta.1 &lt; 1.0.0-beta.2 &lt; 1.0.0-rc.1 &lt; 1.0.0.
/// Build metadata ("+sha") is dropped and never affects the order. A fourth numeric part is
/// accepted because Windows file versions have one. Numbers are kept as digit strings and
/// compared by length, so a hostile version string cannot overflow anything.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    // A version string comes from a file on disk the user may not control: bound the work.
    private const int MaxLength = 128;

    private readonly string[] _numbers;
    private readonly string[] _preRelease;

    private AppVersion(string text, string[] numbers, string[] preRelease)
    {
        Text = text;
        _numbers = numbers;
        _preRelease = preRelease;
    }

    /// <summary>The version as shown to the user: trimmed, without build metadata.</summary>
    public string Text { get; }

    /// <summary>Null when <paramref name="text"/> is not a version (missing, empty, garbled, too long).</summary>
    public static AppVersion? TryParse(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
        {
            return null;
        }

        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            if (!AllIdentifiers(trimmed[(plus + 1)..], numericNoLeadingZero: false))
            {
                return null;
            }
            trimmed = trimmed[..plus];
        }

        string corePart = trimmed;
        string[] pre = [];
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            corePart = trimmed[..dash];
            var preText = trimmed[(dash + 1)..];
            if (!AllIdentifiers(preText, numericNoLeadingZero: true))
            {
                return null;
            }
            pre = preText.Split('.');
        }

        var numbers = corePart.Split('.');
        if (numbers.Length is not (3 or 4) || numbers.Any(n => !IsDigits(n)))
        {
            return null;
        }
        return new AppVersion(trimmed, [.. numbers.Select(StripLeadingZeros)], pre);
    }

    /// <summary>Negative when this is older than <paramref name="other"/>, 0 when the same precedence.</summary>
    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        for (var i = 0; i < 4; i++)
        {
            var c = CompareNumbers(Part(i), other.Part(i));
            if (c != 0)
            {
                return c;
            }
        }

        // A version with a pre-release label is older than the same version without one.
        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
        {
            return other._preRelease.Length.CompareTo(_preRelease.Length);
        }
        for (var i = 0; i < Math.Min(_preRelease.Length, other._preRelease.Length); i++)
        {
            var a = _preRelease[i];
            var b = other._preRelease[i];
            var aNumeric = IsDigits(a);
            var bNumeric = IsDigits(b);
            var c = aNumeric && bNumeric
                ? CompareNumbers(StripLeadingZeros(a), StripLeadingZeros(b))
                : aNumeric ? -1 : bNumeric ? 1 : string.CompareOrdinal(a, b);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }
        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    public override string ToString() => Text;

    private string Part(int index) => index < _numbers.Length ? _numbers[index] : "0";

    private static int CompareNumbers(string a, string b) =>
        a.Length != b.Length ? a.Length.CompareTo(b.Length) : Math.Sign(string.CompareOrdinal(a, b));

    private static bool AllIdentifiers(string text, bool numericNoLeadingZero)
    {
        if (text.Length == 0)
        {
            return false;
        }
        foreach (var id in text.Split('.'))
        {
            if (id.Length == 0 || !id.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-'))
            {
                return false;
            }
            if (numericNoLeadingZero && IsDigits(id) && id.Length > 1 && id[0] == '0')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    private static string StripLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
