using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// The questions a running job asks the user. Implemented by <see cref="UI.UiPrompts"/>,
/// which marshals to the UI thread and shows the conflict dialog owned by the job's
/// progress window. Called from job worker threads; the job sits in AwaitingDecision until
/// the task completes.
/// </summary>
internal interface IJobPrompts
{
    /// <summary>
    /// Explorer's Replace / Skip / Let me decide. Null means the user closed the dialog or
    /// pressed Cancel: the job is canceled. Keep-both is offered per file where
    /// <see cref="FileConflict.KeepBothAllowed"/> is true. Also completes with null when
    /// <paramref name="cancellationToken"/> fires or the app is shutting down, so exit never
    /// waits on an unanswered dialog.
    /// </summary>
    Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken);
}
