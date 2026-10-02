using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// <see cref="IJobPrompts"/> for the real UI: posts to the UI thread, shows a modeless
/// <see cref="ConflictDialog"/> (several jobs may ask at once, as several Explorer copy
/// dialogs can) and completes the task when it closes. Cancellation closes the dialog
/// and returns null.
/// </summary>
internal sealed class UiPrompts : IJobPrompts
{
    public UiPrompts(SynchronizationContext ui)
    {
        Ui = ui;
    }

    public SynchronizationContext Ui { get; }

    public Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        bool allowKeepBoth,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
