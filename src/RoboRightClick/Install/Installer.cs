using System.Diagnostics;
using RoboRightClick.App;
using RoboRightClick.Jobs;
using RoboRightClick.Core;
using RoboRightClick.UI;

namespace RoboRightClick.Install;

/// <summary>
/// Per-user install and uninstall. No admin rights, no Explorer settings, and no Explorer
/// restart: classic static verbs are read from the registry on each right-click. Results
/// are shown in a Gridline dialog (these are one-time user actions, and a GUI-subsystem exe's
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

    private const string NewerConfigKeptMessage =
        "config.json was written by a newer version of RoboRightClick, or its \"version\" field is damaged, "
        + "so it was left unchanged; "
        + "the start-with-Windows choice applies to the registry only.";

    /// <summary>
    /// 0. <see cref="UninstallPlan.InstallRefusal"/>: a location uninstall would refuse fails
    /// the install (exit code 1, message) before anything changes. 1. Read what is installed
    /// (<see cref="ReadInstalledFacts"/>) and ask <see cref="InstallDecision.Decide"/>: fresh
    /// install, update, repair, or refuse to replace a newer version (exit code 1, nothing
    /// changed) unless --force. 2. If a tray is running, SingleInstance.RequestExitAndWait
    /// (fails with a message if it will not exit because jobs are active; a running copy is
    /// never killed). 3. Replace the installed exe with this one unless it already runs from
    /// there: a copy to a temp name in the same folder, then File.Replace, which keeps the old
    /// exe as a .old copy; the result is compared with the source. 4. Rewrite the menu icons.
    /// 5. Read an existing config.json, if any. 6.
    /// RegistryWriter.Write(Registration.InstallValues(new InstallTarget(installedExe,
    /// Registration.ResolveStartWithWindows(command.StartWithWindows, existing), userSid,
    /// version))). 7. Write the default config.json only if none exists, or write back the
    /// existing one with an explicit --autostart/--no-autostart applied, unless a newer
    /// version wrote it (then it is left as it is, and the message says so). An update adds
    /// nothing to config.json: a field this version knows and the file lacks reads as its
    /// default (<see cref="SettingsSerializer"/>), so the user's file is never rewritten. 8.
    /// Success deletes the .old copy; any failure from step 3 on restores it, puts the old
    /// version back in DisplayVersion, restarts the old tray and reports the failure, so the
    /// previous install stays as it was. 9. After an update or repair, SHChangeNotify
    /// (SHCNE_ASSOCCHANGED) so Explorer reloads the cached menu icons. 10. Start the installed
    /// exe (tray), with <see cref="CommandLine.AfterInstallSwitch"/> only for a fresh install,
    /// so the first-run hint shows once. 11. Message: <see cref="InstallText.Done"/> and
    /// <see cref="InstallText.Installed"/> (none with --quiet; the exit code is the result).
    /// </summary>
    public static int Install(CliInstall command, AppPaths paths)
    {
        var quiet = command.Quiet;
        try
        {
            // First, before the tray is stopped or anything is written: an install uninstall
            // would later refuse to remove must not happen at all.
            if (UninstallPlan.InstallRefusal(paths, Environment.GetFolderPath(Environment.SpecialFolder.System)) is { } refusal)
            {
                return Fail("Can't install", refusal, quiet);
            }

            var facts = ReadInstalledFacts(paths);
            var decision = InstallDecision.Decide(facts, HostEnvironment.Version, command.Force);
            if (decision is InstallDecision.RefuseDowngrade refuse)
            {
                RepairStaleVersionLabel(facts);
                return Fail("A newer version is installed", InstallText.DowngradeRefused(refuse), quiet);
            }

            if (!SingleInstance.RequestExitAndWait(TrayExitTimeout))
            {
                return Fail("Jobs are still running", BusyMessage, quiet);
            }

            string? backup = null;
            try
            {
                backup = CopyExecutable(HostEnvironment.ExecutablePath, paths);
                WriteMenuIcons(paths);

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

                var configKept = !WriteConfig(paths, command.StartWithWindows, startWithWindows, existing, existingText, existingHadProblems);

                // The hotkey the tray will load: a missing file was just written with the defaults.
                var pasteHotkey = existing is null ? Settings.Default.PasteHotkey : existing.PasteHotkey;

                DeleteBackup(backup);
                if (decision is not InstallDecision.FreshInstall)
                {
                    ShellNotify.AssociationsChanged();
                }
                StartTray(paths, afterInstall: decision is InstallDecision.FreshInstall);
                return Succeed(
                    InstallText.Done(decision),
                    InstallText.Installed(pasteHotkey)
                        + (configKept ? "\n\n" + NewerConfigKeptMessage : string.Empty),
                    quiet);
            }
            catch
            {
                if (decision is not InstallDecision.FreshInstall)
                {
                    RollBack(backup, paths, decision);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            return Fail("Install failed", $"{ex.Message} Close any program that uses the install folder and run the install again.", quiet);
        }
    }

    /// <summary>
    /// A plain start from outside the install folder (a double-clicked download): a Gridline
    /// dialog worded by <see cref="InstallText.Offer"/> for what is installed (nothing: install,
    /// with a "Start with Windows" box; older: update; the same: repair, plus Open; newer:
    /// an explanation and Open, nothing that changes the install). The action button runs
    /// <see cref="Install"/> with the same decision; Open starts the installed app; Cancel,
    /// Escape or the close box exit 0 and change nothing.
    /// </summary>
    public static int OfferInstall(AppPaths paths)
    {
        Settings? existing;
        try
        {
            existing = ReadExistingConfig(paths, out _, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existing = null;
        }

        var facts = ReadInstalledFacts(paths);
        var decision = InstallDecision.Decide(facts, HostEnvironment.Version, force: false);
        var offer = InstallText.Offer(decision);
        var canOpen = offer.CanOpen && File.Exists(paths.InstalledExe);

        // Only a first install asks about autostart; an update or repair keeps the user's choice.
        var startDefault = Registration.ResolveStartWithWindows(explicitChoice: null, existing);
        Gridline.CheckBox? startWithWindows = decision is InstallDecision.FreshInstall
            ? new Gridline.CheckBox("Start with Windows when I sign in", "StartWithWindows") { Checked = startDefault }
            : null;

        var facts2 = new List<(string, string)> { ("This version", HostEnvironment.Version) };
        if (decision is not InstallDecision.FreshInstall)
        {
            facts2.Add(("Installed", InstallDecision.ResolveInstalled(facts)?.Text ?? "unknown"));
        }
        facts2.Add(("Installs to", paths.InstallDirectory));
        facts2.Add(("Settings", paths.ConfigFile));
        facts2.Add(("Remove with", "Settings > Apps > Installed apps, or RoboRightClick.exe --uninstall"));

        var content = new MessageContent(offer.Heading.TrimEnd('?', '.'), offer.Body)
        {
            PaneTitle = decision switch
            {
                InstallDecision.FreshInstall => "Install for this user",
                InstallDecision.Update => "Update",
                InstallDecision.Repair => "Repair",
                _ => "Installed version",
            },
            Heading = offer.Heading,
            Facts = facts2,
        };

        var buttons = new List<DialogButton>();
        if (offer.Action is { } action)
        {
            buttons.Add(new DialogButton(action, action, DialogResult.OK, IsDefault: true));
        }
        if (canOpen)
        {
            buttons.Add(new DialogButton("Open", "Open", DialogResult.Yes, IsDefault: offer.Action is null));
        }
        buttons.Add(new DialogButton(offer.Action is null ? "Close" : "Cancel", "Cancel", DialogResult.Cancel, IsCancel: true));

        var answer = MessageDialog.Show(owner: null, content, buttons, startWithWindows);
        if (answer == DialogResult.Yes)
        {
            return OpenInstalled(paths);
        }
        if (answer != DialogResult.OK)
        {
            return CliExitCodes.Ok;
        }

        // An unchanged box passes no choice, so a reinstall leaves an existing config.json as it is.
        bool? choice = startWithWindows is null || startWithWindows.Checked == startDefault ? null : startWithWindows.Checked;
        return Install(new CliInstall(choice), paths);
    }

    /// <summary>Starts the installed exe with no arguments, which starts or opens the tray.</summary>
    private static int OpenInstalled(AppPaths paths)
    {
        try
        {
            Process.Start(new ProcessStartInfo(paths.InstalledExe)
            {
                UseShellExecute = false,
                WorkingDirectory = paths.InstallDirectory,
            })?.Dispose();
            return CliExitCodes.Ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Fail("Can't open", $"The installed {AppInfo.Name} could not be started: {ex.Message}", quiet: false);
        }
    }

    /// <summary>
    /// 1. Stop the running tray as in install (refuse while jobs run). 2.
    /// RegistryWriter.Remove(Registration.UninstallRemovals()). 3. Delete exactly the files
    /// <see cref="UninstallPlan"/> lists (config.json, config.json.bad, history.jsonl, crash.log, each
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
                ? UninstallPlan.SelfDeleteArguments(paths.InstalledExe, paths.InstallDirectory, Environment.GetFolderPath(Environment.SpecialFolder.System))
                : null;

            if (!SingleInstance.RequestExitAndWait(TrayExitTimeout))
            {
                return Fail("Jobs are still running", BusyMessage, quiet);
            }

            // Job folders are listed once the tray has stopped, so none appears afterwards.
            var plan = UninstallPlan.For(paths, ListJobFolderNames(paths), runningFromInstall);

            RegistryWriter.Remove(Registration.UninstallRemovals());
            var leftBehind = RemovePlanned(plan, paths.InstalledExe);

            // The message comes before the cleanup process starts: it waits only about 30
            // seconds for this process to exit, and a message box can stay open longer.
            var message = leftBehind == 0
                ? "The right-click items, the tray icon, the settings and the job logs were removed."
                : leftBehind == 1
                    ? "The right-click items and the tray icon were removed. 1 item was left in place because RoboRightClick did not create it or could not remove it."
                    : $"The right-click items and the tray icon were removed. {leftBehind} items were left in place because RoboRightClick did not create them or could not remove them.";
            var code = Succeed($"{AppInfo.Name} was uninstalled.", message, quiet);

            if (selfDelete is not null)
            {
                StartSelfDelete(selfDelete);
            }
            return code;
        }
        catch (Exception ex)
        {
            return Fail("Uninstall failed", $"{ex.Message} Close any program that uses RoboRightClick's files and run the uninstall again.", quiet);
        }
    }

    /// <summary>
    /// Puts this exe at the installed path. Returns the path of the .old copy of the previous
    /// exe that a failed install restores, or null when there was nothing to keep (a first
    /// install, or already running from the install folder).
    /// </summary>
    private static string? CopyExecutable(string source, AppPaths paths)
    {
        if (WinPath.AreSame(source, paths.InstalledExe))
        {
            return null;
        }

        Directory.CreateDirectory(paths.InstallDirectory);

        // A temp name in the same folder, then a replace: a failed copy never leaves a
        // truncated exe at the path the registry points to.
        var temp = WinPath.Combine(paths.InstallDirectory, AppInfo.ExeName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = paths.InstalledExe + AppPaths.BackupExeSuffix;
        var replaced = false;
        try
        {
            File.Copy(source, temp, overwrite: false);
            if (File.Exists(paths.InstalledExe))
            {
                // A .old left by an interrupted earlier update is stale: the installed exe is
                // whole (the swap below is atomic), so it is the one worth keeping.
                File.Delete(backup);
                RetryWhileExeInUse(() => File.Replace(temp, paths.InstalledExe, backup, ignoreMetadataErrors: true));
                replaced = true;
            }
            else
            {
                RetryWhileExeInUse(() => File.Move(temp, paths.InstalledExe, overwrite: false));
            }

            // An install once reported success and left the previous exe in place (docs/testlog.md
            // 2026-10-02): check that what is installed is what was meant to be.
            if (!SameContent(source, paths.InstalledExe))
            {
                throw new IOException("The installed program file does not match the one being installed.");
            }
            return replaced ? backup : null;
        }
        catch
        {
            if (replaced)
            {
                RestoreExecutable(backup, paths);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Writes each verb's .ico beside the installed exe, where its registry Icon value points.
    /// Rewritten on every install so a reinstall also refreshes the art. Each file goes to a
    /// temp name first, so the shell never reads a half-written icon.
    /// </summary>
    private static void WriteMenuIcons(AppPaths paths)
    {
        var assembly = typeof(Installer).Assembly;
        foreach (var verb in ShellVerbs.All)
        {
            var name = ShellVerbs.IconFileName(verb);
            using var source = assembly.GetManifestResourceStream("RoboRightClick.Icons." + name)
                ?? throw new InvalidOperationException($"The {verb.Label} menu icon is missing from this build.");
            var target = WinPath.Combine(paths.InstallDirectory, name);
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = File.Create(temp))
                {
                    source.CopyTo(file);
                }
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
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

    /// <summary>
    /// Writes config.json as described on <see cref="Install"/>. Returns false when an
    /// explicit autostart choice was not written because the file comes from a newer version
    /// or its version cannot be read (<see cref="SettingsSerializer.MayOverwrite"/>): the Run value still follows the
    /// choice, the file stays exactly as the newer version left it.
    /// </summary>
    private static bool WriteConfig(
        AppPaths paths, bool? explicitChoice, bool startWithWindows,
        Settings? existing, string? existingText, bool existingHadProblems)
    {
        if (existing is null)
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
            WriteAtomically(paths.ConfigFile, SettingsSerializer.Serialize(Settings.Default with { StartWithWindows = startWithWindows }));
            return true;
        }

        // A plain reinstall leaves the user's file, comments and all, exactly as it is.
        if (explicitChoice is null)
        {
            return true;
        }

        if (!SettingsSerializer.MayOverwrite(existingText))
        {
            return false;
        }

        if (existingHadProblems && existingText is not null)
        {
            // Rewriting normalizes the file and drops what could not be parsed; keep the original.
            File.WriteAllText(paths.ConfigFile + UninstallPlan.BackupSuffix, existingText);
        }
        WriteAtomically(paths.ConfigFile, SettingsSerializer.Serialize(existing with { StartWithWindows = startWithWindows }));
        return true;
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

    /// <summary>
    /// What is installed now: the installed exe's product version (read from the file, which
    /// is what runs) and the Uninstall key's DisplayVersion. Either may be missing or
    /// unreadable; <see cref="InstallDecision"/> decides what that means.
    /// </summary>
    private static InstalledFacts ReadInstalledFacts(AppPaths paths)
    {
        var exeExists = File.Exists(paths.InstalledExe);
        string? exeVersion = null;
        if (exeExists)
        {
            try
            {
                exeVersion = FileVersionInfo.GetVersionInfo(paths.InstalledExe).ProductVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable counts as no version, which Decide treats as older.
            }
        }

        var (keyExists, registryVersion) = RegistryWriter.ReadInstalledVersion();
        return new InstalledFacts(exeExists || keyExists, exeVersion, registryVersion);
    }

    /// <summary>
    /// Best effort when an install is refused: if the Installed apps entry names a version
    /// other than the exe's, correct the label so the list tells the truth. Nothing else changes.
    /// </summary>
    private static void RepairStaleVersionLabel(InstalledFacts facts)
    {
        if (InstallDecision.StaleRegistryVersion(facts) is not { } version)
        {
            return;
        }
        try
        {
            RegistryWriter.Write([new RegistryValue(Registration.UninstallKey, "DisplayVersion", version)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A label that stays wrong is not worth failing the refusal for.
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
        {
            return false;
        }
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(first)
            .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }

    private static void RestoreExecutable(string backup, AppPaths paths) =>
        RetryWhileExeInUse(() => File.Move(backup, paths.InstalledExe, overwrite: true));

    /// <summary>
    /// The install failed after the exe was replaced: put the previous exe back, give
    /// DisplayVersion its old value and restart the tray that was stopped, so the old install
    /// is as it was (<paramref name="backup"/> is null when the exe was never replaced or was already restored). (The menu icons are rewritten from the new build and stay; they are the
    /// same files with possibly newer art.) A failure here is swallowed: the original error is
    /// the one worth reporting, and the .old copy is left beside the exe for uninstall to remove.
    /// </summary>
    private static void RollBack(string? backup, AppPaths paths, InstallDecision decision)
    {
        try
        {
            if (backup is not null && File.Exists(backup))
            {
                RestoreExecutable(backup, paths);
            }
            var old = decision switch
            {
                InstallDecision.Update u => u.From,
                InstallDecision.Repair r => r.Version,
                _ => null,
            };
            if (old is not null)
            {
                RegistryWriter.Write([new RegistryValue(Registration.UninstallKey, "DisplayVersion", old)]);
            }
            StartTray(paths, afterInstall: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            // See the summary.
        }
    }

    private static void DeleteBackup(string? backup)
    {
        if (backup is null)
        {
            return;
        }
        try
        {
            File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Uninstall removes a leftover .old; it does not affect the running install.
        }
    }

    /// <param name="afterInstall">
    /// True for a first install only: the tray then shows the hint about pinning its icon. An
    /// update or a restart after a rollback starts it plainly.
    /// </param>
    private static void StartTray(AppPaths paths, bool afterInstall)
    {
        var start = new ProcessStartInfo(paths.InstalledExe)
        {
            UseShellExecute = false,
            WorkingDirectory = paths.InstallDirectory,
        };
        if (afterInstall)
        {
            start.ArgumentList.Add(CommandLine.AfterInstallSwitch);
        }
        Process.Start(start)?.Dispose();
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

    private static int Succeed(string heading, string message, bool quiet)
    {
        if (!quiet)
        {
            Notice(heading, message, MessageTone.Neutral);
        }
        return CliExitCodes.Ok;
    }

    private static int Fail(string heading, string message, bool quiet)
    {
        if (!quiet)
        {
            Notice(heading, message, MessageTone.Danger);
        }
        return CliExitCodes.Failed;
    }

    private static void Notice(string heading, string message, MessageTone tone) =>
        MessageDialog.Notice(owner: null, heading, message, tone);
}
