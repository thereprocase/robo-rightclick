namespace RoboRightClick.App;

/// <summary>
/// What the process does when something escapes every handler. Ephemeral mode promises
/// that the app writes no job data, and a crash report written by Windows Error Reporting
/// would contain the process memory, paths included. So while ephemeral jobs run, a crash
/// ends the process without involving WER.
/// </summary>
internal static class CrashPolicy
{
    /// <summary>
    /// Called first in Main: SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX),
    /// Application.SetUnhandledExceptionMode(CatchException), and handlers for
    /// Application.ThreadException and AppDomain.UnhandledException that call
    /// <see cref="OnUnhandled"/>.
    /// </summary>
    /// <param name="ephemeralJobsActive">Asked at crash time; must not take locks that a crashing thread may hold (read a volatile counter).</param>
    /// <param name="killChildren">Best-effort kill of every robocopy the app started (the kill-on-close job object also does this when the process ends).</param>
    public static void Install(Func<bool> ephemeralJobsActive, Action killChildren) => throw new NotImplementedException();

    /// <summary>
    /// Ephemeral jobs active: killChildren, then TerminateProcess(GetCurrentProcess(), 1).
    /// Otherwise (normal mode): show a MessageBox with the exception message when on the UI
    /// thread and let the runtime's default handling continue.
    /// </summary>
    public static void OnUnhandled(Exception exception) => throw new NotImplementedException();
}
