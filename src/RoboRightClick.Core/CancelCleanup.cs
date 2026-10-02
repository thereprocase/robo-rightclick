namespace RoboRightClick.Core;

/// <param name="Delete">Destination files to delete if present: partial copies the job itself created.</param>
/// <param name="LeftInPlace">Destinations that existed before the job and may now be partly overwritten; reported, never deleted.</param>
public sealed record CleanupPlan(IReadOnlyList<string> Delete, IReadOnlyList<string> LeftInPlace);

/// <summary>
/// Explorer deletes the partially written file when a copy is canceled. Robocopy is
/// killed instead, so the job works out afterwards which destination files may be
/// partial. Getting this wrong deletes user data, so every rule errs towards keeping.
/// </summary>
public static class CancelCleanup
{
    /// <param name="startedFiles">Planned files of the steps that had started when cancel hit.</param>
    /// <param name="completedSources">Sources reported complete (robocopy FileReported or in-process success).</param>
    /// <param name="preExistingDestinations">Destinations the scan found already present.</param>
    /// <param name="move">True for a cut.</param>
    /// <param name="sourceStillExists">Live check by the host, made after robocopy has exited.</param>
    public static CleanupPlan Select(
        IEnumerable<PlannedFile> startedFiles,
        IEnumerable<string> completedSources,
        IEnumerable<string> preExistingDestinations,
        bool move,
        Func<string, bool> sourceStillExists)
    {
        var completed = new HashSet<string>(completedSources, WinPath.Comparer);
        var preExisting = new HashSet<string>(preExistingDestinations, WinPath.Comparer);
        var delete = new List<string>();
        var left = new List<string>();

        foreach (var file in startedFiles)
        {
            if (completed.Contains(file.SourcePath))
            {
                continue;
            }
            if (preExisting.Contains(file.DestinationPath))
            {
                // The user's own file. It may be half overwritten, but deleting it
                // would turn a partial loss into a total one.
                left.Add(file.DestinationPath);
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
