using System.Runtime.InteropServices;
using RoboRightClick.App;
using RoboRightClick.Cli;
using RoboRightClick.Core;
using RoboRightClick.Install;
using RoboRightClick.Jobs;

// Every P/Invoke target is a Windows system DLL: never search the application folder,
// which for a downloaded exe is the Downloads folder (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RoboRightClick;

internal static class Program
{
    // The tray's job manager, for the crash handler, which is installed before the tray
    // exists and may run on any thread. Null outside the tray's lifetime.
    private static JobManager? s_jobs;

    /// <summary>
    /// STA is required twice over: WinForms, and the COM class objects registered on this
    /// thread, whose calls are delivered through its message loop.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // First, before anything loads a DLL by name: the attribute above covers this
        // assembly's own imports, this covers every other LoadLibrary in the process
        // (WinForms, GDI+, COM). If the tray fails to start on Windows, this call is the first
        // suspect. A failure leaves the default search order, which the attribute still narrows.
        AppNative.SetDefaultDllDirectories(AppNative.LOAD_LIBRARY_SEARCH_SYSTEM32);

        ApplicationConfiguration.Initialize();
        CrashPolicy.Install(
            ephemeralJobsActive: () => Volatile.Read(ref s_jobs)?.EphemeralJobsActive ?? false,
            killChildren: () => Volatile.Read(ref s_jobs)?.KillRunningProcesses());

        return CommandLine.Parse(args) switch
        {
            CliRunTray tray => RunTray(tray),
            CliInstall install => Installer.Install(install, HostEnvironment.Paths),
            CliUninstall => Installer.Uninstall(HostEnvironment.Paths),
            CliInvokeVerb verb => CliRunner.InvokeVerb(verb),
            CliHelp => CliRunner.PrintUsage(error: null),
            CliError error => CliRunner.PrintUsage(error.Message),
            var other => throw new InvalidOperationException($"Unhandled command {other.GetType().Name}."),
        };
    }

    private static int RunTray(CliRunTray tray) =>
        StartupRules.Decide(tray, HostEnvironment.RunningFromInstallLocation) switch
        {
            StartupAction.RunTray => TrayApplication.Run(tray.StartedByCom, jobs => Volatile.Write(ref s_jobs, jobs)),
            StartupAction.OfferInstall => Installer.OfferInstall(HostEnvironment.Paths),
            var other => throw new InvalidOperationException($"Unhandled startup action {other}."),
        };
}
