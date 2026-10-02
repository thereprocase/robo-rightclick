using RoboRightClick.App;
using RoboRightClick.Com;
using RoboRightClick.Core;
using RoboRightClick.Jobs;
using RoboRightClick.UI;

namespace RoboRightClick.Verbs;

/// <summary>
/// What each verb does once the COM layer has its paths. Invoked on the UI thread from
/// Execute; everything here is posted back to the same thread so Execute returns
/// immediately and verbs still run in click order (a copy followed at once by a paste
/// sees its own clipboard write).
/// </summary>
internal sealed class VerbDispatcher : IVerbHandler
{
    public VerbDispatcher(
        SynchronizationContext ui,
        ClipboardService clipboard,
        JobManager jobs,
        SettingsStore settings,
        FileSystemFacts fileSystem,
        Notifier notifier)
    {
        Ui = ui;
        ClipboardService = clipboard;
        Jobs = jobs;
        SettingsStore = settings;
        FileSystem = fileSystem;
        Notifier = notifier;
    }

    public SynchronizationContext Ui { get; }
    public ClipboardService ClipboardService { get; }
    public JobManager Jobs { get; }
    public SettingsStore SettingsStore { get; }
    public FileSystemFacts FileSystem { get; }
    public Notifier Notifier { get; }

    /// <summary>
    /// RoboCopy / RoboCut: <see cref="ClipboardService.WriteFiles"/> with the current
    /// logging mode. Nothing else: like Explorer, copying to the clipboard starts no job.
    /// RoboPaste: <see cref="ShellVerbs.PasteDestination"/> on the selection, then
    /// <see cref="ClipboardService.ReadFiles"/>; the verb comes from
    /// <see cref="ClipboardPayload.VerbForPaste"/>; IsDirectory per source from
    /// <see cref="FileSystemFacts"/>; then <see cref="JobManager.Enqueue"/> with the
    /// clipboard sequence number for a cut. "Nothing to paste" and a refused destination
    /// become a toast (no paths in it, whatever the mode).
    /// </summary>
    public void Invoke(ShellVerb verb, IReadOnlyList<string> paths) => throw new NotImplementedException();
}
