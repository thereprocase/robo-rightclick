namespace RoboRightClick.Core;

/// <summary>
/// The one gate every path from outside the app passes before it reaches planning:
/// clipboard CF_HDROP entries (any process can write them), Explorer's selection and CLI
/// arguments. Robocopy receives these paths on a command line, so anything that could be
/// read as a switch, a wildcard, a device or a stream is refused here rather than escaped
/// later.
/// </summary>
/// <remarks>
/// Accepted: a drive path ("C:\..." with an ASCII drive letter) or a UNC path
/// ("\\server\share\..."). Refused: relative paths, device and long-path prefixes
/// ("\\?\", "\\.\", "\??\"), "." and ".." segments, empty segments, alternate data streams
/// (':' after the drive colon), the characters "&lt;&gt;|*?" plus '"' and '/', control
/// characters, reserved device names in any segment, segments ending in '.' or ' ', and
/// paths longer than <see cref="MaxPathLength"/>. Explorer never produces any of these for
/// a real file-system item, so refusing them costs no legitimate use.
/// </remarks>
public static class PathPolicy
{
    /// <summary>The Win32 extended-length limit, in UTF-16 code units.</summary>
    public const int MaxPathLength = 32_767;

    public const string UnsupportedPathReason = "This item's path is not a plain drive or network path.";

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static bool IsAcceptable(string? path) => Problem(path) is null;

    /// <summary>Why <paramref name="path"/> is refused, or null when it is acceptable. The text never repeats the path.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "empty path";
        }
        if (path.Length > MaxPathLength)
        {
            return "path is longer than 32,767 characters";
        }
        foreach (var c in path)
        {
            if (c < 0x20 || c is '"' or '<' or '>' or '|' or '*' or '?' or '/')
            {
                return "path contains a character that is not allowed";
            }
        }

        string rest;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            rest = path[3..];
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // "\\?\" and "\\.\" never get here: '?' is refused above, and a "." server
            // segment is refused below.
            var parts = path[2..].Split('\\');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return "network path needs a server and a share";
            }
            if (parts[0] is "." or "..")
            {
                return "device paths are not allowed";
            }
            if (SegmentProblem(parts[0]) is { } serverProblem)
            {
                return serverProblem;
            }
            rest = string.Join('\\', parts[1..]);
        }
        else
        {
            return "path must start with a drive letter or \\\\server\\share";
        }

        if (rest.Length == 0)
        {
            return null;
        }
        var segments = rest.Split('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            // One trailing separator ("D:\dst\") is how roots and some shell paths look.
            if (segments[i].Length == 0 && i == segments.Length - 1)
            {
                continue;
            }
            if (SegmentProblem(segments[i]) is { } problem)
            {
                return problem;
            }
        }
        return null;
    }

    private static string? SegmentProblem(string segment)
    {
        if (segment.Length == 0)
        {
            return "path contains an empty segment";
        }
        if (segment is "." or "..")
        {
            return "path contains '.' or '..'";
        }
        if (segment.Contains(':'))
        {
            return "path names an alternate data stream";
        }
        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return "a path segment ends with '.' or a space";
        }
        // Windows treats "NUL", "nul.txt" and "NUL .txt" alike: the stem before the first
        // dot, with trailing spaces removed, is what names the device.
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return "path contains a reserved device name";
        }
        return null;
    }
}
