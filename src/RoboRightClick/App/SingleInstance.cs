namespace RoboRightClick.App;

/// <summary>
/// One tray per user session. Named kernel objects in the session-local namespace:
/// a mutex held for the tray's lifetime, and an event that asks the running tray to exit
/// (used by --install for upgrades and by --uninstall).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\RoboRightClick.Instance";
    public const string ExitEventName = @"Local\RoboRightClick.ExitRequest";

    private SingleInstance()
    {
    }

    /// <summary>The instance guard, or null when another tray already owns the mutex (this process should exit 0).</summary>
    public static SingleInstance? TryAcquire() => throw new NotImplementedException();

    /// <summary>
    /// Signals the exit event and waits up to <paramref name="timeout"/> for the mutex to
    /// be released. False if a tray is still running (for example it refused because jobs
    /// are active).
    /// </summary>
    public static bool RequestExitAndWait(TimeSpan timeout) => throw new NotImplementedException();

    /// <summary>Raised on a thread-pool thread when another process signals the exit event.</summary>
    public event EventHandler? ExitRequested;

    public void Dispose()
    {
    }

    private void OnExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
