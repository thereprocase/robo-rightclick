namespace RoboRightClick.App;

/// <summary>
/// The composition root and the UI thread's lifetime. Runs on the [STAThread] main
/// thread; Application.Run(this) is the message loop that also dispatches COM calls to
/// the class objects registered by <see cref="Com.ComServer"/>.
/// </summary>
/// <remarks>
/// <para>Start order, kept short because Explorer waits on it when COM starts the tray for
/// a right-click: SingleInstance.TryAcquire (lost: an -Embedding start waits for Ready,
/// then exits 0) → ComCallerSecurity.InitializeProcess → SettingsStore.Load →
/// ClipboardService, JobLogStore, JobManager, VerbDispatcher (constructors do no I/O) →
/// ComServer.Register → SingleInstance.SignalReady → NotifyIcon → message loop. Posted to
/// run once the loop is up: settings watcher, the settings-problem toast, the interrupted-
/// jobs check (normal mode), the first-run tray hint, log pruning. Any exception before the
/// loop shows a MessageBox with the reason and exits 1.</para>
/// <para>Unhandled exceptions (<see cref="CrashPolicy"/>): Application.ThreadException and
/// AppDomain.UnhandledException are handled. While any ephemeral job is active the process
/// kills its robocopy children and ends with TerminateProcess, so Windows Error Reporting
/// never snapshots memory holding job paths (Environment.FailFast would report to WER).</para>
/// <para>Session end: on WM_QUERYENDSESSION with active jobs, ShutdownBlockReasonCreate
/// ("Copying files…") and veto, so Windows shows its standard "an app is preventing
/// shutdown" screen. On WM_ENDSESSION(true), JobManager.CancelAllAndWaitAsync with a 5 s
/// limit so cancel cleanup still runs.</para>
/// <para>Exit order (async, message loop still pumping): confirm if JobManager.HasActiveJobs
/// → ComServer.Dispose (revoke) → await JobManager.CancelAllAndWaitAsync → hide icon →
/// dispose the rest → release the mutex → ExitThread. An exit request from another process
/// (SingleInstance.ExitRequested, marshaled to the UI thread) is honored only when no jobs
/// are active; otherwise it toasts and stays.</para>
/// <para>Tray: left click and double click open Jobs (a job AwaitingDecision brings its
/// conflict dialog to the front instead). Menu: Jobs…, Pause all (checked while on),
/// Resume all, Ephemeral mode (check), Settings…, Open logs (hidden in ephemeral), Exit.
/// Icon and tooltip follow TrayStatus.Derive on JobManager.StateChanged, settings changes
/// and a 1 s timer while jobs run; the tooltip is set only when its text changes.
/// JobManager.Created opens a progress window after ~1 s when showProgressWindow is on.
/// Finished → Notifier. Turning ephemeral on: when job logs exist, ask "Delete the N
/// existing job logs too? [Delete] [Keep]"; when normal-mode jobs are still running, a
/// toast says the change applies to new jobs only.</para>
/// </remarks>
internal sealed class TrayApplication : ApplicationContext
{
    public TrayApplication(bool startedByCom)
    {
        StartedByCom = startedByCom;
    }

    /// <summary>Started by COM (-Embedding) rather than by the user or the Run key. The tray stays resident either way.</summary>
    public bool StartedByCom { get; }

    /// <summary>Runs the tray until exit. Returns the process exit code.</summary>
    public static int Run(bool startedByCom) => throw new NotImplementedException();
}
