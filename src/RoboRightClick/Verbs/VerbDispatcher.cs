using RoboRightClick.App;
using RoboRightClick.Com;
using RoboRightClick.Core;
using RoboRightClick.Jobs;
using RoboRightClick.UI;

namespace RoboRightClick.Verbs;

/// <summary>
/// What each verb does once the COM layer has its paths. Invoked on the UI thread from
/// Execute; the work is queued on the same thread so Execute returns immediately. Verbs run
/// strictly one after another in click order (an async queue: the next starts when the
/// previous one's clipboard access finished), so a copy followed at once by a paste sees
/// its own clipboard write. No file-system call happens here: the UI thread also serves
/// every COM call, and one stat on a dead network share would freeze every right-click.
/// </summary>
internal sealed class VerbDispatcher : IVerbHandler
{
    public VerbDispatcher(
        SynchronizationContext ui,
        ClipboardService clipboard,
        JobManager jobs,
        SettingsStore settings,
        Notifier notifier)
    {
        Ui = ui;
        ClipboardService = clipboard;
        Jobs = jobs;
        SettingsStore = settings;
        Notifier = notifier;
    }

    public SynchronizationContext Ui { get; }
    public ClipboardService ClipboardService { get; }
    public JobManager Jobs { get; }
    public SettingsStore SettingsStore { get; }
    public Notifier Notifier { get; }

    /// <summary>
    /// RoboCopy / RoboCut: <paramref name="selection"/> paths through
    /// <see cref="PathPolicy"/> (any refused path: <see cref="VerbRefusal.SelectionNotFiles"/>,
    /// nothing written), then <see cref="ClipboardService.WriteFilesAsync"/> with the current
    /// logging mode. Like Explorer, copying to the clipboard starts no job.
    /// RoboPaste: <see cref="ShellVerbs.PasteDestination"/>, then
    /// <see cref="ClipboardService.ReadFilesAsync"/>; the verb from
    /// <see cref="ClipboardPayload.VerbForPaste"/>. A move whose every source's parent is the
    /// destination (string comparison only) is Explorer's no-op: nothing happens, no toast.
    /// Otherwise <see cref="JobManager.Enqueue"/> with a <see cref="PasteOrder"/> and the
    /// clipboard sequence number for a cut. Every refusal is a <see cref="ToastText.ForRefusal"/>
    /// toast; none carries a path.
    /// </summary>
    /// <param name="skippedItems">Selected items with no file-system path (virtual folders).</param>
    public void Invoke(ShellVerb verb, IReadOnlyList<string> selection, int skippedItems) => throw new NotImplementedException();
}
