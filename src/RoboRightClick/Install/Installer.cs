using RoboRightClick.Core;

namespace RoboRightClick.Install;

/// <summary>
/// Per-user install and uninstall. No admin rights, no Explorer settings, and no Explorer
/// restart: classic static verbs are read from the registry on each right-click.
/// </summary>
internal static class Installer
{
    /// <summary>
    /// 1. If a tray is running, SingleInstance.RequestExitAndWait (fails with a message if
    /// it will not exit because jobs are active). 2. Copy the running exe to
    /// AppPaths.InstalledExe unless it already runs from there. 3. RegistryWriter.Write(
    /// Registration.InstallValues(installedExe, startWithWindows)). 4. Write the default
    /// config.json only if none exists, so a reinstall keeps the user's settings.
    /// 5. Start the installed exe (tray). Returns a CliExitCodes value; messages go to the
    /// console when attached, otherwise a message box.
    /// </summary>
    public static int Install(CliInstall command, AppPaths paths) => throw new NotImplementedException();

    /// <summary>
    /// 1. Stop the running tray as in install (refuse while jobs run). 2.
    /// RegistryWriter.Remove(Registration.UninstallRemovals()). 3. Delete config.json (and
    /// its folder if empty) and the data folder with job logs and history, so no paths
    /// are left behind. 4. Delete the install folder; when running from it, hand that to a
    /// short-lived "cmd /c" that waits for this process to exit. Removes nothing outside
    /// <see cref="AppPaths"/> and the registry list.
    /// </summary>
    public static int Uninstall(AppPaths paths) => throw new NotImplementedException();
}
