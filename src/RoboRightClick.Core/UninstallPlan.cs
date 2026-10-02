namespace RoboRightClick.Core;

/// <summary>
/// Exactly what uninstall deletes from the file system, as data. Everything listed lives
/// under a location in <see cref="AppPaths"/>, and nothing else can be listed: the host
/// deletes these files, removes these directories with RemoveDirectory (which fails on
/// anything unexpected left inside) and never walks a directory to delete it. A job folder
/// name that does not look like one the app created is ignored, so a stray user folder in
/// the logs directory survives.
/// </summary>
/// <param name="Files">Files to delete, each inside one of the listed directories.</param>
/// <param name="Directories">Directories to remove after the files, deepest first.</param>
/// <param name="ExpectedLeafNames">
/// For each directory, the last path component it must have. The host re-checks this on the
/// path it is about to remove, so a path that changed between planning and deleting is refused.
/// </param>
public sealed record UninstallPlan(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Directories,
    IReadOnlyDictionary<string, string> ExpectedLeafNames)
{
    public const string BackupSuffix = ".bad";
    public const string RotatedHistoryFileName = "history.1.jsonl";

    /// <summary>
    /// Characters that cmd.exe interprets or that could end a quoted argument. They are
    /// refused in every path that reaches a command line or a delete list, instead of being
    /// escaped: cmd's escaping rules differ between quoted and unquoted text and between
    /// /c and batch files, and a path with one of these is not worth the risk.
    /// </summary>
    private static readonly char[] UnsafePathCharacters = ['"', '&', '|', '<', '>', '^', '%', '!'];

    /// <param name="jobFolderNames">Leaf names found in the jobs directory (unvalidated).</param>
    /// <param name="runningFromInstallDir">
    /// True when the running exe is the installed one: the install folder is then left to
    /// <see cref="SelfDeleteArguments"/>, because a running exe cannot delete itself.
    /// </param>
    /// <exception cref="ArgumentException">A path has an unsafe character or an unexpected layout.</exception>
    public static UninstallPlan For(AppPaths paths, IReadOnlyList<string> jobFolderNames, bool runningFromInstallDir)
    {
        RequireLayout(paths);

        var jobs = new List<string>();
        foreach (var name in jobFolderNames)
        {
            if (JobLogNames.IsJobFolderName(name) && !jobs.Contains(name, StringComparer.Ordinal))
            {
                jobs.Add(name);
            }
        }

        var files = new List<string>();
        var directories = new List<string>();
        var leaves = new Dictionary<string, string>(WinPath.Comparer);

        void AddDirectory(string path, string expectedLeaf)
        {
            RequireSafe(path);
            RequireLeaf(path, expectedLeaf);
            directories.Add(path);
            leaves[path] = expectedLeaf;
        }

        foreach (var name in jobs)
        {
            var folder = WinPath.Combine(paths.JobsDirectory, name);
            files.Add(WinPath.Combine(folder, AppPaths.JobRecordFileName));
            files.Add(WinPath.Combine(folder, AppPaths.RobocopyLogFileName));
            files.Add(WinPath.Combine(folder, AppPaths.JobRecordFileName + AppPaths.TempSuffix));
            AddDirectory(folder, name);
        }
        AddDirectory(paths.JobsDirectory, AppPaths.JobsFolderName);

        files.Add(paths.HistoryFile);
        files.Add(WinPath.Combine(paths.DataDirectory, RotatedHistoryFileName));
        AddDirectory(paths.DataDirectory, AppInfo.Name);

        files.Add(paths.ConfigFile);
        files.Add(paths.ConfigFile + BackupSuffix);
        files.Add(paths.ConfigFile + AppPaths.TempSuffix);
        AddDirectory(paths.ConfigDirectory, AppInfo.Name);

        if (!runningFromInstallDir)
        {
            // The installed exe is the only file the installer put there. Anything else in
            // the folder makes RemoveDirectory fail, which leaves it for the user.
            files.Add(paths.InstalledExe);
            AddDirectory(paths.InstallDirectory, AppInfo.Name);
        }

        foreach (var file in files)
        {
            RequireSafe(file);
        }
        return new UninstallPlan(files, directories, leaves);
    }

    /// <summary>
    /// The argument string for %SystemRoot%\System32\cmd.exe that deletes the running exe
    /// after this process exits, then removes the (by then empty) install folder. Deleting
    /// the exe is retried for about 30 seconds because it fails while the process is alive.
    /// The folder is removed with rd without /s: if anything else is in it, it stays.
    /// </summary>
    public static string SelfDeleteArguments(string exePath, string installDir)
    {
        RequireSafe(exePath);
        RequireSafe(installDir);
        RequireLeaf(installDir, AppInfo.Name);
        if (!WinPath.AreSame(WinPath.GetParent(exePath), installDir)
            || !WinPath.Comparer.Equals(WinPath.GetFileName(exePath), AppInfo.ExeName))
        {
            throw new ArgumentException("The executable must be the installed one inside the install folder.", nameof(exePath));
        }

        // /s makes cmd strip the outermost pair of quotes and keep the rest as written.
        // ping is the one-second sleep: timeout.exe refuses to run without console input.
        return "/d /s /c \"for /l %n in (1,1,30) do @(del /f /q \"" + exePath + "\" >nul 2>&1 & "
            + "if not exist \"" + exePath + "\" (rd \"" + installDir + "\" >nul 2>&1 & exit /b 0) "
            + "else (ping -n 2 127.0.0.1 >nul))\"";
    }

    private static void RequireSafe(string path)
    {
        if (path.IndexOfAny(UnsafePathCharacters) >= 0 || path.Any(char.IsControl))
        {
            throw new ArgumentException("A path contains a character that is refused on a delete or command line.", nameof(path));
        }
    }

    private static void RequireLeaf(string path, string expected)
    {
        if (!string.Equals(WinPath.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected a folder named '{expected}'.", nameof(path));
        }
    }

    /// <summary>
    /// Guards against an AppPaths assembled by hand: every location must sit where
    /// <see cref="AppPaths.From"/> would put it, so the plan cannot reach outside.
    /// </summary>
    private static void RequireLayout(AppPaths paths)
    {
        var consistent =
            WinPath.AreSame(WinPath.GetParent(paths.JobsDirectory), paths.DataDirectory)
            && WinPath.AreSame(WinPath.GetParent(paths.HistoryFile), paths.DataDirectory)
            && WinPath.AreSame(WinPath.GetParent(paths.ConfigFile), paths.ConfigDirectory)
            && WinPath.AreSame(WinPath.GetParent(paths.InstalledExe), paths.InstallDirectory)
            && WinPath.Comparer.Equals(WinPath.GetFileName(paths.ConfigFile), AppPaths.ConfigFileName)
            && WinPath.Comparer.Equals(WinPath.GetFileName(paths.HistoryFile), AppPaths.HistoryFileName)
            && WinPath.Comparer.Equals(WinPath.GetFileName(paths.InstalledExe), AppInfo.ExeName);
        if (!consistent)
        {
            throw new ArgumentException("AppPaths is not laid out as AppPaths.From lays it out.", nameof(paths));
        }
    }
}
