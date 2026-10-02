using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>
/// What the process does when something escapes every handler. Ephemeral mode promises
/// that the app writes no job data, and a crash report written by Windows Error Reporting
/// would contain the process memory, paths included. So while ephemeral jobs run, a crash
/// ends the process without involving WER.
/// </summary>
internal static class CrashPolicy
{
    private static Func<bool>? s_ephemeralJobsActive;
    private static Action? s_killChildren;
    private static int s_uiThreadId;

    // 1 while a crash message box is up, so a burst of failures shows one box, not a stack of them.
    private static int s_showingMessage;

    /// <summary>
    /// Called first in Main: SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX),
    /// Application.SetUnhandledExceptionMode(CatchException), and handlers for
    /// Application.ThreadException and AppDomain.UnhandledException that call
    /// <see cref="OnUnhandled"/>.
    /// </summary>
    /// <param name="ephemeralJobsActive">Asked at crash time; must not take locks that a crashing thread may hold (read a volatile counter).</param>
    /// <param name="killChildren">Best-effort kill of every robocopy the app started (the kill-on-close job object also does this when the process ends).</param>
    public static void Install(Func<bool> ephemeralJobsActive, Action killChildren)
    {
        s_ephemeralJobsActive = ephemeralJobsActive;
        s_killChildren = killChildren;
        s_uiThreadId = Environment.CurrentManagedThreadId;

        // The error mode is inherited by child processes, so robocopy hitting a removed drive
        // also fails with an error code instead of a system dialog nobody is watching.
        AppNative.SetErrorMode(AppNative.GetErrorMode() | AppNative.SEM_FAILCRITICALERRORS | AppNative.SEM_NOGPFAULTERRORBOX);

        // Must precede the first window: afterwards WinForms refuses to change the mode.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnUnhandled(e.ExceptionObject as Exception ?? new InvalidOperationException("A non-exception object was thrown."));
    }

    /// <summary>
    /// Ephemeral jobs active: killChildren, then TerminateProcess(GetCurrentProcess(), 1).
    /// Otherwise (normal mode): show a MessageBox with the exception message when on the UI
    /// thread and let the runtime's default handling continue.
    /// </summary>
    public static void OnUnhandled(Exception exception)
    {
        if (EphemeralJobsActive())
        {
            EndWithoutReport();
            return;
        }

        if (Environment.CurrentManagedThreadId != s_uiThreadId
            || Interlocked.Exchange(ref s_showingMessage, 1) == 1)
        {
            return;
        }
        try
        {
            MessageBox.Show(
                $"{AppInfo.Name} hit an unexpected error. Restart it from the Start menu if it stops responding.\n\n{exception.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Reporting the crash must not become a second crash.
        }
        finally
        {
            Volatile.Write(ref s_showingMessage, 0);
        }
    }

    /// <summary>
    /// When the question itself fails, assume ephemeral jobs are running: ending without a
    /// report loses nothing that normal mode would have kept (job.json reports the
    /// interruption at the next start), while guessing wrong the other way leaks paths.
    /// </summary>
    private static bool EphemeralJobsActive()
    {
        try
        {
            return s_ephemeralJobsActive?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// TerminateProcess, not Environment.FailFast: FailFast reports to WER. Children first,
    /// so no robocopy keeps writing after the app that tracks it is gone; the job object's
    /// kill-on-close covers this too, and also covers a failure here.
    /// </summary>
    private static void EndWithoutReport()
    {
        try
        {
            s_killChildren?.Invoke();
        }
        catch (Exception)
        {
            // Best effort; the kill-on-close job object ends them with the process.
        }
        AppNative.TerminateProcess(AppNative.GetCurrentProcess(), 1);

        // Unreachable unless TerminateProcess failed. Exit still avoids WER.
        Environment.Exit(1);
    }
}
