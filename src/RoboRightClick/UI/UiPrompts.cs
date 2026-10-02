using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// <see cref="IJobPrompts"/> for the real UI: posts to the UI thread, shows a modeless
/// <see cref="ConflictDialog"/> (several jobs may ask at once, as several Explorer copy
/// dialogs can), owned by the job's progress window when one is open, and completes the
/// task when it closes. Cancellation closes the dialog and returns null.
/// <see cref="Shutdown"/> closes every open dialog and makes later requests return null at
/// once, so exit never waits on a question nobody will answer.
/// </summary>
internal sealed class UiPrompts : IJobPrompts
{
    public UiPrompts(SynchronizationContext ui, Func<ProgressWindowHost?> progressWindows)
    {
        Ui = ui;
        ProgressWindows = progressWindows;
    }

    public SynchronizationContext Ui { get; }

    /// <summary>Late-bound: the host is created after the job manager, which needs these prompts.</summary>
    public Func<ProgressWindowHost?> ProgressWindows { get; }

    public Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <summary>Brings the job's open conflict dialog to the front; false when it has none.</summary>
    public bool Activate(Guid jobId) => throw new NotImplementedException();

    public void Shutdown() => throw new NotImplementedException();
}
