using RoboRightClick.Core;

namespace RoboRightClick.App;

/// <summary>
/// config.json in memory and on disk. <see cref="Current"/> is an immutable record
/// swapped atomically, so job creation can read it from any thread. Loading never
/// fails: bad fields fall back per field (<see cref="SettingsSerializer.Parse"/>) and
/// the problems are kept for one tray warning.
/// </summary>
internal sealed class SettingsStore
{
    public SettingsStore(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public Settings Current => throw new NotImplementedException();

    /// <summary>Problems from the last load; shown once via <see cref="ToastText.ForSettingsProblems"/>.</summary>
    public IReadOnlyList<string> LoadProblems => throw new NotImplementedException();

    /// <summary>Raised on the thread that called <see cref="Save"/> after <see cref="Current"/> changes.</summary>
    public event EventHandler<Settings>? Changed;

    /// <summary>Reads config.json; a missing file means defaults and is not created here (install writes it).</summary>
    public void Load() => throw new NotImplementedException();

    /// <summary>
    /// Writes via a temp file in the same folder and File.Replace/Move, so a crash never
    /// leaves half a config. This is the only file ephemeral mode writes. Also applies
    /// startWithWindows to the Run value through <see cref="Install.RegistryWriter"/>.
    /// </summary>
    public void Save(Settings settings) => throw new NotImplementedException();

    private void OnChanged(Settings settings) => Changed?.Invoke(this, settings);
}
