using RoboRightClick.App;

namespace RoboRightClick.UI;

/// <summary>
/// One page mapping every config.json field: threads, retries, retry wait, conflict
/// default, max concurrent jobs (0 = unlimited), logging mode (labeled "Ephemeral: write
/// nothing about new jobs to disk"), log retention, start with Windows, notify on complete,
/// show progress window, extra args for copy and move (with the allowed switches listed).
/// Values are checked by round-tripping through <see cref="Core.SettingsSerializer.Parse"/>;
/// any problem is shown next to the field and Save stays disabled. Opened with a field
/// name highlights that field (settings-problem toast click). Also: "Open config file",
/// "Delete all job logs" (asks first; JobLogStore.DeleteAll), and the version. Saving goes
/// through <see cref="SettingsStore.Save"/>; an external edit while open reloads the fields
/// unless the user has unsaved changes, which are kept with a notice.
/// </summary>
internal sealed class SettingsWindow : Form
{
    public SettingsWindow(SettingsStore store, Logging.JobLogStore logStore, Jobs.JobManager jobs)
    {
        Store = store;
        LogStore = logStore;
        Jobs = jobs;
    }

    public SettingsStore Store { get; }

    public Logging.JobLogStore LogStore { get; }

    public Jobs.JobManager Jobs { get; }

    /// <summary>Show and activate; optionally focus the field for a config key such as "threads".</summary>
    public void ShowSettings(string? highlightKey = null) => throw new NotImplementedException();
}
