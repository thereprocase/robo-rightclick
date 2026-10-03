using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.App;
using RoboRightClick.Com;
using RoboRightClick.Core;

namespace RoboRightClick.Verbs;

/// <summary>
/// Turns a Robo-Paste hotkey press (<see cref="PasteHotkey"/>) into the folder it meant, then
/// hands that folder to the same <see cref="IVerbHandler"/> path a right-click on the folder
/// background takes: the dispatcher's FIFO, <see cref="VerbRules.PasteDestinationRefusal"/>,
/// <see cref="PathPolicy"/>, the clipboard checks and the job manager.
/// </summary>
/// <remarks>
/// <para>One long-lived background thread in the multithreaded apartment, one press at a time:
/// calls into File Explorer are out-of-process and need no message pump here, and nothing can
/// re-enter. A press that arrives while one is in flight was taken by the hook and is ignored.</para>
/// <para>Per press, within <see cref="HotkeyDeadline.BudgetMs"/> of the key's own tick:
/// <list type="number">
/// <item>Desktop: SHGetKnownFolderPath(FOLDERID_Desktop, KF_FLAG_DONT_VERIFY), which never
/// waits on File Explorer.</item>
/// <item>Explorer: CoCreateInstance(CLSID_ShellWindows); for every entry
/// IServiceProvider::QueryService(SID_STopLevelBrowser, IShellBrowser) and GetWindow;
/// <see cref="TabMatch.Choose"/> with the tab captured at the press; the chosen window's
/// process must be %SystemRoot%\explorer.exe; QueryActiveShellView → IFolderView::GetFolder
/// (IShellItem) → GetAttributes and GetDisplayName(SIGDN_FILESYSPATH);
/// <see cref="PasteFolderRule"/>. LocationURL is not used: it is a URL, not the folder.</item>
/// <item><see cref="ClipboardGuard"/>: after a Ctrl+C or Ctrl+X made just before the hotkey,
/// wait for the clipboard to change, or refuse.</item>
/// </list>
/// When the budget runs out first, a watchdog marks the press abandoned, posts the
/// <see cref="VerbRefusal.ExplorerNotResponding"/> toast and cancels the outstanding call; a
/// late answer is dropped. Refusals go through <see cref="IVerbHandler.RefuseSelection"/>, so
/// toasts keep click order. Every proxy is released on this thread with
/// <see cref="ComNative.FinalRelease"/>.</para>
/// <para>Logs nothing about a press: the folder is a path, and ephemeral mode forbids it.</para>
/// </remarks>
internal sealed partial class ExplorerFolderLocator : IDisposable
{
    /// <summary>Bound on the ShellWindows entries read; File Explorer is another process and its count is not trusted.</summary>
    private const int MaxShellWindows = 512;

    private const int ClipboardPollMs = 20;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly PasteHotkey _hotkey;
    private readonly SynchronizationContext _ui;
    private readonly IVerbHandler _handler;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly Thread _thread;
    private uint _nativeThreadId;
    private bool _callCancellation;
    private bool _disposed;

    public ExplorerFolderLocator(PasteHotkey hotkey, SynchronizationContext ui, IVerbHandler handler)
    {
        _hotkey = hotkey;
        _ui = ui;
        _handler = handler;
        _thread = new Thread(Run) { IsBackground = true, Name = "Paste hotkey locator" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();

    /// <summary>
    /// Stops waiting for presses. A thread stuck in a call to a hung File Explorer is
    /// abandoned: it is a background thread and ends with the process.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Set();
        if (_thread.IsAlive && _thread.Join(TimeSpan.FromSeconds(1)))
        {
            _stop.Dispose();
        }
    }

    private void Run()
    {
        try
        {
            ComNative.CoInitializeEx(0, ComNative.COINIT_MULTITHREADED);
            _nativeThreadId = NativeMethods.GetCurrentThreadId();
            _callCancellation = ComNative.CoEnableCallCancellation(0) >= 0;
        }
        catch (Exception)
        {
            return;
        }

        WaitHandle[] waits = [_stop, _hotkey.Pressed];
        while (true)
        {
            try
            {
                if (WaitHandle.WaitAny(waits) == 0)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var press = _hotkey.TakePress();
            try
            {
                Handle(press);
            }
            catch (Exception)
            {
                // A press must never end in silence, and the loop must survive it.
                Post(handler => handler.RefuseSelection(ShellVerb.RoboPaste, VerbRefusal.Failed));
            }
            finally
            {
                _hotkey.ReleasePress();
            }
        }
    }

    /// <summary>Running → Done (the answer is posted) or Running → Abandoned (the watchdog won); never both.</summary>
    private sealed class Attempt
    {
        public const int Running = 0;
        public const int Done = 1;
        public const int Abandoned = 2;

        private int _state;

        public bool IsAbandoned => Volatile.Read(ref _state) == Abandoned;

        public bool TryFinish(int to) => Interlocked.CompareExchange(ref _state, to, Running) == Running;
    }

    private void Handle(HotkeyPress press)
    {
        var attempt = new Attempt();
        var remaining = HotkeyDeadline.Remaining(press.Time, NowTick());
        if (remaining == 0)
        {
            Finish(attempt, null, VerbRefusal.ExplorerNotResponding);
            return;
        }

        using var watchdog = new CancellationTokenSource();
        // Disposing the registration waits for a running callback, so its CoCancelCall can
        // never reach a call made for the next press.
        using (watchdog.Token.Register(() => Abandon(attempt)))
        {
            watchdog.CancelAfter(TimeSpan.FromMilliseconds(remaining));

            string? folder;
            VerbRefusal? refusal;
            try
            {
                (folder, refusal) = press.Desktop ? DesktopFolder() : ExplorerFolder(press, attempt);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                (folder, refusal) = (null, VerbRefusal.FolderNotIdentified);
            }
            catch (Exception)
            {
                // Through Finish, so an abandoned press still gets exactly one toast.
                (folder, refusal) = (null, VerbRefusal.Failed);
            }

            if (folder is not null && refusal is null)
            {
                refusal = WaitForClipboard(press, attempt);
            }
            Finish(attempt, refusal is null ? folder : null, refusal ?? (folder is null ? VerbRefusal.FolderNotIdentified : null));
        }
    }

    private void Finish(Attempt attempt, string? folder, VerbRefusal? refusal)
    {
        if (!attempt.TryFinish(Attempt.Done))
        {
            return; // abandoned: the toast is already queued and the late answer is dropped
        }
        if (folder is not null)
        {
            Post(handler => handler.Invoke(ShellVerb.RoboPaste, [folder], 0, null));
        }
        else
        {
            Post(handler => handler.RefuseSelection(ShellVerb.RoboPaste, refusal ?? VerbRefusal.FolderNotIdentified));
        }
    }

    /// <summary>On a thread-pool thread, when the budget ran out.</summary>
    private void Abandon(Attempt attempt)
    {
        if (!attempt.TryFinish(Attempt.Abandoned))
        {
            return;
        }
        Post(handler => handler.RefuseSelection(ShellVerb.RoboPaste, VerbRefusal.ExplorerNotResponding));
        if (_callCancellation)
        {
            // Unblocks a call into a hung File Explorer (it returns RPC_E_CALL_CANCELED), so the
            // next press is not stuck behind it. Best effort: nothing to cancel is fine too.
            ComNative.CoCancelCall(_nativeThreadId, 0);
        }
    }

    /// <summary>The same posting the COM layer gets: queued on the UI thread, behind earlier clicks.</summary>
    private void Post(Action<IVerbHandler> action)
    {
        try
        {
            _ui.Post(_ => action(_handler), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The tray is exiting; the press is dropped.
        }
    }

    private static uint NowTick() => unchecked((uint)Environment.TickCount);

    /// <summary>
    /// <see cref="ClipboardGuard"/>: polls the clipboard sequence number (no clipboard open,
    /// so it cannot get in File Explorer's way) until the copy or cut made just before the
    /// hotkey has landed, the guard's wait is over, or the press was abandoned.
    /// </summary>
    private static VerbRefusal? WaitForClipboard(in HotkeyPress press, Attempt attempt)
    {
        while (!attempt.IsAbandoned)
        {
            switch (ClipboardGuard.Decide(press, ClipboardNative.GetClipboardSequenceNumber(), NowTick()))
            {
                case ClipboardGuardAction.Proceed:
                    return null;
                case ClipboardGuardAction.Refuse:
                    return VerbRefusal.ClipboardNotReady;
                default:
                    Thread.Sleep(ClipboardPollMs);
                    break;
            }
        }
        return null; // abandoned: Finish drops whatever is returned
    }

    private static (string? Folder, VerbRefusal? Refusal) DesktopFolder()
    {
        var folderId = ShellWindowsConstants.FOLDERID_Desktop;
        if (ComNative.SHGetKnownFolderPath(in folderId, ShellWindowsConstants.KF_FLAG_DONT_VERIFY, 0, out var buffer) < 0 || buffer == 0)
        {
            if (buffer != 0)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
            return (null, VerbRefusal.DestinationNotFileSystem);
        }
        try
        {
            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(path) ? (null, VerbRefusal.DestinationNotFileSystem) : (path, null);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static (string? Folder, VerbRefusal? Refusal) ExplorerFolder(in HotkeyPress press, Attempt attempt)
    {
        using var scope = new ComScope();
        var clsid = ShellWindowsConstants.CLSID_ShellWindows;
        var iid = ShellWindowsConstants.IID_IShellWindows;
        if (ComNative.CoCreateInstance(in clsid, 0, ShellWindowsConstants.CLSCTX_SERVER, in iid, out var windowsPointer) < 0 || windowsPointer == 0)
        {
            return (null, VerbRefusal.FolderNotIdentified);
        }
        var windows = scope.Wrap<IShellWindows>(windowsPointer);
        if (windows.GetCount(out var count) < 0 || count < 0 || count > MaxShellWindows)
        {
            return (null, VerbRefusal.FolderNotIdentified);
        }

        var entries = new List<ShellWindowEntry>(count);
        var browsers = new List<IShellBrowser?>(count);
        for (var i = 0; i < count; i++)
        {
            if (attempt.IsAbandoned)
            {
                return (null, null);
            }
            var (entry, browser) = ReadEntry(windows, i, scope);
            entries.Add(entry);
            browsers.Add(browser);
        }

        if (TabMatch.Choose(press.Tab, press.Foreground, entries) is not { } chosen
            || browsers[chosen] is not { } shellBrowser
            || !IsSystemExplorer(entries[chosen].BrowserWindow))
        {
            return (null, VerbRefusal.FolderNotIdentified);
        }
        return attempt.IsAbandoned ? (null, null) : ReadFolder(shellBrowser, scope);
    }

    /// <summary>A default entry (no window) when any step fails: <see cref="TabMatch"/> treats it as unreadable.</summary>
    private static (ShellWindowEntry Entry, IShellBrowser? Browser) ReadEntry(IShellWindows windows, int index, ComScope scope)
    {
        try
        {
            if (windows.Item(new VariantInt32(index), out var dispatch) < 0 || dispatch == 0)
            {
                return (default, null);
            }
            scope.Keep(dispatch);
            var serviceIid = ShellWindowsConstants.IID_IServiceProvider;
            if (Marshal.QueryInterface(dispatch, in serviceIid, out var servicePointer) < 0 || servicePointer == 0)
            {
                return (default, null);
            }
            var services = scope.Wrap<IOleServiceProvider>(servicePointer);
            var sid = ShellWindowsConstants.SID_STopLevelBrowser;
            var browserIid = ShellWindowsConstants.IID_IShellBrowser;
            if (services.QueryService(in sid, in browserIid, out var browserPointer) < 0 || browserPointer == 0)
            {
                return (default, null);
            }
            var browser = scope.Wrap<IShellBrowser>(browserPointer);
            if (browser.GetWindow(out var window) < 0 || window == 0)
            {
                return (default, null);
            }
            return (new ShellWindowEntry(window, NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT)), browser);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (default, null);
        }
    }

    private static (string? Folder, VerbRefusal? Refusal) ReadFolder(IShellBrowser browser, ComScope scope)
    {
        if (browser.QueryActiveShellView(out var viewPointer) < 0 || viewPointer == 0)
        {
            return (null, VerbRefusal.FolderNotIdentified);
        }
        scope.Keep(viewPointer);
        var folderViewIid = ShellWindowsConstants.IID_IFolderView;
        if (Marshal.QueryInterface(viewPointer, in folderViewIid, out var folderViewPointer) < 0 || folderViewPointer == 0)
        {
            return (null, VerbRefusal.FolderNotIdentified);
        }
        var folderView = scope.Wrap<IFolderView>(folderViewPointer);
        var itemIid = ShellWindowsConstants.IID_IShellItem;
        if (folderView.GetFolder(in itemIid, out var itemPointer) < 0 || itemPointer == 0)
        {
            return (null, VerbRefusal.DestinationNotFileSystem);
        }
        var item = scope.Wrap<IShellItem>(itemPointer);

        // S_FALSE means "not every requested attribute is set"; the value is still valid.
        if (item.GetAttributes(PasteFolderRule.AttributeMask, out var attributes) < 0)
        {
            attributes = 0;
        }
        return PasteFolderRule.Check(attributes & PasteFolderRule.AttributeMask, ReadFileSystemPath(item));
    }

    private static string? ReadFileSystemPath(IShellItem item)
    {
        if (item.GetDisplayName(ShellConstants.SIGDN_FILESYSPATH, out var buffer) < 0 || buffer == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// ShellWindows lists the folder windows of whatever registered with it. Only File
    /// Explorer's own process may name the destination of a paste.
    /// </summary>
    private static unsafe bool IsSystemExplorer(nint window)
    {
        if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return false;
        }
        var process = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return false;
        }
        try
        {
            const int capacity = 1024;
            var buffer = stackalloc char[capacity];
            var length = (uint)capacity;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length) || length == 0 || length >= capacity)
            {
                return false;
            }
            var image = new string(buffer, 0, (int)length);
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return WinPath.AreSame(image, expected);
        }
        finally
        {
            ComNative.CloseHandle(process);
        }
    }

    /// <summary>
    /// Every COM reference taken for one press, released together on this thread: raw
    /// pointers with Marshal.Release, proxies with FinalRelease (created as unique instances,
    /// so the release happens now and not on the finalizer thread). A wrapped pointer is
    /// released by both, and that is balanced, not a double release: the proxy takes a
    /// reference of its own when it is created (StrategyBasedComWrappers' default strategy
    /// AddRefs the pointer), which FinalRelease gives back, while the reference the call
    /// returned to us stays ours to release. ComClient and ShellSelection do the same.
    /// </summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _proxies = [];
        private readonly List<nint> _pointers = [];

        public T Wrap<T>(nint pointer) where T : class
        {
            Keep(pointer);
            var proxy = ComNative.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            _proxies.Add(proxy);
            return (T)proxy;
        }

        public void Keep(nint pointer) => _pointers.Add(pointer);

        public void Dispose()
        {
            for (var i = _proxies.Count - 1; i >= 0; i--)
            {
                ComNative.FinalRelease(_proxies[i]);
            }
            for (var i = _pointers.Count - 1; i >= 0; i--)
            {
                if (_pointers[i] != 0)
                {
                    Marshal.Release(_pointers[i]);
                }
            }
        }
    }

    private static unsafe partial class NativeMethods
    {
        public const uint GA_ROOT = 2;

        [LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentThreadId();

        [LibraryImport("user32.dll")]
        public static partial nint GetAncestor(nint hwnd, uint gaFlags);

        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);
    }
}
