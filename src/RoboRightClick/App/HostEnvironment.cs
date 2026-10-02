using System.Reflection;
using System.Security.Principal;
using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>Process-wide facts read once: where things live and which build this is.</summary>
internal static class HostEnvironment
{
    private static readonly Lazy<string> CurrentUserSid = new(ReadUserSid);

    public static AppPaths Paths { get; } = AppPaths.From(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>"1.0.0-beta.1", from InformationalVersion in Directory.Build.props.</summary>
    public static string Version { get; } =
        typeof(HostEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>The user's SID in S-1-... form (WindowsIdentity.GetCurrent().User), for the COM security descriptors.</summary>
    public static string UserSid => CurrentUserSid.Value;

    /// <summary>
    /// True when this process runs from <see cref="AppPaths.InstalledExe"/>. A plain start
    /// from anywhere else (the beta tester double-clicking the download) offers to install
    /// instead of starting a tray that no menu item points at.
    /// </summary>
    /// <remarks>
    /// A string comparison (case-insensitive, as NTFS): a start through an 8.3 short name
    /// or a link to the install folder counts as "elsewhere" and is offered the install,
    /// which then finds the exe already in place.
    /// </remarks>
    public static bool RunningFromInstallLocation => WinPath.AreSame(ExecutablePath, Paths.InstalledExe);

    /// <summary>
    /// The running executable. Environment.ProcessPath, not Assembly.Location: a
    /// single-file app has no assembly path.
    /// </summary>
    public static string ExecutablePath { get; } =
        Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unavailable.");

    private static string ReadUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current user has no SID.");
    }
}
