using RoboRightClick.App;
using RoboRightClick.Cli;
using RoboRightClick.Core;
using RoboRightClick.Install;

namespace RoboRightClick;

internal static class Program
{
    /// <summary>
    /// STA is required twice over: WinForms, and the COM class objects registered on this
    /// thread, whose calls are delivered through its message loop.
    /// </summary>
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
