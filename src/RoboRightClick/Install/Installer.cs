using RoboRightClick.Core;

namespace RoboRightClick.Install;

/// <summary>
/// Per-user install and uninstall. No admin rights, no Explorer settings, and no Explorer
/// restart: classic static verbs are read from the registry on each right-click. Results
/// are shown in a message box (these are one-time user actions, and a GUI-subsystem exe's
/// console output arrives after the prompt has returned); exit codes are
/// <see cref="CliExitCodes"/>. A per-user install gives no protection against other
/// processes of the same user replacing the installed exe; that is true of every per-user
/// app and is stated in docs/design.md.
/// </summary>
internal static class Installer
{
    /// <summary>
    /// 1. If a tray is running, SingleInstance.RequestExitAndWait (fails with a message if
    /// it will not exit because jobs are active). 2. Copy the running exe to
    /// AppPaths.InstalledExe unless it already runs from there (copy to a temp name in the
    /// same folder, then replace). 3. Read an existing config.json, if any. 4.
    /// RegistryWriter.Write(Registration.InstallValues(new InstallTarget(installedExe,
    /// Registration.ResolveStartWithWindows(command.StartWithWindows, existing), userSid,
    /// version))). 5. Write the default config.json only if none exists, or write back the
    /// existing one with an explicit --autostart/--no-autostart applied. 6. Start the
    /// installed exe (tray). 7. Message: "Installed. Right-click files → Show more options
    /// → Robo-Copy / Robo-Cut / Robo-Paste."
    /// </summary>
    public static int Install(CliInstall command, AppPaths paths) => throw new NotImplementedException();

    /// <summary>
    /// A plain start from outside the install folder (a double-clicked download): "RoboRightClick
    /// isn't installed for this user. [Install] [Cancel]". Install runs <see cref="Install"/>
    /// with no autostart override; Cancel exits 0.
    /// </summary>
    public static int OfferInstall(AppPaths paths) => throw new NotImplementedException();

    /// <summary>
    /// 1. Stop the running tray as in install (refuse while jobs run). 2.
    /// RegistryWriter.Remove(Registration.UninstallRemovals()). 3. Delete exactly the files
    /// <see cref="UninstallPlan"/> lists (config.json, config.json.bad, history.jsonl, each
    /// job folder's job.json and robocopy.log) and then remove those folders with
    /// RemoveDirectory, which fails on anything unexpected left inside: never a blind
    /// recursive delete. Every folder is checked first: its leaf name is the expected one
    /// and it is not a reparse point (a link is removed as a link, its target untouched).
    /// 4. The install folder: when running from it, start
    /// %SystemRoot%\System32\cmd.exe by absolute path with the command line from
    /// UninstallPlan.SelfDeleteCommand, which waits for this process to exit and deletes the
    /// exe and the folder. Removes nothing outside <see cref="AppPaths"/> and the registry list.
    /// </summary>
    public static int Uninstall(AppPaths paths) => throw new NotImplementedException();
}
