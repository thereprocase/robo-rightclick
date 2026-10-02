using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>
/// What the process does when something escapes every handler. Ephemeral mode promises
/// that the app writes no job data, and a crash report written by Windows Error Reporting
/// would contain the process memory, paths included. So while ephemeral jobs run, a crash
/// ends the process without involving WER. Otherwise, in normal mode with no ephemeral job
/// in this session (<see cref="CrashLog.MayWrite"/>), one entry is appended to crash.log
/// first (<see cref="CrashLog"/>), so a beta tester can send the actual exception.
/// </summary>
internal static class CrashPolicy
{
    private static Func<bool>? s_ephemeralJobsActive;
    private static Action? s_killChildren;
    private static Func<bool>? s_crashLogAllowed;
    private static int s_uiThreadId;

    private static readonly TimeSpan KillChildrenLimit = TimeSpan.FromSeconds(2);

    /// <summary>Two crashes at once write one after the other; a writer stuck longer than this is skipped, not waited for.</summary>
    private static readonly TimeSpan CrashLogLockLimit = TimeSpan.FromSeconds(2);

    private static readonly Lock s_crashLogGate = new();
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static int s_crashLogEntries;

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
    /// <param name="crashLogAllowed">
    /// Asked at crash time, lock-free like <paramref name="ephemeralJobsActive"/>: whether
    /// <see cref="CrashLog.MayWrite"/> holds (a tray in normal mode, no ephemeral job this session).
    /// </param>
    public static void Install(Func<bool> ephemeralJobsActive, Action killChildren, Func<bool> crashLogAllowed)
    {
        s_ephemeralJobsActive = ephemeralJobsActive;
        s_killChildren = killChildren;
        s_crashLogAllowed = crashLogAllowed;
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
    /// Ephemeral jobs active: killChildren, then TerminateProcess(GetCurrentProcess(), 1),
    /// with nothing written. Otherwise: append to crash.log when allowed, then show a
    /// MessageBox with the exception message when on the UI thread and let the runtime's
    /// default handling continue.
    /// </summary>
    public static void OnUnhandled(Exception exception)
    {
        if (EphemeralJobsActive())
        {
            EndWithoutReport();
            return;
        }

        var onUiThread = Environment.CurrentManagedThreadId == s_uiThreadId;
        TryAppendCrashLog(exception, onUiThread);

        if (!onUiThread
            || Interlocked.Exchange(ref s_showingMessage, 1) == 1)
        {
            return;
        }
        try
        {
            MessageBox.Show(
                // Install creates no Start menu entry; any Robo item starts the tray through COM.
                $"{AppInfo.Name} hit an unexpected error. If it stops responding, end it in Task Manager; "
                    + $"the next Robo-Copy, Robo-Cut or Robo-Paste starts it again.\n\n{exception.Message}",
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
    /// One <see cref="CrashLog.Format"/> entry appended to <see cref="AppPaths.CrashLogFile"/>,
    /// rotating to <see cref="AppPaths.RotatedCrashLogFile"/> first when
    /// <see cref="CrashLog.ShouldRotate"/> says so. Never throws and never waits long: it runs
    /// on a crashing thread, and the report must not become a second failure.
    /// </summary>
    private static void TryAppendCrashLog(Exception exception, bool onUiThread)
    {
        try
        {
            if (!(s_crashLogAllowed?.Invoke() ?? false)
                || Interlocked.Increment(ref s_crashLogEntries) > CrashLog.MaxEntriesPerRun)
            {
                return;
            }

            var entry = CrashLog.Format(new CrashFacts(
                TimeProvider.System.GetUtcNow(),
                HostEnvironment.Version,
                Environment.OSVersion.Version.ToString(),
                onUiThread,
                CrashLog.LayersOf(exception)));
            var bytes = Utf8NoBom.GetBytes(entry);

            if (!s_crashLogGate.TryEnter(CrashLogLockLimit))
            {
                return;
            }
            try
            {
                var paths = HostEnvironment.Paths;
                Directory.CreateDirectory(paths.DataDirectory);
                var existing = new FileInfo(paths.CrashLogFile);
                if (existing.Exists && CrashLog.ShouldRotate(existing.Length, bytes.Length))
                {
                    File.Move(paths.CrashLogFile, paths.RotatedCrashLogFile, overwrite: true);
                }
                using var stream = new FileStream(paths.CrashLogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                s_crashLogGate.Exit();
            }
        }
        catch (Exception)
        {
            // Best effort: a full disk or a locked file loses this entry, nothing else.
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
            // Bounded: the crashing thread may hold a lock the kill needs, and nothing may
            // stand between a crash and TerminateProcess (WER would otherwise snapshot memory).
            var kill = s_killChildren;
            if (kill is not null)
            {
                var killer = new Thread(() =>
                {
                    try
                    {
                        kill();
                    }
                    catch (Exception)
                    {
                        // Best effort; the kill-on-close job object ends them with the process.
                    }
                })
                { IsBackground = true };
                killer.Start();
                killer.Join(KillChildrenLimit);
            }
        }
        catch (Exception)
        {
            // Same: the job object covers it.
        }
        AppNative.TerminateProcess(AppNative.GetCurrentProcess(), 1);

        // Unreachable unless TerminateProcess failed. Exit still avoids WER.
        Environment.Exit(1);
    }
}
