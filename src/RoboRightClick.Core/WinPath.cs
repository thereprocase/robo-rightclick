namespace RoboRightClick.Core;

/// <summary>
/// Windows path operations implemented as plain string logic.
/// </summary>
/// <remarks>
/// Core is tested on Linux, where System.IO.Path treats '\' as an ordinary
/// character. Every path Core sees comes from the Windows shell, so it must
/// apply Windows rules regardless of the OS it runs on. Comparisons are
/// ordinal-ignore-case, matching NTFS's default case-insensitive lookup.
/// </remarks>
public static class WinPath
{
    public const char Separator = '\\';

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Removes trailing separators, except the one that makes a root a root ("C:\").</summary>
    public static string TrimTrailingSeparators(string path)
    {
        var root = GetRoot(path);
        var trimmed = path.Replace('/', Separator);
        while (trimmed.Length > root.Length && trimmed.EndsWith(Separator))
        {
            trimmed = trimmed[..^1];
        }
        return trimmed;
    }

    /// <summary>
    /// Returns the root of a drive path ("C:\") or UNC path ("\\server\share\"),
    /// or an empty string for relative paths.
    /// </summary>
    public static string GetRoot(string path)
    {
        var p = path.Replace('/', Separator);
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            return p.Length >= 3 && p[2] == Separator ? p[..3] : p[..2];
        }
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var serverEnd = p.IndexOf(Separator, 2);
            if (serverEnd < 0)
            {
                return p;
            }
            var shareEnd = p.IndexOf(Separator, serverEnd + 1);
            return shareEnd < 0 ? p + Separator : p[..(shareEnd + 1)];
        }
        return string.Empty;
    }

    public static bool IsRoot(string path) =>
        Comparer.Equals(TrimTrailingSeparators(path), GetRoot(path));

    public static string Combine(string directory, string name)
    {
        var dir = TrimTrailingSeparators(directory);
        return dir.EndsWith(Separator) ? dir + name : dir + Separator + name;
    }

    public static string GetFileName(string path)
    {
        var p = TrimTrailingSeparators(path);
        if (IsRoot(p))
        {
            return string.Empty;
        }
        var i = p.LastIndexOf(Separator);
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>Returns the containing directory, or an empty string for a root.</summary>
    public static string GetParent(string path)
    {
        var p = TrimTrailingSeparators(path);
        if (IsRoot(p))
        {
            return string.Empty;
        }
        var root = GetRoot(p);
        var i = p.LastIndexOf(Separator);
        if (i < root.Length)
        {
            return root;
        }
        return p[..i];
    }

    public static bool AreSame(string a, string b) =>
        Comparer.Equals(TrimTrailingSeparators(a), TrimTrailingSeparators(b));

    /// <summary>True when <paramref name="candidate"/> is strictly inside <paramref name="ancestor"/>.</summary>
    public static bool IsStrictlyUnder(string candidate, string ancestor)
    {
        var c = TrimTrailingSeparators(candidate);
        var a = TrimTrailingSeparators(ancestor);
        var prefix = a.EndsWith(Separator) ? a : a + Separator;
        return c.Length > prefix.Length && c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The form used to match a path robocopy printed (/FP) against a planned path. Robocopy
    /// may print the "C:\." that <see cref="RobocopyArgs.Quote"/> gives a root as
    /// "C:\.\name", and long paths with an extended-length prefix; both sides go through
    /// this before comparing with <see cref="Comparer"/>.
    /// </summary>
    public static string NormalizeForMatch(string path)
    {
        var p = path.Replace('/', Separator);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        while (p.Contains(@"\.\", StringComparison.Ordinal))
        {
            p = p.Replace(@"\.\", @"\", StringComparison.Ordinal);
        }
        if (p.EndsWith(@"\.", StringComparison.Ordinal))
        {
            p = p[..^1];
        }
        return TrimTrailingSeparators(p);
    }

    /// <summary>True when one path is the other or contains it: the two locations share files.</summary>
    public static bool Overlap(string a, string b) => AreSame(a, b) || IsStrictlyUnder(a, b) || IsStrictlyUnder(b, a);

    /// <summary>Splits "name.ext" into ("name", ".ext"); dotfiles like ".gitignore" have no extension.</summary>
    public static (string Stem, string Extension) SplitExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            return (fileName, string.Empty);
        }
        return (fileName[..dot], fileName[dot..]);
    }
}
