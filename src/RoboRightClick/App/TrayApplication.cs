using System.ComponentModel;
using System.Diagnostics;
using RoboRightClick.Com;
using RoboRightClick.Core;
using RoboRightClick.Jobs;
using RoboRightClick.Logging;
using RoboRightClick.UI;
using RoboRightClick.Verbs;

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
/// never snapshots memory holding job paths (Environment.FailFast would report to WER).
/// Otherwise, in normal mode with no ephemeral job this session, the exception is appended
/// to crash.log first.</para>
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
/// <para>Implementation notes. The NotifyIcon and Notifier are constructed (hidden) before
/// the VerbDispatcher, which needs the Notifier for refusal toasts; the icon becomes visible
/// only after registration. Gridline fonts load after SignalReady and before the menu and
/// any form, keeping the font copy off the path Explorer waits on. The decisions themselves
/// (start, exit, menu state, first run, which dialog a click raises) are Core's
/// <see cref="StartupRules"/>, tested on Linux.</para>
/// </remarks>
internal sealed class TrayApplication : ApplicationContext
{
    /// <summary>Windows may end the process soon after WM_ENDSESSION returns; cleanup gets this long.</summary>
    public static readonly TimeSpan SessionEndCancelLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A user-chosen exit waits longer: the loop keeps pumping and the icon stays, so a slow
    /// cleanup on a network share can finish instead of leaving partial files behind.
    /// </summary>
    public static readonly TimeSpan ExitCancelLimit = TimeSpan.FromSeconds(30);

    private const int ActiveRefreshIntervalMs = 1000;
    private const string ShutdownBlockReason = "Copying files…";

    private readonly SingleInstance _instance;
    private readonly bool _afterInstall;

    /// <summary>
    /// Read before the COM class objects are registered, so every job of this run is created
    /// at or after it; the interrupted-jobs check leaves those alone.
    /// </summary>
    private readonly DateTimeOffset _startedAt = TimeProvider.System.GetUtcNow();
    private readonly Action<JobManager?> _publishJobs;

    // Everything the tray creates, in creation order; teardown disposes it in reverse, so
    // windows go before the job manager, and the manager before the services its jobs use.
    private readonly List<IDisposable> _owned = [];

    private readonly WindowsFormsSynchronizationContext _ui;
    private readonly SettingsStore _settings;
    private readonly ClipboardService _clipboard;
    private readonly JobLogStore _logStore;
    private readonly UiPrompts _prompts;
    private readonly JobManager _jobs;
    private readonly NotifyIcon _icon;
    private readonly Notifier _notifier;
    private readonly ComServer _comServer;
    private readonly TrayIcons _trayIcons;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseAllItem;
    private readonly ToolStripMenuItem _ephemeralItem;
    private readonly ToolStripMenuItem _openLogsItem;
    private readonly JobsWindow _jobsWindow;
    private readonly ProgressWindowHost _progressWindows;
    private readonly SessionEndWindow _sessionWindow;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private SettingsWindow? _settingsWindow;
    private Font? _menuFont;
    private int _menuFontDpi;

    private TrayIconState? _shownIconState;
    private bool _shownEphemeral;
    private string? _shownTooltip;
    private IReadOnlyList<string> _toastedProblems = [];

    // Read by job worker threads (ClearClipboardIfUnchanged), so volatile.
    private volatile bool _shuttingDown;
    private Task? _cancelForExit;
    private bool _inSessionEnd;
    private bool _finished;
    private bool _tornDown;

    private TrayApplication(CliRunTray command, SingleInstance instance, Action<JobManager?> publishJobs)
    {
        StartedByCom = command.StartedByCom;
        _afterInstall = command.AfterInstall;
        _instance = instance;
        _publishJobs = publishJobs;
        try
        {
            // Before any COM object or window exists: CoInitializeSecurity can run only once,
            // and OLE initializes itself lazily the first time WinForms needs it.
            ComCallerSecurity.InitializeProcess(HostEnvironment.UserSid);

            _settings = Own(new SettingsStore(HostEnvironment.Paths));
            _settings.Load();

            // Installed now rather than by Application.Run: the job manager and dispatcher
            // capture it, and COM calls queued before the loop starts are delivered through it.
            _ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ui);

            _clipboard = Own(new ClipboardService());
            _logStore = new JobLogStore(HostEnvironment.Paths, () => _settings.Current.LogRetentionJobs);
            var sampler = Own(new ProgressSampler(TimeProvider.System, ProgressSampler.DefaultInterval));
            var fileSystem = new FileSystemFacts();
            _prompts = new UiPrompts(_ui, () => _progressWindows);
            _jobs = Own(new JobManager(
                _ui,
                () => _settings.Current,
                claims => new JobServices(_prompts, fileSystem, sampler, TimeProvider.System, claims, ClearClipboardIfUnchangedAsync),
                _logStore));
            _publishJobs(_jobs);

            _icon = Own(new NotifyIcon { Text = AppInfo.Name, Visible = false });
            // Owned after the icon, so it is disposed first: its batch timer must not fire
            // into a disposed NotifyIcon.
            _notifier = Own(new Notifier(_icon));
            var dispatcher = new VerbDispatcher(_ui, _clipboard, _jobs, _settings, _notifier);

            _comServer = Own(new ComServer(dispatcher));
            _comServer.Register();
            _instance.SignalReady();

            // Explorer's activation can proceed from here; the rest is the tray's own UI.
            Gridline.LoadFonts();
            _trayIcons = Own(new TrayIcons());
            _pauseAllItem = new ToolStripMenuItem("Pause all", null, (_, _) => TogglePauseAll());
            _ephemeralItem = new ToolStripMenuItem("Ephemeral mode", null, (_, _) => ToggleEphemeral());
            _openLogsItem = new ToolStripMenuItem("Open logs", null, (_, _) => OpenLogs());
            _menu = Own(BuildMenu());
            _icon.ContextMenuStrip = _menu;
            _jobsWindow = Own(new JobsWindow(_jobs, _logStore) { Prompts = _prompts });
            _progressWindows = Own(new ProgressWindowHost(_jobs, () => _settings.Current, _jobsWindow));
            _sessionWindow = Own(new SessionEndWindow(this));
            _refreshTimer = Own(new System.Windows.Forms.Timer { Interval = ActiveRefreshIntervalMs });

            Wire();
            RefreshTray();
            _icon.Visible = true;
            Post(AfterLoopStarted);
        }
        catch
        {
            Teardown();
            throw;
        }
    }

    /// <summary>Started by COM (-Embedding) rather than by the user or the Run key. The tray stays resident either way.</summary>
    public bool StartedByCom { get; }

    /// <summary>Runs the tray until exit. Returns the process exit code.</summary>
    /// <param name="command">How this tray was started: by COM, by the installer (first-run hint) or by the user.</param>
    /// <param name="publishJobs">
    /// Receives the job manager once it exists and null after teardown, for
    /// <see cref="CrashPolicy"/>, which is installed before the tray and asks it at crash time.
    /// </param>
    public static int Run(CliRunTray command, Action<JobManager?> publishJobs)
    {
        TrayApplication app;
        try
        {
            var instance = SingleInstance.TryAcquire();
            if (instance is null && command.StartedByCom)
            {
                // Another tray owns the session. COM launched this one because it did not see
                // a registered server: either the other tray has not registered yet (wait for
                // it), or it is shutting down (take over once it lets go of the mutex).
                instance = SingleInstance.WaitForReadyOrAcquire(SingleInstance.ReadyTimeout);
            }
            if (instance is null)
            {
                return CliExitCodes.Ok;
            }
            app = new TrayApplication(command, instance, publishJobs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{AppInfo.Name} could not start.\n\n{ex.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return CliExitCodes.Failed;
        }

        try
        {
            Application.Run(app);
        }
        finally
        {
            app.Dispose();
        }
        return CliExitCodes.Ok;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _finished = true;
            Teardown();
        }
        base.Dispose(disposing);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new Gridline.MenuRenderer() };
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Jobs…", null, (_, _) => ShowJobs()),
            new ToolStripSeparator(),
            _pauseAllItem,
            new ToolStripMenuItem("Resume all", null, (_, _) => ResumeAll()),
            new ToolStripSeparator(),
            _ephemeralItem,
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
            _openLogsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => RequestExit(requestedByOtherProcess: false)),
        ]);
        menu.Opening += (_, _) => UpdateMenu();
        return menu;
    }

    private void Wire()
    {
        _jobs.Created += (_, jobId) =>
        {
            if (!_finished)
            {
                _progressWindows.JobCreated(jobId);
            }
        };
        _jobs.StateChanged += (_, _) => RefreshTray();
        _jobs.Finished += (_, job) =>
        {
            if (!_finished)
            {
                _notifier.JobFinished(job, _settings.Current.NotifyOnComplete);
            }
            RefreshTray();
        };
        _settings.Changed += (_, _) => OnSettingsChanged();
        _notifier.Clicked += (_, target) => OnToastClicked(target);
        _instance.ExitRequested += (_, _) => Post(() => RequestExit(requestedByOtherProcess: true));
        _icon.MouseClick += (_, e) => OnTrayClicked(e.Button);
        _icon.MouseDoubleClick += (_, e) => OnTrayClicked(e.Button);
        _refreshTimer.Tick += (_, _) => RefreshTray();
    }

    /// <summary>Posted from the constructor, so it runs on the first pass of the message loop.</summary>
    private void AfterLoopStarted()
    {
        if (_shuttingDown)
        {
            return;
        }
        try
        {
            _settings.Watch(_ui);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Without the watcher, hand edits apply at the next start; nothing else depends on it.
        }
        var problemsShown = ToastSettingsProblemsIfNew();
        RunStartupChecks(_settings.Current.Logging, problemsShown);
    }

    /// <summary>
    /// The interrupted-jobs toast, the first-run hint and pruning. The disk reads run on the
    /// thread pool: the UI thread also serves every right-click.
    /// </summary>
    private async void RunStartupChecks(LoggingMode mode, bool settingsToastShown)
    {
        int interrupted;
        try
        {
            // Ephemeral mode writes nothing about jobs, so the check, which rewrites the job
            // logs it reports, runs in normal mode only.
            interrupted = mode == LoggingMode.Normal
                ? await Task.Run(() => _logStore.MarkInterrupted(_startedAt, _jobs.ActiveLogFolders, TimeProvider.System))
                : 0;
        }
        catch (Exception)
        {
            // Advisory checks: whatever fails here must not take the tray down (async void
            // would rethrow it into the crash policy).
            return;
        }
        if (_shuttingDown)
        {
            return;
        }

        switch (StartupRules.PickStartupToast(interrupted, settingsToastShown, _afterInstall))
        {
            case StartupToast.Interrupted:
                _notifier.Show(ToastText.ForInterrupted(interrupted), ToastTarget.Jobs);
                break;
            case StartupToast.TrayHint:
                _notifier.Show(ToastText.ForTrayHint(), ToastTarget.None);
                break;
        }

        if (mode == LoggingMode.Normal)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _logStore.Prune(_jobs.ActiveLogFolders());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Retention is retried after the next normal-mode job finishes.
                }
            });
        }
    }

    private void OnSettingsChanged()
    {
        if (_finished)
        {
            return;
        }
        // A raised maxConcurrentJobs starts waiting jobs now, not at the next state change.
        _jobs.SettingsChanged();
        RefreshTray();
        ToastSettingsProblemsIfNew();
    }

    /// <summary>One toast per distinct set of problems, so saving an unrelated field does not repeat it.</summary>
    private bool ToastSettingsProblemsIfNew()
    {
        var problems = _settings.LoadProblems;
        if (problems.Count == 0)
        {
            _toastedProblems = [];
            return false;
        }
        if (problems.SequenceEqual(_toastedProblems, StringComparer.Ordinal))
        {
            return false;
        }
        _toastedProblems = problems;
        _notifier.Show(ToastText.ForSettingsProblems(problems, _settings.SavesRefused), ToastTarget.Settings);
        return true;
    }

    private void RefreshTray()
    {
        if (_finished)
        {
            return;
        }
        var snapshots = _jobs.Snapshots();
        var status = TrayStatus.Derive(snapshots, _settings.Current.Logging);
        if (_shownIconState != status.Icon || _shownEphemeral != status.Ephemeral)
        {
            _icon.Icon = _trayIcons.For(status.Icon, status.Ephemeral);
            _shownIconState = status.Icon;
            _shownEphemeral = status.Ephemeral;
        }

        // Setting the text re-sends the whole icon to the shell; skip it when nothing changed.
        if (_shownTooltip != status.Tooltip)
        {
            _icon.Text = status.Tooltip;
            _shownTooltip = status.Tooltip;
        }

        // Speed and ETA change without state events; the timer only runs while they can.
        _refreshTimer.Enabled = snapshots.Any(j => !JobStates.IsTerminal(j.State));
    }

    private void UpdateMenu()
    {
        var dpi = _menu.DeviceDpi;
        if (_menuFont is null || dpi != _menuFontDpi)
        {
            // Pixel-sized, so it is rebuilt for the DPI of the monitor the menu opens on.
            var previous = _menuFont;
            _menuFont = Gridline.Sans(Gridline.Scale(_menu, (int)Gridline.SizeUi));
            _menuFontDpi = dpi;
            _menu.Font = _menuFont;
            previous?.Dispose();
        }

        var state = TrayMenu.For(_settings.Current.Logging, _jobs.PauseAllActive);
        _pauseAllItem.Checked = state.PauseAllChecked;
        _ephemeralItem.Checked = state.EphemeralChecked;
        _openLogsItem.Visible = state.OpenLogsVisible;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.Enabled = !_shuttingDown;
        }
    }

    private void OnTrayClicked(MouseButtons button)
    {
        if (button != MouseButtons.Left || _shuttingDown)
        {
            return;
        }
        if (StartupRules.ConflictToFront(_jobs.Snapshots()) is { } waiting && _prompts.Activate(waiting))
        {
            return;
        }
        _jobsWindow.ShowJobs();
    }

    private void OnToastClicked(ToastTarget target)
    {
        if (_shuttingDown)
        {
            return;
        }
        switch (target)
        {
            case ToastTarget.Jobs:
                _jobsWindow.ShowJobs(attentionOnly: true);
                break;
            case ToastTarget.Settings:
                ShowSettings();
                break;
        }
    }

    private void ShowJobs() => _jobsWindow.ShowJobs();

    private void ShowSettings()
    {
        if (_settingsWindow is null || _settingsWindow.IsDisposed)
        {
            _settingsWindow = new SettingsWindow(_settings, _logStore, _jobs);
        }
        _settingsWindow.ShowSettings();
    }

    /// <summary>A checked "Pause all" turns it off again; leaving the click inert would read as broken.</summary>
    private void TogglePauseAll()
    {
        if (_jobs.PauseAllActive)
        {
            _jobs.ResumeAll();
        }
        else
        {
            _jobs.PauseAll();
        }
        RefreshTray();
    }

    private void ResumeAll()
    {
        _jobs.ResumeAll();
        RefreshTray();
    }

    private async void ToggleEphemeral()
    {
        var before = _settings.Current;
        var to = before.Logging == LoggingMode.Ephemeral ? LoggingMode.Normal : LoggingMode.Ephemeral;
        if (!await TrySaveSettingsAsync(before with { Logging = to }))
        {
            return;
        }
        if (StartupRules.ModeChangeNeedsNotice(to, _jobs.Snapshots()))
        {
            _notifier.Show(ToastText.ForModeAppliesToNewJobs(to));
        }
        if (to != LoggingMode.Ephemeral)
        {
            return;
        }

        try
        {
            var count = await Task.Run(_logStore.CountJobFolders);
            if (_shuttingDown || !StartupRules.ShouldOfferLogDeletion(before.Logging, to, count) || !AskDeleteLogs(count))
            {
                return;
            }
            await Task.Run(() => _logStore.DeleteAll(_jobs.ActiveLogFolders()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Some job logs could not be deleted.\n\n{ex.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task<bool> TrySaveSettingsAsync(Settings settings)
    {
        try
        {
            await _settings.SaveAsync(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(
                $"The setting could not be saved.\n\n{ex.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>Keep is the default: when in doubt, keep data.</summary>
    private static bool AskDeleteLogs(int count) =>
        Gridline.Confirm(
            owner: null,
            "Ephemeral mode is on",
            count == 1
                ? "New jobs write nothing to disk. Delete the existing job log too?"
                : $"New jobs write nothing to disk. Delete the {count:N0} existing job logs too?",
            "Delete",
            "DeleteLogs",
            "Keep",
            "KeepLogs");

    private void OpenLogs()
    {
        // The menu item is hidden in ephemeral mode, but the mode may have changed since the
        // menu opened (a hand edit), and opening the folder would create it.
        if (_settings.Current.Logging != LoggingMode.Normal)
        {
            return;
        }
        var folder = _settings.Paths.DataDirectory;
        try
        {
            Directory.CreateDirectory(folder);

            // By absolute path, like every process the app starts.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(
                $"The log folder could not be opened.\n\n{ex.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async void RequestExit(bool requestedByOtherProcess)
    {
        if (_cancelForExit is not null)
        {
            return;
        }
        switch (StartupRules.ExitDecision(_jobs.HasActiveJobs, requestedByOtherProcess))
        {
            case ExitAction.RefuseWithToast:
                _notifier.Show(ToastText.ForExitRefused());
                return;
            case ExitAction.ConfirmWithUser:
                // The dialog pumps messages; a session end may have started exiting meanwhile.
                if (!ConfirmExit() || _cancelForExit is not null)
                {
                    return;
                }
                break;
        }
        await BeginCancelForExit(ExitCancelLimit);
        FinishExit();
    }

    /// <summary>Keep running is the default, so Enter or Escape never cancels a paste.</summary>
    private bool ConfirmExit()
    {
        var active = _jobs.Snapshots().Count(j => !JobStates.IsTerminal(j.State));
        return Gridline.Confirm(
            owner: null,
            active == 1 ? "1 job is still running" : $"{active} jobs are still running",
            "Exiting cancels them, as Cancel does in the Jobs window.",
            "Exit",
            "ConfirmExit",
            "Keep running",
            "KeepRunning");
    }

    /// <summary>Shared by Exit and session end; whichever comes first starts it.</summary>
    private Task BeginCancelForExit(TimeSpan timeout) => _cancelForExit ??= CancelForExitAsync(timeout);

    /// <summary>
    /// Revoke first, so a right-click during shutdown is not routed to a server that is going
    /// away. Then close the conflict questions (each open one answers "cancel", and no new
    /// one appears while jobs wind down), then cancel with the loop pumping, because jobs in
    /// Finalizing post to it.
    /// </summary>
    private async Task CancelForExitAsync(TimeSpan timeout)
    {
        _shuttingDown = true;
        RevokeComServer();
        try
        {
            _prompts.Shutdown();
        }
        catch (Exception)
        {
            // Nothing here may keep the process alive; the cancel below ends those jobs anyway.
        }
        try
        {
            await _jobs.CancelAllAndWaitAsync(timeout);
        }
        catch (Exception)
        {
            // Exit must complete whatever the cancel did; cleanup that did not finish is
            // reported at the next start through job.json (normal mode).
        }
    }

    /// <summary>
    /// Never throws: the session-end pump waits on the task this runs in, inside a window
    /// procedure. Clears the ready event too, so an -Embedding start from here on waits to
    /// take over instead of exiting into a failed activation (<see cref="SingleInstance.WaitForReadyOrAcquire"/>).
    /// </summary>
    private void RevokeComServer()
    {
        if (_owned.Remove(_comServer))
        {
            DisposeQuietly(_comServer);
        }
        try
        {
            _instance.ClearReady();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }

    private void FinishExit()
    {
        // Never inside the session-end pump: teardown destroys the window whose message is
        // still being handled. OnEndSession posts this again when it returns.
        if (_finished || _inSessionEnd)
        {
            return;
        }
        _finished = true;
        _icon.Visible = false;
        Teardown();
        ExitThread();
    }

    /// <summary>WM_QUERYENDSESSION: false vetoes, with a reason Windows shows on its "apps are preventing shutdown" screen.</summary>
    private bool OnQueryEndSession(nint hwnd)
    {
        if (!_shuttingDown && _jobs.HasActiveJobs)
        {
            AppNative.ShutdownBlockReasonCreate(hwnd, ShutdownBlockReason);
            return false;
        }
        AppNative.ShutdownBlockReasonDestroy(hwnd);
        return true;
    }

    /// <summary>
    /// WM_ENDSESSION. When the session really ends, Windows may terminate the process as soon
    /// as this returns, so cancel cleanup is waited for here, pumping messages (jobs in
    /// Finalizing post to this thread), for at most <see cref="SessionEndCancelLimit"/>.
    /// </summary>
    private void OnEndSession(nint hwnd, bool ending)
    {
        AppNative.ShutdownBlockReasonDestroy(hwnd);
        if (!ending || _inSessionEnd)
        {
            return;
        }
        _inSessionEnd = true;
        try
        {
            var cancel = BeginCancelForExit(SessionEndCancelLimit);

            // A hard stop slightly past the cancel limit, in case the wait itself overruns.
            var deadline = Environment.TickCount64 + (long)SessionEndCancelLimit.TotalMilliseconds + 500;
            while (!cancel.IsCompleted && Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                cancel.Wait(TimeSpan.FromMilliseconds(15));
            }
            _icon.Visible = false;
        }
        finally
        {
            _inSessionEnd = false;
        }
        Post(FinishExit);
    }

    /// <summary>
    /// Job threads call this through <see cref="JobServices.ClearClipboardIfUnchanged"/>. The
    /// task always completes: a job in Finalizing waits on it, and keeping the clipboard is
    /// the safe outcome of any failure (the user can still paste or replace it).
    /// </summary>
    private Task<bool> ClearClipboardIfUnchangedAsync(uint sequenceNumber)
    {
        if (_shuttingDown)
        {
            return Task.FromResult(false);
        }
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = Post(async () =>
        {
            try
            {
                result.TrySetResult(!_shuttingDown && await _clipboard.ClearIfUnchangedAsync(sequenceNumber));
            }
            catch (Exception)
            {
                result.TrySetResult(false);
            }
        });
        if (!posted)
        {
            result.TrySetResult(false);
        }
        return result.Task;
    }

    /// <summary>False once the UI thread's marshaling window is gone (after exit).</summary>
    private bool Post(Action action)
    {
        try
        {
            _ui.Post(_ => action(), null);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes everything in reverse creation order, then releases the mutex last, so a new
    /// instance can start only once this one has let go of COM, the clipboard and its icon.
    /// Runs once; each step is isolated so one failure cannot keep the mutex held.
    /// </summary>
    private void Teardown()
    {
        if (_tornDown)
        {
            return;
        }
        _tornDown = true;
        _shuttingDown = true;

        DisposeQuietly(_settingsWindow);
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            DisposeQuietly(_owned[i]);
        }
        _owned.Clear();
        DisposeQuietly(_menuFont);

        _publishJobs(null);
        DisposeQuietly(_instance);
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown continues; the process is ending and the OS reclaims what is left.
        }
    }

    /// <summary>
    /// Receives the session-end messages. A hidden top-level window, not a message-only one:
    /// message-only windows do not receive the WM_QUERYENDSESSION broadcast.
    /// </summary>
    private sealed class SessionEndWindow : NativeWindow, IDisposable
    {
        private readonly TrayApplication _owner;

        public SessionEndWindow(TrayApplication owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = AppInfo.Name });
        }

        public void Dispose() => DestroyHandle();

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case AppNative.WM_QUERYENDSESSION:
                    m.Result = _owner.OnQueryEndSession(Handle) ? 1 : 0;
                    return;
                case AppNative.WM_ENDSESSION:
                    _owner.OnEndSession(Handle, m.WParam != 0);
                    m.Result = 0;
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }
    }
}
