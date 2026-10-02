using RoboRightClick.Core;

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
/// mode writes.
/// </remarks>
internal sealed class SettingsStore : IDisposable
{
    public const string BadCopySuffix = ".bad";

    public SettingsStore(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public Settings Current => throw new NotImplementedException();

    /// <summary>Problems from the last load.</summary>
    public IReadOnlyList<string> LoadProblems => throw new NotImplementedException();

    /// <summary>Raised on the UI thread after <see cref="Current"/> changes (save or external edit).</summary>
    public event EventHandler<Settings>? Changed;

    /// <summary>Reads config.json; a missing file means defaults and is not created here (install writes it).</summary>
    public void Load() => throw new NotImplementedException();

    /// <summary>Starts watching for external edits; events are posted to <paramref name="ui"/>.</summary>
    public void Watch(SynchronizationContext ui) => throw new NotImplementedException();

    /// <summary>
    /// Writes via a temp file in the same folder and File.Replace/Move, so a crash never
    /// leaves half a config. Also applies startWithWindows to the Run value through
    /// <see cref="Install.RegistryWriter.SetStartWithWindows"/>. The watcher ignores the
    /// app's own write.
    /// </summary>
    public void Save(Settings settings) => throw new NotImplementedException();

    public void Dispose()
    {
    }

    private void OnChanged(Settings settings) => Changed?.Invoke(this, settings);
}
