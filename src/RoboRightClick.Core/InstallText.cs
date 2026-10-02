namespace RoboRightClick.Core;

/// <summary>The message a finished install shows (none with --quiet).</summary>
public static class InstallText
{
    /// <summary>
    /// What to do next. With the Robo-Paste hotkey on it names the hotkey: an upgrade from a
    /// version without it turns it on (a config.json without "pasteHotkey" means the default),
    /// and a keyboard hook must not arrive unannounced. The tray's first-run hint names it too,
    /// but only when no notice about interrupted pastes or a settings problem takes its place
    /// (<see cref="StartupRules.PickStartupToast"/>); this message always shows.
    /// </summary>
    /// <param name="pasteHotkey">The hotkey the installed config.json gives the tray; null when off.</param>
    public static string Installed(HotkeySpec? pasteHotkey) =>
        "Right-click files or folders and choose Show more options, then Robo-Copy or Robo-Cut. "
            + "Right-click the destination folder, or the empty space inside it, and choose Robo-Paste. "
            + (pasteHotkey is null
                ? string.Empty
                : $"In a File Explorer folder or on the desktop, {pasteHotkey.Format()} also runs Robo-Paste; Settings changes it or turns it off. ")
            + "The app's icon near the clock opens Jobs and Settings; Windows may put it under the ^ arrow.";
}
