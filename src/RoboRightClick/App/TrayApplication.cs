using RoboRightClick.Com;
using RoboRightClick.Jobs;
using RoboRightClick.Logging;
using RoboRightClick.UI;
using RoboRightClick.Verbs;

namespace RoboRightClick.App;

/// <summary>
/// The composition root and the UI thread's lifetime. Runs on the [STAThread] main
/// thread; Application.Run(this) is the message loop that also dispatches COM calls to
/// the class objects registered by <see cref="ComServer"/>.
/// </summary>
/// <remarks>
/// Start order: SingleInstance (exit 0 if another tray owns it) → SettingsStore.Load →
/// NotifyIcon + TrayIcons + Notifier → ClipboardService → JobLogStore → JobManager →
/// VerbDispatcher → ComServer.Register (last, so no call arrives before its handler
/// exists) → one settings-problem toast if needed.
/// Exit order: confirm if JobManager.HasActiveJobs → ComServer.Dispose (revoke) →
/// JobManager.CancelAllAndWaitAsync → hide icon → dispose the rest → release the mutex.
/// An exit request from another process (SingleInstance.ExitRequested) is honored only
/// when no jobs are active; otherwise it toasts and stays.
/// Tray menu: Jobs…, Pause all, Resume all, Ephemeral mode (check), Settings…, Open logs
/// (hidden in ephemeral), Exit. Double-click opens Jobs. The icon and tooltip follow
/// TrayStatus.Derive on every JobManager.Changed and settings change. Ephemeral toggling
/// saves the setting; when jobs created in normal mode are still running, a toast says the
/// change applies to new jobs only.
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
