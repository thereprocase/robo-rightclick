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

    /// <summary>What Continue will do, for the line above the button: "2 files replaced, 1 file pasted under a new name, 3 files skipped".</summary>
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
            // Keep both keeps the existing file and gives the pasted one a new name ("x (2).txt").
            parts.Add(Count(keepBoth) + " pasted under a new name");
        }
        if (skip > 0)
        {
            parts.Add(Count(skip) + " skipped");
        }
        return parts.Count == 0 ? "No files" : string.Join(", ", parts);
    }

    /// <summary>
    /// The note on a row of the per-file list: how the two files compare, and why keep-both is
    /// not offered where it is not. The size and date columns stay plain values, so they fit.
    /// </summary>
    public static string Note(FileConflict conflict)
    {
        var parts = new List<string>();
        if (conflict.LooksIdentical)
        {
            parts.Add("Same size and date");
        }
        else
        {
            // -1: the existing file is newer (larger), 0: the same, 1: the pasted file is.
            var newer = conflict.Source.LastWriteUtc.CompareTo(conflict.Existing.LastWriteUtc);
            var larger = conflict.Source.Size.CompareTo(conflict.Existing.Size);
            // Short: the note column shares the row with eight others.
            static string Side(int sign) => sign > 0 ? "Pasted file" : "Existing file";
            parts.Add((Math.Sign(newer), Math.Sign(larger)) switch
            {
                (0, _) => $"{Side(larger)} is larger; same date",
                (_, 0) => $"{Side(newer)} is newer; same size",
                var (n, l) when n == l => $"{Side(newer)} is newer and larger",
                _ => $"{Side(newer)} is newer; {Side(larger).ToLowerInvariant()} is larger",
            });
        }
        if (!conflict.KeepBothAllowed)
        {
            parts.Add(KeepBothUnavailableReason);
        }
        return string.Join(". ", parts);
    }

    /// <summary>The conflict dialog's three choices and their notes, worded for one file or several.</summary>
    public static ConflictChoiceText ChoiceText(int count)
    {
        if (count == 1)
        {
            return new ConflictChoiceText(
                "Replace the file in the destination", "Overwrites it with the file being pasted",
                "Skip this file", "Leaves the file in the destination as it is",
                "Let me decide", "Compare the two files; tick both to keep both");
        }
        var files = count.ToString("N0", CultureInfo.InvariantCulture) + " files";
        return new ConflictChoiceText(
            "Replace the files in the destination", $"Overwrites the {files} with the ones being pasted",
            "Skip these files", $"Leaves the {files} in the destination as they are",
            "Let me decide for each file", "Tick the files to keep; tick both to keep both");
    }

    /// <summary>The pane title over the per-file list. Never contains a file name: titles are shown in capitals, which would misstate a name.</summary>
    public static string ListTitle(int count) =>
        count == 1 ? "1 file with the same name" : count.ToString("N0", CultureInfo.InvariantCulture) + " files with the same names";

    private static string Count(int n) => n == 1 ? "1 file" : n.ToString("N0", CultureInfo.InvariantCulture) + " files";

    private static FileDecision LeastDestructive(FileDecision a, FileDecision b) =>
        a == FileDecision.Skip || b == FileDecision.Skip ? FileDecision.Skip
        : a == FileDecision.KeepBoth || b == FileDecision.KeepBoth ? FileDecision.KeepBoth
        : FileDecision.Replace;
}

/// <summary>Button text and the line under it for each of the conflict dialog's three choices.</summary>
public sealed record ConflictChoiceText(
    string Replace, string ReplaceNote, string Skip, string SkipNote, string Decide, string DecideNote);
