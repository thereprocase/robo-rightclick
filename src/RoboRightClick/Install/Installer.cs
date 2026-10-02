using System.Diagnostics;
using RoboRightClick.App;
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
    /// <summary>How long a running tray gets to exit before install or uninstall gives up.</summary>
    private static readonly TimeSpan TrayExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long replacing or deleting the installed exe is retried. The tray releases its
    /// mutex before its process has ended, and Windows refuses to replace or delete an exe
    /// whose image is still mapped by a running process.
    /// </summary>
    private static readonly TimeSpan ExeInUseTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ExeInUseRetryInterval = TimeSpan.FromMilliseconds(100);

    private const string BusyMessage =
        "RoboRightClick is still running and has jobs in progress. Finish or cancel them, then try again.";

    /// <summary>
    /// 1. If a tray is running, SingleInstance.RequestExitAndWait (fails with a message if
    /// it will not exit because jobs are active). 2. Copy the running exe to
    /// AppPaths.InstalledExe unless it already runs from there (copy to a temp name in the
    /// same folder, then replace). 3. Read an existing config.json, if any. 4.
    /// RegistryWriter.Write(Registration.InstallValues(new InstallTarget(installedExe,
    /// Registration.ResolveStartWithWindows(command.StartWithWindows, existing), userSid,
    /// version))). 5. Write the default config.json only if none exists, or write back the
    /// existing one with an explicit --autostart/--no-autostart applied. 6. Start the
    /// installed exe (tray) with <see cref="CommandLine.AfterInstallSwitch"/>, so it shows the
    /// first-run hint once. 7. Message: "Installed. Right-click files → Show more options
    /// → Robo-Copy / Robo-Cut / Robo-Paste." (none with --quiet; the exit code is the result).
    /// </summary>
    public static int Install(CliInstall command, AppPaths paths)
    {
        var quiet = command.Quiet;
        try
        {
            if (!SingleInstance.RequestExitAndWait(TrayExitTimeout))
            {
                return Fail(BusyMessage, quiet);
            }

            CopyExecutable(HostEnvironment.ExecutablePath, paths);

            var existing = ReadExistingConfig(paths, out var existingText, out var existingHadProblems);
            var startWithWindows = Registration.ResolveStartWithWindows(command.StartWithWindows, existing);

            RegistryWriter.Write(Registration.InstallValues(new InstallTarget(
                paths.InstalledExe, startWithWindows, HostEnvironment.UserSid, HostEnvironment.Version)));
            if (!startWithWindows)
            {
                // InstallValues only adds the Run value; a reinstall that resolves autostart
                // off must also remove the one an earlier install or Settings wrote.
                RegistryWriter.SetStartWithWindows(false, paths.InstalledExe);
            }

            WriteConfig(paths, command.StartWithWindows, startWithWindows, existing, existingText, existingHadProblems);

            StartTray(paths);
            return Succeed("Installed. Right-click files → Show more options → Robo-Copy / Robo-Cut / Robo-Paste.", quiet);
        }
        catch (Exception ex)
        {
            return Fail($"Install failed: {ex.Message}", quiet);
        }
    }

    /// <summary>
    /// A plain start from outside the install folder (a double-clicked download): "RoboRightClick
    /// isn't installed for this user. [Install] [Cancel]". Install runs <see cref="Install"/>
    /// with no autostart override; Cancel exits 0.
    /// </summary>
    public static int OfferInstall(AppPaths paths)
    {
        const string heading = "RoboRightClick isn't installed for this user.";
        const string text = "Installing adds Robo-Copy, Robo-Cut and Robo-Paste to the right-click menu. "
            + "It needs no administrator rights.";

        // TaskDialog throws unless visual styles are on. This runs before any window exists,
        // which is when enabling them is allowed; if they still are not available, a plain
        // message box asks the same question.
        if (!Application.UseVisualStyles)
        {
            Application.EnableVisualStyles();
        }
        if (!Application.UseVisualStyles)
        {
            var answer = MessageBox.Show(
                heading + "\n\n" + text, AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            return answer == DialogResult.OK
                ? Install(new CliInstall(StartWithWindows: null), paths)
                : CliExitCodes.Ok;
        }

        var install = new TaskDialogButton("Install");
        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = heading,
            Text = text,
            Buttons = { install, TaskDialogButton.Cancel },
            DefaultButton = install,
        };

        return TaskDialog.ShowDialog(page) == install
            ? Install(new CliInstall(StartWithWindows: null), paths)
            : CliExitCodes.Ok;
    }

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
    /// UninstallPlan.SelfDeleteArguments, which waits for this process to exit and deletes the
    /// exe and the folder. Removes nothing outside <see cref="AppPaths"/> and the registry list.
    /// With --quiet no message box is shown; the exit code is the result.
    /// </summary>
    public static int Uninstall(CliUninstall command, AppPaths paths)
    {
        var quiet = command.Quiet;
        try
        {
            var runningFromInstall = WinPath.AreSame(HostEnvironment.ExecutablePath, paths.InstalledExe);

            // Validated before the tray is stopped and before anything is removed: a path the
            // plan refuses leaves the installation exactly as it was, tray included.
            _ = UninstallPlan.For(paths, [], runningFromInstall);
            var selfDelete = runningFromInstall
                ? UninstallPlan.SelfDeleteArguments(paths.InstalledExe, paths.InstallDirectory)
                : null;

            if (!SingleInstance.RequestExitAndWait(TrayExitTimeout))
            {
                return Fail(BusyMessage, quiet);
            }

            // Job folders are listed once the tray has stopped, so none appears afterwards.
            var plan = UninstallPlan.For(paths, ListJobFolderNames(paths), runningFromInstall);

            RegistryWriter.Remove(Registration.UninstallRemovals());
            var leftBehind = RemovePlanned(plan, paths.InstalledExe);

            // The message comes before the cleanup process starts: it waits only about 30
            // seconds for this process to exit, and a message box can stay open longer.
            var message = leftBehind == 0
                ? "RoboRightClick was uninstalled."
                : $"RoboRightClick was uninstalled. {leftBehind} item(s) were left in place because they were not created by it or could not be removed.";
            var code = Succeed(message, quiet);

            if (selfDelete is not null)
            {
                StartSelfDelete(selfDelete);
            }
            return code;
        }
        catch (Exception ex)
        {
            return Fail($"Uninstall failed: {ex.Message}", quiet);
        }
    }

    private static void CopyExecutable(string source, AppPaths paths)
    {
        if (WinPath.AreSame(source, paths.InstalledExe))
        {
            return;
        }

        Directory.CreateDirectory(paths.InstallDirectory);

        // A temp name in the same folder, then a replace: a failed copy never leaves a
        // truncated exe at the path the registry points to.
        var temp = WinPath.Combine(paths.InstallDirectory, AppInfo.ExeName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, temp, overwrite: false);
            RetryWhileExeInUse(() => File.Move(temp, paths.InstalledExe, overwrite: true));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static Settings? ReadExistingConfig(AppPaths paths, out string? text, out bool hadProblems)
    {
        text = null;
        hadProblems = false;
        if (!File.Exists(paths.ConfigFile))
        {
            return null;
        }

        text = File.ReadAllText(paths.ConfigFile);
        var result = SettingsSerializer.Parse(text);
        hadProblems = result.Problems.Count > 0;
        return result.Settings;
    }

    private static void WriteConfig(
        AppPaths paths, bool? explicitChoice, bool startWithWindows,
        Settings? existing, string? existingText, bool existingHadProblems)
    {
        if (existing is null)
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
            WriteAtomically(paths.ConfigFile, SettingsSerializer.Serialize(Settings.Default with { StartWithWindows = startWithWindows }));
            return;
        }

        // A plain reinstall leaves the user's file, comments and all, exactly as it is.
        if (explicitChoice is null)
        {
            return;
        }

        if (existingHadProblems && existingText is not null)
        {
            // Rewriting normalizes the file and drops what could not be parsed; keep the original.
            File.WriteAllText(paths.ConfigFile + UninstallPlan.BackupSuffix, existingText);
        }
        WriteAtomically(paths.ConfigFile, SettingsSerializer.Serialize(existing with { StartWithWindows = startWithWindows }));
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void StartTray(AppPaths paths)
    {
        Process.Start(new ProcessStartInfo(paths.InstalledExe)
        {
            ArgumentList = { CommandLine.AfterInstallSwitch },
            UseShellExecute = false,
            WorkingDirectory = paths.InstallDirectory,
        })?.Dispose();
    }

    private static IReadOnlyList<string> ListJobFolderNames(AppPaths paths)
    {
        if (!Directory.Exists(paths.JobsDirectory))
        {
            return [];
        }
        try
        {
            // Names only; UninstallPlan keeps the ones shaped like job folders.
            return Directory.EnumerateDirectories(paths.JobsDirectory)
                .Select(d => Path.GetFileName(d))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes the planned files and removes the planned folders, never more. Returns how
    /// many planned items could not be removed (or were left because they sit behind a
    /// link); a missing item is not a failure.
    /// </summary>
    private static int RemovePlanned(UninstallPlan plan, string installedExe)
    {
        var failures = 0;

        // Links first: nothing inside a folder that is really a link to elsewhere is
        // touched, and the link itself is removed as a link.
        var links = plan.Directories.Where(IsReparsePoint).ToList();

        foreach (var file in plan.Files)
        {
            if (IsBehindLink(file, links))
            {
                failures++;
                continue;
            }
            try
            {
                if (WinPath.AreSame(file, installedExe))
                {
                    RetryWhileExeInUse(() => File.Delete(file));
                }
                else
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }

        foreach (var directory in plan.Directories)
        {
            if (links.Any(l => !WinPath.AreSame(l, directory) && WinPath.IsStrictlyUnder(directory, l)))
            {
                failures++;
                continue;
            }
            if (!Directory.Exists(directory))
            {
                continue;
            }
            if (!WinPath.Comparer.Equals(WinPath.GetFileName(directory), plan.ExpectedLeafNames[directory]))
            {
                failures++;
                continue;
            }
            try
            {
                // Non-recursive: removes an empty folder or a link, and fails on content.
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }
        return failures;
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell: treat as a link, so nothing inside it is deleted.
            return true;
        }
    }

    /// <summary>Runs an operation on the installed exe, retrying while a just-exited tray still maps it.</summary>
    private static void RetryWhileExeInUse(Action operation)
    {
        var deadline = DateTime.UtcNow + ExeInUseTimeout;
        while (true)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (
                ex is (IOException or UnauthorizedAccessException)
                    and not (FileNotFoundException or DirectoryNotFoundException)
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(ExeInUseRetryInterval);
            }
        }
    }

    private static bool IsBehindLink(string file, IReadOnlyList<string> links) =>
        links.Any(l => WinPath.IsStrictlyUnder(file, l));

    private static void StartSelfDelete(string arguments)
    {
        // The absolute path, never a PATH lookup: this runs with the user's rights and
        // deletes files, so which cmd.exe it is must not depend on the environment.
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Process.Start(new ProcessStartInfo(Path.Combine(systemDirectory, "cmd.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Not the install folder: a process whose directory is there would block its removal.
            WorkingDirectory = systemDirectory,
        })?.Dispose();
    }

    private static int Succeed(string message, bool quiet)
    {
        if (!quiet)
        {
            MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return CliExitCodes.Ok;
    }

    private static int Fail(string message, bool quiet)
    {
        if (!quiet)
        {
            MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return CliExitCodes.Failed;
    }
}
