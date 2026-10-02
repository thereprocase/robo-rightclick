using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// Explorer's "Replace or Skip Files" dialog: "The destination has N files with the same
/// names" with Replace the files / Skip these files / Let me decide for each file. The
/// third choice expands to a per-file list with a source and a destination checkbox per
/// row, sizes and modified dates (<see cref="FileConflict"/>), and "Select all" for each
/// side. Both ticked = keep both (only when allowed), source only = replace,
/// destination only or neither = skip. Closing or Cancel = cancel the job.
/// Built in code (no designer files); modeless; the result is read after FormClosed.
/// </summary>
internal sealed class ConflictDialog : Form
{
    public ConflictDialog(JobSnapshot job, IReadOnlyList<FileConflict> conflicts, bool allowKeepBoth)
    {
        Job = job;
        Conflicts = conflicts;
        AllowKeepBoth = allowKeepBoth;
    }

    public JobSnapshot Job { get; }

    public IReadOnlyList<FileConflict> Conflicts { get; }

    public bool AllowKeepBoth { get; }

    /// <summary>Null until the user picks; stays null on cancel.</summary>
    public ConflictChoice? Choice { get; private set; }
}
