using System.Runtime.InteropServices;
using RoboRightClick.App;
using RoboRightClick.Cli;
using RoboRightClick.Core;
using RoboRightClick.Install;

// Every P/Invoke target is a Windows system DLL: never search the application folder,
// which for a downloaded exe is the Downloads folder (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RoboRightClick;

internal static class Program
{
    /// <summary>
    /// STA is required twice over: WinForms, and the COM class objects registered on this
    /// thread, whose calls are delivered through its message loop.
    /// </summary>
    /// <remarks>
    /// To be wired by the integration package: first SetDefaultDllDirectories(
    /// LOAD_LIBRARY_SEARCH_SYSTEM32) (if the tray then fails to start on Windows, this call is
    /// the first suspect), then CrashPolicy.Install. A plain start (no -Embedding) from
    /// outside the install folder goes to Installer.OfferInstall instead of the tray.
    /// </remarks>
    [STAThread]
    private static int Main(string[] args) => CommandLine.Parse(args) switch
    {
        CliRunTray tray => TrayApplication.Run(tray.StartedByCom),
        CliInstall install => Installer.Install(install, HostEnvironment.Paths),
        CliUninstall => Installer.Uninstall(HostEnvironment.Paths),
        CliInvokeVerb verb => CliRunner.InvokeVerb(verb),
        CliHelp => CliRunner.PrintUsage(error: null),
        CliError error => CliRunner.PrintUsage(error.Message),
        var other => throw new InvalidOperationException($"Unhandled command {other.GetType().Name}."),
    };
}
