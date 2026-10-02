using System.Reflection;
using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>Process-wide facts read once: where things live and which build this is.</summary>
internal static class HostEnvironment
{
    public static AppPaths Paths { get; } = AppPaths.From(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>"1.0.0-beta.1", from InformationalVersion in Directory.Build.props.</summary>
    public static string Version { get; } =
        typeof(HostEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>The user's SID in S-1-... form (WindowsIdentity.GetCurrent().User), for the COM security descriptors.</summary>
    public static string UserSid => throw new NotImplementedException();

    /// <summary>
    /// True when this process runs from <see cref="AppPaths.InstalledExe"/>. A plain start
    /// from anywhere else (the beta tester double-clicking the download) offers to install
    /// instead of starting a tray that no menu item points at.
    /// </summary>
    public static bool RunningFromInstallLocation => throw new NotImplementedException();

    /// <summary>
    /// The running executable. Environment.ProcessPath, not Assembly.Location: a
    /// single-file app has no assembly path.
    /// </summary>
    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unavailable.");
}
