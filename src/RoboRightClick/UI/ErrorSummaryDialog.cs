using RoboRightClick.Core;

namespace RoboRightClick.UI;

internal enum ErrorSummaryChoice
{
    /// <summary>Dialog closed without a choice: the job keeps its attention state.</summary>
    None,

    /// <summary>DoneWithErrors: JobManager.Retry. Failed: JobManager.Rerun. Damaged cancel: Retry of the damaged files ("Finish replacing them").</summary>
    TryAgain,
    Skip,
}

/// <summary>
/// The end-of-job counterpart of Explorer's per-file error prompt (the one deliberate
/// deviation, docs/parity.md). Sections, each shown only when non-empty: the failure reason
/// (<see cref="JobSnapshot.FailureReason"/>); retryable errors (path, Windows message,
/// code; the first <see cref="JobRecords.MaxRecordedErrors"/>, then "and N more, see the
/// log"); refused items with their reason (not retryable); files damaged by a cancel; files
/// skipped because their name appeared during the copy. Buttons: "Try again (N)" where N
/// counts only retryable items (absent when N is 0, except for a Failed job, where it
/// re-runs the whole paste) and "Skip". Shown from the Jobs window, the progress window or
/// a toast click, never by the job itself, so a failed job never blocks anything.
/// </summary>
internal sealed class ErrorSummaryDialog : Form
{
    public ErrorSummaryDialog(JobSnapshot job, IReadOnlyList<ErrorReported> errors)
    {
        Job = job;
        Errors = errors;
    }

    public JobSnapshot Job { get; }

    public IReadOnlyList<ErrorReported> Errors { get; }

    public ErrorSummaryChoice Choice { get; private set; }
}
