namespace RoboRightClick.Core;

/// <summary>
/// The Settings window's note about problems found when config.json was loaded. Every field
/// falls back to its default except "pasteHotkey", which falls back to off
/// (<see cref="SettingsSerializer.PasteHotkeyOffSuffix"/>), so the hotkey's problems get
/// their own sentence: one lead-in for both would tell the user the hotkey uses its default
/// (on) and is off in the same breath.
/// </summary>
public static class SettingsLoadNotice
{
    /// <summary>The note, or "" when there is nothing to say.</summary>
    public static string Compose(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0)
        {
            return string.Empty;
        }
        var hotkey = problems.Where(SettingsSerializer.IsPasteHotkeyProblem).Select(WithoutOffSuffix).ToList();
        var others = problems.Where(p => !SettingsSerializer.IsPasteHotkeyProblem(p)).ToList();

        var sentences = new List<string>();
        if (others.Count > 0)
        {
            sentences.Add("config.json has problems, so these settings are using their defaults: " + string.Join("; ", others) + ".");
        }
        if (hotkey.Count > 0)
        {
            sentences.Add((others.Count > 0 ? "The hotkey is off: " : "config.json has a problem, so the hotkey is off: ")
                + string.Join("; ", hotkey) + ".");
        }
        sentences.Add("Save writes the values shown here and fixes the file.");
        return string.Join(" ", sentences);
    }

    private static string WithoutOffSuffix(string problem) =>
        problem.EndsWith(SettingsSerializer.PasteHotkeyOffSuffix, StringComparison.Ordinal)
            ? problem[..^SettingsSerializer.PasteHotkeyOffSuffix.Length]
            : problem;
}
