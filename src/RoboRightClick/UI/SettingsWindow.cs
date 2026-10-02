using RoboRightClick.App;

namespace RoboRightClick.UI;

/// <summary>
/// One page mapping every config.json field: threads, retries, retry wait, conflict
/// default, max concurrent jobs (0 = unlimited), logging mode, log retention, start with
/// Windows, notify on complete, extra args for copy and move. Values are checked by
/// round-tripping through <see cref="Core.SettingsSerializer.Parse"/>; any problem is
/// shown next to the field and Save stays disabled. Also: "Open config file" and the
/// version. Saving goes through <see cref="SettingsStore.Save"/>.
/// </summary>
internal sealed class SettingsWindow : Form
{
    public SettingsWindow(SettingsStore store)
    {
        Store = store;
    }

    public SettingsStore Store { get; }
}
