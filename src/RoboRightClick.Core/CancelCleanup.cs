namespace RoboRightClick.Core;

/// <param name="Delete">Destination files to delete if present: partial copies the job itself created.</param>
/// <param name="LeftInPlace">
/// Destinations that existed before their step and may now be partly overwritten (robocopy
/// allocates full length first, so they can look complete). Reported, never deleted.
/// </param>
public sealed record CleanupPlan(IReadOnlyList<string> Delete, IReadOnlyList<string> LeftInPlace);

/// <summary>
/// Explorer deletes the partially written file when a copy is canceled. Robocopy is
/// killed instead, so the job works out afterwards which destination files may be
/// partial. Getting this wrong deletes user data, so every rule errs towards keeping.
/// </summary>
public static class CancelCleanup
{
    /// <param name="startedFiles">
    /// Planned files of the robocopy steps that had started when cancel hit. In-process
    /// copies are never listed: CopyFileEx deletes its own partial file on PROGRESS_CANCEL,
    /// and a copy that failed because its name was taken never wrote anything of ours.
    /// </param>
    /// <param name="completedSources">Sources reported complete (normalized with <see cref="WinPath.NormalizeForMatch"/>).</param>
    /// <param name="presentBeforeStep">
    /// Destinations that existed when their step started: the scan's conflicts plus the
    /// host's re-check just before the step, so a file that appeared while the job waited
    /// (the user, another program) counts as someone else's.
    /// </param>
    /// <param name="move">True for a cut.</param>
    /// <param name="sourceStillExists">Live check by the host, made after robocopy has exited.</param>
    /// <param name="claimedByOtherJob">
    /// True when another job of this session plans to write, or wrote, this destination. Its
    /// file may be the only copy of a moved source, so this job must not touch it.
    /// </param>
    public static CleanupPlan Select(
        IEnumerable<PlannedFile> startedFiles,
        IEnumerable<string> completedSources,
        IEnumerable<string> presentBeforeStep,
        bool move,
        Func<string, bool> sourceStillExists,
        Func<string, bool> claimedByOtherJob)
    {
        var completed = new HashSet<string>(completedSources.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var present = new HashSet<string>(presentBeforeStep.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var delete = new List<string>();
        var left = new List<string>();

        foreach (var file in startedFiles)
        {
            if (completed.Contains(WinPath.NormalizeForMatch(file.SourcePath)))
            {
                continue;
            }
            if (present.Contains(WinPath.NormalizeForMatch(file.DestinationPath)))
            {
                // The user's own file. It may be half overwritten, but deleting it
                // would turn a partial loss into a total one.
                left.Add(file.DestinationPath);
                continue;
            }
            if (claimedByOtherJob(file.DestinationPath))
            {
                // Another paste wrote or is writing this name; it is not ours to judge.
                continue;
            }
            if (move && !sourceStillExists(file.SourcePath))
            {
                // Robocopy /MOV deletes a source only after its copy finished, and the
                // kill can land between that delete and the file's output line. The
                // destination is then the only copy left.
                continue;
            }
            delete.Add(file.DestinationPath);
        }
        return new CleanupPlan(delete, left);
    }
}
