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

    /// <summary>The offer a double-clicked exe shows (<see cref="Offer"/>).</summary>
    /// <param name="Action">The button that does the work; null when nothing may be changed.</param>
    /// <param name="CanOpen">Whether "Open" (start the installed app) is offered.</param>
    public sealed record InstallOffer(string Heading, string Body, string? Action, bool CanOpen);

    public static InstallOffer Offer(InstallDecision decision) => decision switch
    {
        InstallDecision.FreshInstall fresh => new(
            $"Install {AppInfo.Name} {fresh.To}?",
            "Installing adds Robo-Copy, Robo-Cut and Robo-Paste to the right-click menu, under Show more options. "
                + "It is installed for your user account only and needs no administrator rights.",
            "Install", CanOpen: false),
        InstallDecision.Update { IsDowngrade: false, From: { } from } update => new(
            $"Update {AppInfo.Name} {from} \u2192 {update.To}?",
            "The running copy is stopped and replaced; your settings and job history are kept. "
                + "It will not stop while copies are in progress.",
            "Update", CanOpen: true),
        InstallDecision.Update { IsDowngrade: false } update => new(
            $"Update {AppInfo.Name} to {update.To}?",
            "The installed version could not be read, so it is treated as older and replaced. "
                + "The running copy is stopped first; your settings and job history are kept.",
            "Update", CanOpen: true),
        InstallDecision.Update update => new(
            $"Replace {AppInfo.Name} {update.From} with the older {update.To}?",
            "This installs an older version over a newer one.",
            "Downgrade", CanOpen: true),
        InstallDecision.Repair repair => new(
            $"{AppInfo.Name} {repair.Version} is installed. Repair it?",
            "Repair puts back the program file, the menu icons and the registry entries. Your settings and job history are kept.",
            "Repair", CanOpen: true),
        InstallDecision.RefuseDowngrade refuse => new(
            $"A newer {AppInfo.Name} is installed.",
            $"Version {refuse.Installed} is installed and this is {refuse.This}. Nothing was changed. "
                + "To go back to the older version on purpose, run it from a terminal with --install --force.",
            null, CanOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };

    /// <summary>Why --install changed nothing on a newer install (exit code 1).</summary>
    public static string DowngradeRefused(InstallDecision.RefuseDowngrade refuse) =>
        $"{AppInfo.Name} {refuse.Installed} is installed, which is newer than this {refuse.This}. "
            + "Nothing was changed. Run --install --force to replace it with this older version.";

    /// <summary>The heading of the result message after a successful install.</summary>
    public static string Done(InstallDecision decision) => decision switch
    {
        InstallDecision.Update { From: { } from, IsDowngrade: false } u => $"{AppInfo.Name} was updated from {from} to {u.To}.",
        InstallDecision.Update { From: { } from } u => $"{AppInfo.Name} was changed from {from} back to {u.To}.",
        InstallDecision.Update u => $"{AppInfo.Name} was updated to {u.To}.",
        InstallDecision.Repair r => $"{AppInfo.Name} {r.Version} was repaired.",
        _ => $"{AppInfo.Name} is installed.",
    };
}
