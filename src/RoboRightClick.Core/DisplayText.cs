using System.Globalization;

namespace RoboRightClick.Core;

/// <summary>
/// Number and name formatting shared by the tray tooltip, the Jobs window and toasts.
/// Sizes follow Explorer (StrFormatByteSize): 1024-based units shown with three
/// significant digits ("1.50 KB"), truncated rather than rounded so a file never
/// looks bigger than it is.
/// </summary>
public static class DisplayText
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        }
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1000 && unit < Units.Length - 1);

        var shown = value switch
        {
            >= 100 => Math.Floor(value).ToString("0", CultureInfo.InvariantCulture),
            >= 10 => (Math.Floor(value * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture),
            _ => (Math.Floor(value * 100) / 100).ToString("0.00", CultureInfo.InvariantCulture),
        };
        return shown + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>Compact remaining time: "40 s", "4 min", "1 h 20 min". Rounded up so it never promises too early.</summary>
    public static string Duration(TimeSpan span)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, span.TotalSeconds));
        if (seconds < 60)
        {
            return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
        var minutes = (seconds + 59) / 60;
        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0
            ? hours.ToString(CultureInfo.InvariantCulture) + " h"
            : string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min");
    }

    /// <summary>"report.txt", or "report.txt and 3 more". Never used where ephemeral mode forbids names.</summary>
    public static string SourcesSummary(IReadOnlyList<string> sources) => sources.Count switch
    {
        0 => string.Empty,
        1 => NameOf(sources[0]),
        _ => string.Create(CultureInfo.InvariantCulture, $"{NameOf(sources[0])} and {sources.Count - 1} more"),
    };

    public static string Items(long count) =>
        count == 1 ? "1 item" : count.ToString("N0", CultureInfo.InvariantCulture) + " items";

    private static string NameOf(string path)
    {
        var name = WinPath.GetFileName(path);
        return name.Length > 0 ? name : WinPath.TrimTrailingSeparators(path);
    }
}
