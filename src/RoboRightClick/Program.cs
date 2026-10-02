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

    // The last job manager the tray published, kept after teardown: whether crash.log may be
    // written depends on the session (any ephemeral job since the start), which outlives it.
    private static JobManager? s_sessionJobs;

    // Set once this process runs as the tray rather than install, uninstall or the CLI.
    private static volatile bool s_isTray;

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
            killChildren: () => Volatile.Read(ref s_jobs)?.KillRunningProcesses(),
            crashLogAllowed: CrashLogAllowed);

        return CommandLine.Parse(args) switch
        {
            CliRunTray tray => RunTray(tray),
            CliInstall install => Installer.Install(install, HostEnvironment.Paths),
            CliUninstall uninstall => Installer.Uninstall(uninstall, HostEnvironment.Paths),
            CliInvokeVerb verb => CliRunner.InvokeVerb(verb),
            CliHelp => CliRunner.PrintUsage(error: null),
            CliError error => CliRunner.PrintUsage(error.Message),
            var other => throw new InvalidOperationException($"Unhandled command {other.GetType().Name}."),
        };
    }

    private static int RunTray(CliRunTray tray)
    {
        switch (StartupRules.Decide(tray, HostEnvironment.RunningFromInstallLocation))
        {
            case StartupAction.RunTray:
                s_isTray = true;
                return TrayApplication.Run(tray, PublishJobs);
            case StartupAction.OfferInstall:
                return Installer.OfferInstall(HostEnvironment.Paths);
            case var other:
                throw new InvalidOperationException($"Unhandled startup action {other}.");
        }
    }

    private static void PublishJobs(JobManager? jobs)
    {
        if (jobs is not null)
        {
            Volatile.Write(ref s_sessionJobs, jobs);
        }
        Volatile.Write(ref s_jobs, jobs);
    }

    /// <summary>
    /// crash.log is written only by the tray; install, uninstall and the CLI write none. Once
    /// the job manager exists, its current settings and session decide. Before it does (a
    /// start that fails, such as CoInitializeSecurity refusing), no job can exist yet and
    /// config.json decides (<see cref="CrashLog.ModeFromConfig"/>). Asked at crash time.
    /// </summary>
    private static bool CrashLogAllowed()
    {
        if (Volatile.Read(ref s_sessionJobs) is { } jobs)
        {
            return CrashLog.MayWrite(jobs.CurrentSettings().Logging, jobs.EphemeralJobsThisSession);
        }
        return s_isTray && CrashLog.MayWrite(LoggingModeFromConfigFile(), ephemeralJobsThisSession: false);
    }

    private static LoggingMode? LoggingModeFromConfigFile()
    {
        try
        {
            return CrashLog.ModeFromConfig(File.ReadAllText(HostEnvironment.Paths.ConfigFile), configReadFailed: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CrashLog.ModeFromConfig(null, configReadFailed: false);
        }
        catch (Exception)
        {
            return CrashLog.ModeFromConfig(null, configReadFailed: true);
        }
    }
}
