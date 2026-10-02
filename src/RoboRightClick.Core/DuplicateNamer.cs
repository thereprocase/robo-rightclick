namespace RoboRightClick.Core;

/// <summary>
/// Explorer's naming rules for copies that would otherwise collide.
/// </summary>
public static class DuplicateNamer
{
    /// <summary>
    /// Name for pasting an item into the folder it came from:
    /// "report.txt" → "report - Copy.txt" → "report - Copy (2).txt".
    /// </summary>
    public static string CopyName(string name, bool isDirectory, Func<string, bool> isTaken)
    {
        var (stem, extension) = isDirectory ? (name, string.Empty) : WinPath.SplitExtension(name);
        var candidate = $"{stem} - Copy{extension}";
        for (var n = 2; isTaken(candidate); n++)
        {
            candidate = $"{stem} - Copy ({n}){extension}";
        }
        return candidate;
    }

    /// <summary>
    /// Name for Explorer's "Keep both files" conflict choice:
    /// "report.txt" → "report (2).txt" → "report (3).txt".
    /// </summary>
    public static string KeepBothName(string name, Func<string, bool> isTaken)
    {
        var (stem, extension) = WinPath.SplitExtension(name);
        string candidate;
        var n = 2;
        do
        {
            candidate = $"{stem} ({n}){extension}";
            n++;
        }
        while (isTaken(candidate));
        return candidate;
    }
}
