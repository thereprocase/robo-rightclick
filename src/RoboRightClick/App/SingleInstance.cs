using System.Security.AccessControl;
using System.Security.Principal;

namespace RoboRightClick.App;

/// <summary>
/// One tray per user session. Named kernel objects in the session-local namespace, each
/// created with an explicit security descriptor granting only the current user (and
/// SYSTEM), so another user's process in the session cannot squat or signal them:
/// a mutex held for the tray's lifetime, an event that asks the running tray to exit
/// (--install for upgrades, --uninstall), and a manual-reset "ready" event set once the
/// COM class objects are registered.
/// </summary>
/// <remarks>
/// A tray started by COM with -Embedding that finds the mutex taken (a Run-key start won
/// the race) waits up to <see cref="ReadyTimeout"/> for the ready event before exiting 0,
/// so COM's activation reaches a registered server instead of failing with "server
/// execution failed".
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\RoboRightClick.Instance";
    public const string ExitEventName = @"Local\RoboRightClick.ExitRequest";
    public const string ReadyEventName = @"Local\RoboRightClick.Ready";

    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _exitEvent;
    private readonly EventWaitHandle _readyEvent;
    private readonly object _listenerLock = new();
    private EventHandler? _exitRequested;
    private RegisteredWaitHandle? _exitWait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle exitEvent, EventWaitHandle readyEvent)
    {
        _mutex = mutex;
        _exitEvent = exitEvent;
        _readyEvent = readyEvent;
    }

    /// <summary>
    /// The instance guard, or null when another tray already owns the mutex (this process should exit 0).
    /// Must run on the thread that will later call <see cref="Dispose"/>: a mutex is released by its owner.
    /// </summary>
    /// <exception cref="InvalidOperationException">The names exist but belong to another account.</exception>
    public static SingleInstance? TryAcquire()
    {
        Mutex? mutex = null;
        EventWaitHandle? exitEvent = null;
        EventWaitHandle? readyEvent = null;
        try
        {
            mutex = MutexAcl.Create(true, MutexName, out var createdNew, MutexSecurityForCurrentUser());
            if (!createdNew)
            {
                mutex.Dispose();
                mutex = null;
                return null;
            }

            exitEvent = EventWaitHandleAcl.Create(
                false, EventResetMode.AutoReset, ExitEventName, out _, EventSecurityForCurrentUser());
            readyEvent = EventWaitHandleAcl.Create(
                false, EventResetMode.ManualReset, ReadyEventName, out _, EventSecurityForCurrentUser());

            var instance = new SingleInstance(mutex, exitEvent, readyEvent);
            mutex = null;
            exitEvent = null;
            readyEvent = null;
            return instance;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The name exists with a descriptor that excludes this user: a different
            // account's process holds it. Failing loudly beats a tray that silently never starts.
            throw new InvalidOperationException("Another account in this session owns RoboRightClick's instance objects.", ex);
        }
        finally
        {
            if (mutex is not null)
            {
                // Only reached on an error after creation: this thread owns it, so give it up.
                TryRelease(mutex);
                mutex.Dispose();
            }
            exitEvent?.Dispose();
            readyEvent?.Dispose();
        }
    }

    /// <summary>Waits for the running tray's ready event; false on timeout.</summary>
    public static bool WaitForReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // The owner creates the mutex first and the event a moment later, so a missing
            // event is polled for rather than treated as failure.
            if (EventWaitHandleAcl.TryOpenExisting(ReadyEventName, EventWaitHandleRights.Synchronize, out var ready))
            {
                using (ready)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    return remaining > TimeSpan.Zero ? ready.WaitOne(remaining) : ready.WaitOne(0);
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// Signals the exit event and waits up to <paramref name="timeout"/> for the mutex to
    /// be released. False if a tray is still running (for example it refused because jobs
    /// are active).
    /// </summary>
    public static bool RequestExitAndWait(TimeSpan timeout)
    {
        // No mutex, no tray: nothing to stop.
        if (!MutexAcl.TryOpenExisting(MutexName, MutexRights.Synchronize, out var running))
        {
            return true;
        }

        using (running)
        {
            if (EventWaitHandleAcl.TryOpenExisting(ExitEventName, EventWaitHandleRights.Modify, out var exit))
            {
                using (exit)
                {
                    exit.Set();
                }
            }

            try
            {
                if (!running.WaitOne(timeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The tray died holding it; the wait still acquired it, which is "gone".
            }
            TryRelease(running);
            return true;
        }
    }

    /// <summary>Sets the ready event: the class objects are registered and resumed.</summary>
    public void SignalReady() => _readyEvent.Set();

    /// <summary>
    /// Raised on a thread-pool thread when another process signals the exit event; handlers
    /// marshal to the UI thread. Listening starts with the first subscriber, so a request
    /// cannot be consumed before anyone is there to handle it.
    /// </summary>
    public event EventHandler? ExitRequested
    {
        add
        {
            lock (_listenerLock)
            {
                _exitRequested += value;
                if (_exitWait is null && !_disposed)
                {
                    _exitWait = ThreadPool.RegisterWaitForSingleObject(
                        _exitEvent, (_, _) => OnExitRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
                }
            }
        }
        remove
        {
            lock (_listenerLock)
            {
                _exitRequested -= value;
            }
        }
    }

    public void Dispose()
    {
        lock (_listenerLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _exitWait?.Unregister(null);
            _exitWait = null;
        }

        _readyEvent.Dispose();
        _exitEvent.Dispose();
        TryRelease(_mutex);
        _mutex.Dispose();
    }

    private void OnExitRequested() => _exitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// ReleaseMutex throws when the calling thread does not own the mutex. At shutdown that
    /// is not worth failing over: closing the handle ends the process's ownership anyway.
    /// </summary>
    private static void TryRelease(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
    }

    private static MutexSecurity MutexSecurityForCurrentUser()
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids())
        {
            security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    private static EventWaitHandleSecurity EventSecurityForCurrentUser()
    {
        var security = new EventWaitHandleSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids())
        {
            security.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>The current user and SYSTEM, both in-box identities. Nobody else is granted anything.</summary>
    private static IEnumerable<SecurityIdentifier> AllowedSids()
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user's SID is unavailable.");
        return [user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)];
    }
}
