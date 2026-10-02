namespace RoboRightClick.Core;

/// <summary>Names shared by install, uninstall, the running app and the CLI.</summary>
public static class AppInfo
{
    public const string Name = "RoboRightClick";
    public const string ExeName = "RoboRightClick.exe";
}

/// <summary>
/// Every location the app reads or writes. The host supplies the two known-folder
/// roots; the layout itself is fixed here so install, uninstall and the running app
/// can never disagree about it.
/// </summary>
/// <param name="InstallDirectory">%LOCALAPPDATA%\Programs\RoboRightClick (per-user, no admin).</param>
/// <param name="ConfigFile">%APPDATA%\RoboRightClick\config.json: the only file ephemeral mode may write.</param>
/// <param name="DataDirectory">%LOCALAPPDATA%\RoboRightClick: job logs and history, normal mode only.</param>
public sealed record AppPaths(
    string InstallDirectory,
    string InstalledExe,
    string ConfigDirectory,
    string ConfigFile,
    string DataDirectory,
    string JobsDirectory,
    string HistoryFile)
{
    public const string ConfigFileName = "config.json";
    public const string JobsFolderName = "jobs";
    public const string HistoryFileName = "history.jsonl";
    public const string JobRecordFileName = "job.json";
    public const string RobocopyLogFileName = "robocopy.log";

    /// <summary>
    /// Suffix of the temp file written next to config.json and job.json before File.Replace.
    /// A crash between the write and the replace leaves one behind, so uninstall deletes it too.
    /// </summary>
    public const string TempSuffix = ".tmp";

    public static AppPaths From(string localAppData, string roamingAppData)
    {
        var install = WinPath.Combine(WinPath.Combine(localAppData, "Programs"), AppInfo.Name);
        var config = WinPath.Combine(roamingAppData, AppInfo.Name);
        var data = WinPath.Combine(localAppData, AppInfo.Name);
        return new AppPaths(
            InstallDirectory: install,
            InstalledExe: WinPath.Combine(install, AppInfo.ExeName),
            ConfigDirectory: config,
            ConfigFile: WinPath.Combine(config, ConfigFileName),
            DataDirectory: data,
            JobsDirectory: WinPath.Combine(data, JobsFolderName),
            HistoryFile: WinPath.Combine(data, HistoryFileName));
    }

    /// <summary>The folder for one job's job.json and robocopy.log.</summary>
    public string JobFolder(DateTimeOffset createdAt, Guid jobId) =>
        WinPath.Combine(JobsDirectory, JobLogNames.FolderName(createdAt, jobId));
}
