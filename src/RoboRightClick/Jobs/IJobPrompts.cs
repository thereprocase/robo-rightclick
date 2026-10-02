using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// The questions a running job asks the user. Implemented by <see cref="UI.UiPrompts"/>,
/// which marshals to the UI thread and shows a modeless dialog. Called from job worker
/// threads; the job sits in AwaitingDecision until the task completes.
/// </summary>
internal interface IJobPrompts
{
    /// <summary>
    /// Explorer's Replace / Skip / Let me decide. Null means the user closed the dialog or
    /// pressed Cancel: the job is canceled. <paramref name="allowKeepBoth"/> is false for a
    /// cut until keep-both moves have an ADR (see ExecutionPlanner).
    /// </summary>
    Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        bool allowKeepBoth,
        CancellationToken cancellationToken);
}
