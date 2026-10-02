using RoboRightClick.Core;

namespace RoboRightClick.UI;

internal enum ErrorSummaryChoice
{
    /// <summary>Dialog closed without a choice: the job keeps its attention state.</summary>
    None,
    TryAgain,
    Skip,
}

/// <summary>
/// The end-of-job counterpart of Explorer's per-file error prompt (the one deliberate
/// deviation, docs/parity.md): lists every <see cref="ErrorReported"/> (path, Windows
/// message, code) and offers "Try again (N)" and "Skip". Shown from the Jobs window or a
/// toast click, never by the job itself, so a failed job never blocks anything.
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
