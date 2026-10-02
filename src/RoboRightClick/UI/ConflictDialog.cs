using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// Explorer's "Replace or Skip Files" dialog: "The destination has N files with the same
/// names" with Replace the files / Skip these files / Let me decide for each file. The
/// third choice expands to a per-file list with a source and a destination checkbox per
/// row, sizes and modified dates (<see cref="FileConflict"/>), and "Select all" for each
/// side. Both ticked = keep both where <see cref="FileConflict.KeepBothAllowed"/>, source
/// only = replace, destination only or neither = skip. Where keep-both is not allowed the
/// row says why ("Keeping both isn't available when moving between drives"). Closing or
/// Cancel = cancel the job. Title names the job ("Robo-Paste: 3 items → Archive"; in
/// ephemeral mode the dialog shows file names, which never leave memory, but its title is
/// generic). Owned by the job's <see cref="ProgressWindow"/> when there is one; otherwise
/// shown with Activate after AllowSetForegroundWindow from the COM call. Built in code (no
/// designer files); modeless; the result is read after FormClosed.
/// </summary>
internal sealed class ConflictDialog : Form
{
    public ConflictDialog(JobSnapshot job, IReadOnlyList<FileConflict> conflicts)
    {
        Job = job;
        Conflicts = conflicts;
    }

    public JobSnapshot Job { get; }

    public IReadOnlyList<FileConflict> Conflicts { get; }

    /// <summary>Null until the user picks; stays null on cancel.</summary>
    public ConflictChoice? Choice { get; private set; }
}
