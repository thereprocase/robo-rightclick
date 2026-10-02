using System.Globalization;

namespace RoboRightClick.Core;

/// <summary>One row of the "Let me decide" list as the user left it.</summary>
public sealed record ConflictRow(FileConflict Conflict, bool SourceChecked, bool DestinationChecked);

/// <summary>
/// Turns the ticks in Explorer's per-file conflict list into decisions. Explorer's rule:
/// tick the source to take it, the destination to keep it, both to keep both (the copy gets
/// a new name), neither to skip.
/// </summary>
public static class ConflictSelection
{
    /// <summary>Shown on a row where keep-both cannot be offered (<see cref="FileConflict.KeepBothAllowed"/>).</summary>
    public const string KeepBothUnavailableReason = "Keeping both isn't available when moving between drives";

    /// <summary>
    /// Both ticked keeps both where that is allowed. Where it is not, the dialog makes the two
    /// boxes exclusive so the case cannot arise; if it does anyway, the answer is Skip, never
    /// Replace: the user ticked the destination file, so overwriting it would destroy the one
    /// thing they asked to keep, while skipping leaves the source where it was (section 11:
    /// when in doubt, keep data).
    /// </summary>
    public static FileDecision Decision(bool sourceChecked, bool destinationChecked, bool keepBothAllowed) =>
        (sourceChecked, destinationChecked) switch
        {
            (true, true) => keepBothAllowed ? FileDecision.KeepBoth : FileDecision.Skip,
            (true, false) => FileDecision.Replace,
            _ => FileDecision.Skip,
        };

    public static FileDecision Decision(ConflictRow row) =>
        Decision(row.SourceChecked, row.DestinationChecked, row.Conflict.KeepBothAllowed);

    /// <summary>
    /// The dialog's answer, keyed by destination path (case-insensitive, as Windows compares
    /// names). If two rows name the same destination, the less destructive decision wins:
    /// Skip, then KeepBoth, then Replace.
    /// </summary>
    public static ConflictChoice.DecideEach Build(IEnumerable<ConflictRow> rows)
    {
        var byDestination = new Dictionary<string, FileDecision>(WinPath.Comparer);
        foreach (var row in rows)
        {
            var decision = Decision(row);
            var key = row.Conflict.DestinationPath;
            byDestination[key] = byDestination.TryGetValue(key, out var earlier)
                ? LeastDestructive(earlier, decision)
                : decision;
        }
        return new ConflictChoice.DecideEach(byDestination);
    }

    /// <summary>What Continue will do, for the line above the button: "2 replaced, 1 kept both, 3 skipped".</summary>
    public static string Summary(IEnumerable<FileDecision> decisions)
    {
        int replace = 0, keepBoth = 0, skip = 0;
        foreach (var decision in decisions)
        {
            switch (decision)
            {
                case FileDecision.Replace:
                    replace++;
                    break;
                case FileDecision.KeepBoth:
                    keepBoth++;
                    break;
                default:
                    skip++;
                    break;
            }
        }
        var parts = new List<string>();
        if (replace > 0)
        {
            parts.Add(Count(replace) + " replaced");
        }
        if (keepBoth > 0)
        {
            parts.Add(Count(keepBoth) + " kept both");
        }
        if (skip > 0)
        {
            parts.Add(Count(skip) + " skipped");
        }
        return parts.Count == 0 ? "No files" : string.Join(", ", parts);
    }

    private static string Count(int n) => n == 1 ? "1 file" : n.ToString("N0", CultureInfo.InvariantCulture) + " files";

    private static FileDecision LeastDestructive(FileDecision a, FileDecision b) =>
        a == FileDecision.Skip || b == FileDecision.Skip ? FileDecision.Skip
        : a == FileDecision.KeepBoth || b == FileDecision.KeepBoth ? FileDecision.KeepBoth
        : FileDecision.Replace;
}
