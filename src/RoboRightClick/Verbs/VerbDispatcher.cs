using System.Diagnostics;
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
    public void Invoke(ShellVerb verb, IReadOnlyList<string> selection, int skippedItems)
    {
        // The caller's list may be reused once Execute returns; the queued work needs its own.
        var items = selection.ToArray();
        Ui.Post(_ => Chain(() => RunAsync(verb, items, skippedItems)), null);
    }

    /// <summary>The end of the FIFO. Only touched from <see cref="Ui"/> callbacks.</summary>
    private Task _tail = Task.CompletedTask;

    private void Chain(Func<Task> work) => _tail = RunAfterAsync(_tail, work);

    /// <summary>
    /// Waits for the previous verb, then runs this one. The awaits resume on the UI context
    /// (no ConfigureAwait), which the clipboard calls need. A verb that throws must not stop
    /// the verbs behind it: <paramref name="previous"/> therefore never faults.
    /// </summary>
    private static async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        await previous;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            // Type name only: an exception message can carry a path, and toasts and logs must not.
            Trace.TraceError("Verb failed: " + ex.GetType().Name);
        }
    }

    private Task RunAsync(ShellVerb verb, IReadOnlyList<string> selection, int skippedItems) => verb switch
    {
        ShellVerb.RoboCopy => WriteToClipboardAsync(selection, skippedItems, TransferVerb.Copy),
        ShellVerb.RoboCut => WriteToClipboardAsync(selection, skippedItems, TransferVerb.Move),
        ShellVerb.RoboPaste => PasteAsync(selection, skippedItems),
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private async Task WriteToClipboardAsync(IReadOnlyList<string> selection, int skippedItems, TransferVerb verb)
    {
        if (VerbRules.SelectionRefusal(selection, skippedItems) is { } refusal)
        {
            Refuse(refusal);
            return;
        }
        if (await ClipboardService.WriteFilesAsync(selection, verb, SettingsStore.Current.Logging) is { } failure)
        {
            Refuse(failure);
        }
    }

    private async Task PasteAsync(IReadOnlyList<string> selection, int skippedItems)
    {
        if (VerbRules.PasteDestinationRefusal(selection, skippedItems) is { } destinationRefusal)
        {
            Refuse(destinationRefusal);
            return;
        }
        // Accepted above, so this is the one trimmed folder.
        var destination = ShellVerbs.PasteDestination(selection).Folder!;

        var read = await ClipboardService.ReadFilesAsync();
        if (read.Refusal is { } clipboardRefusal)
        {
            Refuse(clipboardRefusal);
            return;
        }
        // ClipboardService classified the read (VerbRules.ClassifyClipboard); a result with
        // neither files nor a refusal would be a bug there, and pasting nothing is the safe answer.
        if (read.Files is not { Paths.Count: > 0 } files)
        {
            Refuse(VerbRefusal.ClipboardEmpty);
            return;
        }

        var verb = ClipboardPayload.VerbForPaste(files.PreferredEffect);
        if (VerbRules.IsSameFolderMove(files.Paths, destination, verb))
        {
            return; // Explorer does nothing here, and so do we.
        }

        var order = new PasteOrder(files.Paths, destination, verb);
        // Only a cut carries the sequence number: it is what stops the same cut being pasted twice.
        if (Jobs.Enqueue(order, verb == TransferVerb.Move ? files.SequenceNumber : null) is null)
        {
            Refuse(VerbRefusal.AlreadyBeingMoved);
        }
    }

    private void Refuse(VerbRefusal refusal) => Notifier.Show(ToastText.ForRefusal(refusal));
}
