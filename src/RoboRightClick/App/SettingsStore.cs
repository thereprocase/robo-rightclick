using System.Text;
using RoboRightClick.Core;
using RoboRightClick.Install;

namespace RoboRightClick.App;

/// <summary>
/// config.json in memory and on disk. <see cref="Current"/> is an immutable record
/// swapped atomically, so job creation can read it from any thread. Loading never
/// fails: bad fields fall back per field (<see cref="SettingsSerializer.Parse"/>) and
/// the problems are kept for one tray warning (<see cref="ToastText.ForSettingsProblems"/>).
/// </summary>
/// <remarks>
/// Edits made by hand ("Open config file") are picked up by a FileSystemWatcher on the
/// config folder, debounced by about 500 ms, and reloaded. If the last load found the file
/// unreadable as JSON, the first save copies it to config.json.bad first, so a typo never
/// silently costs the user their whole file. The config folder is the only place ephemeral
/// mode writes. A file written by a newer version (<see cref="SettingsLoadResult.WrittenByNewerVersion"/>)
/// is loaded for the settings this version knows and never saved over: every save first
/// re-reads the file and refuses with <see cref="NewerConfigException"/>.
/// </remarks>
internal sealed class SettingsStore : IDisposable
{
    public const string BadCopySuffix = ".bad";

    /// <summary>Fixed name, so a crash between write and replace leaves at most one stray file.</summary>
    public const string TempSuffix = AppPaths.TempSuffix;

    /// <summary>Editors save in several steps (truncate, write, rename); wait for them to settle.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Reads that fail (an editor still holds the file) are retried this many times, one debounce apart.</summary>
    private const int MaxReadAttempts = 5;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Guards everything below that the watcher's timer thread and the UI thread share.
    private readonly Lock _gate = new();

    private volatile Settings _current = Settings.Default;
    private volatile IReadOnlyList<string> _loadProblems = [];

    // The text this process last loaded or saved. The watcher compares against it to ignore
    // the app's own writes and touches that change nothing.
    private string? _lastKnownText;

    // Bumped on every load, reload and save. A reload read before a save is dropped instead
    // of overwriting the newer settings in memory.
    private int _generation;
    private bool _lastLoadUnreadable;
    private bool _lastLoadNewer;
    private int _readAttempts;

    private SynchronizationContext? _ui;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private bool _disposed;

    public SettingsStore(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public Settings Current => _current;

    /// <summary>Problems from the last load.</summary>
    public IReadOnlyList<string> LoadProblems => _loadProblems;

    /// <summary>Raised on the UI thread after <see cref="Current"/> changes (save or external edit).</summary>
    public event EventHandler<Settings>? Changed;

    /// <summary>Reads config.json; a missing file means defaults and is not created here (install writes it).</summary>
    public void Load()
    {
        var text = TryReadConfigText(out var readFailed);
        lock (_gate)
        {
            _generation++;
            Apply(text);
            if (readFailed)
            {
                // A file that exists but could not be read is treated like one that could not
                // be parsed: defaults for now, and a .bad copy before anything overwrites it.
                _lastLoadUnreadable = true;
                _loadProblems = ["config could not be read; using defaults"];
            }
        }
    }

    /// <summary>Starts watching for external edits; events are posted to <paramref name="ui"/>.</summary>
    public void Watch(SynchronizationContext ui)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ui = ui;
            _debounceTimer ??= new System.Threading.Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
            StartWatcherIfPossible();
        }
    }

    /// <summary>
    /// Writes via a temp file in the same folder and File.Replace/Move, so a crash never
    /// leaves half a config. Also applies startWithWindows to the Run value through
    /// <see cref="Install.RegistryWriter.SetStartWithWindows"/>. The watcher ignores the
    /// app's own write.
    /// </summary>
    /// <exception cref="NewerConfigException">
    /// config.json was written by a newer version (known from the last load, or found on disk
    /// now): nothing is written and <see cref="Current"/> is unchanged. It is an IOException,
    /// so every caller's existing save-failure message shows it.
    /// </exception>
    /// <exception cref="IOException">The file could not be written; <see cref="Current"/> is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">As above.</exception>
    public void Save(Settings settings)
    {
        var text = SettingsSerializer.Serialize(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Checked against the file as it is now: a newer version may have written it since
            // the last load, and a user may have deleted a newer file to start over. Only when
            // it cannot be read does the last load decide.
            var onDisk = TryReadConfigText(out var readFailed);
            if (readFailed ? _lastLoadNewer : !SettingsSerializer.MayOverwrite(onDisk))
            {
                throw new NewerConfigException();
            }
            Directory.CreateDirectory(Paths.ConfigDirectory);
            if (_lastLoadUnreadable && File.Exists(Paths.ConfigFile))
            {
                File.Copy(Paths.ConfigFile, Paths.ConfigFile + BadCopySuffix, overwrite: true);
            }
            WriteAtomically(text);

            _generation++;
            _lastKnownText = text;
            _lastLoadUnreadable = false;
            _lastLoadNewer = false;
            _current = settings;
            _loadProblems = [];

            // The folder may only now exist (first save on a machine that never installed).
            if (_ui is not null)
            {
                StartWatcherIfPossible();
            }
        }

        try
        {
            // The Run value always names the installed exe: that is the one the Run key may start.
            RegistryWriter.SetStartWithWindows(settings.StartWithWindows, Paths.InstalledExe);
        }
        finally
        {
            // Raised even when the registry write failed: config.json and Current did change.
            RaiseChanged(settings);
        }
    }

    /// <summary>
    /// <see cref="Save"/> on the thread pool, for callers on the UI thread: %APPDATA% can be
    /// redirected to a network share, and that thread also serves every right-click.
    /// <see cref="Changed"/> still arrives on the UI thread. Exceptions as for Save.
    /// </summary>
    public Task SaveAsync(Settings settings) => Task.Run(() => Save(settings));

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            watcher = _watcher;
            timer = _debounceTimer;
            _watcher = null;
            _debounceTimer = null;
        }
        watcher?.Dispose();
        timer?.Dispose();
    }

    private void OnChanged(Settings settings) => Changed?.Invoke(this, settings);

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Apply(string? text)
    {
        _lastKnownText = text;
        if (text is null)
        {
            _lastLoadUnreadable = false;
            _lastLoadNewer = false;
            _current = Settings.Default;
            _loadProblems = [];
            return;
        }
        var result = SettingsSerializer.Parse(text);
        _lastLoadUnreadable = result.Unreadable;
        _lastLoadNewer = result.WrittenByNewerVersion;
        _current = result.Settings;
        _loadProblems = result.Problems;
    }

    private void WriteAtomically(string text)
    {
        var temp = Paths.ConfigFile + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Utf8NoBom.GetBytes(text);
            stream.Write(bytes);

            // On disk before the rename, or a power cut could leave a renamed but empty file.
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(Paths.ConfigFile))
        {
            File.Replace(temp, Paths.ConfigFile, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, Paths.ConfigFile);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Without the folder there is nothing to watch yet; <see cref="Save"/> retries.</summary>
    private void StartWatcherIfPossible()
    {
        if (_watcher is not null || _disposed || !Directory.Exists(Paths.ConfigDirectory))
        {
            return;
        }
        var watcher = new FileSystemWatcher(Paths.ConfigDirectory, AppPaths.ConfigFileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => ScheduleReload();
        watcher.Created += (_, _) => ScheduleReload();
        watcher.Renamed += (_, _) => ScheduleReload();

        // A buffer overflow loses events, not changes: reload and compare.
        watcher.Error += (_, _) => ScheduleReload();
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            _readAttempts = 0;
            _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>On a timer thread: read and parse here, so the UI thread never waits on the disk.</summary>
    private void OnDebounceElapsed()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            generation = _generation;
        }

        var text = TryReadConfigText(out var readFailed);
        if (readFailed)
        {
            lock (_gate)
            {
                if (!_disposed && ++_readAttempts < MaxReadAttempts)
                {
                    _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
                }
            }
            return;
        }

        // A deleted file keeps the settings in memory: editors that save by delete-and-rename
        // pass through this state, and the next start reads whatever is there then.
        if (text is null)
        {
            return;
        }

        SynchronizationContext? ui;
        lock (_gate)
        {
            if (_disposed || text == _lastKnownText)
            {
                return;
            }
            ui = _ui;
        }
        ui?.Post(_ => ApplyReload(text, generation), null);
    }

    /// <summary>On the UI thread, so <see cref="Changed"/> fires there and never overlaps a save.</summary>
    private void ApplyReload(string text, int generationAtRead)
    {
        Settings current;
        lock (_gate)
        {
            if (_disposed || _generation != generationAtRead || text == _lastKnownText)
            {
                return;
            }
            _generation++;
            Apply(text);
            current = _current;
        }
        OnChanged(current);
    }

    private void RaiseChanged(Settings settings)
    {
        var ui = _ui;
        if (ui is null || SynchronizationContext.Current == ui)
        {
            OnChanged(settings);
        }
        else
        {
            ui.Post(_ => OnChanged(settings), null);
        }
    }

    /// <summary>The file's text, or null when it does not exist. <paramref name="readFailed"/> when it exists but could not be read.</summary>
    private string? TryReadConfigText(out bool readFailed)
    {
        readFailed = false;
        try
        {
            return File.ReadAllText(Paths.ConfigFile, Utf8NoBom);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Loading never fails: an unreadable file at startup means defaults for now.
            readFailed = true;
            return null;
        }
    }
}

/// <summary>
/// A save refused because config.json comes from a newer version. Derived from IOException
/// so the tray's and the Settings window's save-failure handling show its message as is.
/// </summary>
internal sealed class NewerConfigException : IOException
{
    public NewerConfigException()
        : base(SettingsSerializer.NewerVersionSaveRefusal)
    {
    }
}
