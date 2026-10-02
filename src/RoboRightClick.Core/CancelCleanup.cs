namespace RoboRightClick.Core;

/// <summary>
/// One file on one volume, as read from an open handle: the volume serial number, the file
/// ID and the creation time. FAT and exFAT have no stable file ID (it is the position of the
/// directory entry, which a new file can take over once the old one is deleted), so the
/// creation time is part of the identity: a different file in the same slot is not this one.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdHigh, ulong FileIdLow, long CreationTime)
{
    /// <summary>A file system that reports no ID at all gives zeros; such an identity proves nothing.</summary>
    public bool IsKnown => FileIdHigh != 0 || FileIdLow != 0;
}

/// <summary>What the host saw at one destination path while the robocopy being killed was suspended.</summary>
public enum KillEvidence
{
    /// <summary>Not looked at, or the look failed: nothing is proven.</summary>
    Unknown = 0,

    /// <summary>Nothing was at the path. Whatever is there now arrived after robocopy stopped.</summary>
    Absent,

    /// <summary>A file was there and robocopy did not have it open: not something robocopy was writing.</summary>
    NotOpenByRobocopy,

    /// <summary>Robocopy had the file open: it was writing it when the cancel stopped it.</summary>
    OpenByRobocopy,
}

public readonly record struct KillObservation(KillEvidence Evidence, FileIdentity Identity);

/// <summary>A planned file of a robocopy run the cancel killed, with that run's conflict policy.</summary>
public sealed record KilledRunFile(PlannedFile File, ConflictPolicy Policy);

/// <summary>A partial file to delete, but only if the file at the path is still this one.</summary>
public sealed record CleanupTarget(string Path, FileIdentity Identity);

/// <param name="Delete">Partial copies robocopy was writing when it was killed; deleted only while their identity still matches.</param>
/// <param name="LeftInPlace">
/// Files that may hold part of what robocopy was writing: destinations that existed before
/// their step under a policy that overwrites (robocopy allocates full length first, so they can
/// look complete), and new files nobody could prove robocopy was writing. Reported, never deleted.
/// </param>
public sealed record CleanupPlan(IReadOnlyList<CleanupTarget> Delete, IReadOnlyList<string> LeftInPlace);

/// <summary>
/// Explorer deletes the partially written file when a copy is canceled. Robocopy is
/// killed instead, so the job works out afterwards which destination files are its partial
/// copies. Getting this wrong deletes user data, so a file is deleted only on positive
/// evidence that robocopy was writing it: while robocopy was suspended for the kill, it held
/// the file open. A file that merely exists where a planned file would go proves nothing: it
/// can be a file another program put there after the step's presence check, which robocopy
/// then skipped without a word.
/// </summary>
public static class CancelCleanup
{
    /// <param name="killedRunFiles">
    /// Planned files of the robocopy runs the cancel killed that had not completed
    /// (<see cref="StepLedger.KilledRunFiles"/>). Runs that ended on their own are not the
    /// cancel's business: their files are complete, failed (an error) or skipped late
    /// arrivals. In-process copies are never listed: CopyFileEx deletes its own partial file
    /// on PROGRESS_CANCEL.
    /// </param>
    /// <param name="completedSources">Sources reported complete (normalized with <see cref="WinPath.NormalizeForMatch"/>).</param>
    /// <param name="presentBeforeStep">
    /// Destinations that existed when their step started: the scan's conflicts plus the
    /// host's re-check just before the step.
    /// </param>
    /// <param name="atKill">
    /// What the host saw at each destination while robocopy was suspended for the kill, keyed
    /// by <see cref="WinPath.NormalizeForMatch"/> path. A path that is missing counts as
    /// <see cref="KillEvidence.Unknown"/>.
    /// </param>
    /// <param name="move">True for a cut.</param>
    /// <param name="destinationExists">Live check by the host after robocopy has exited; asked only where the evidence is unknown.</param>
    /// <param name="sourceStillExists">Live check by the host, made after robocopy has exited.</param>
    /// <param name="claimedByOtherJob">
    /// True when another job of this session plans to write, or wrote, this destination. Its
    /// file may be the only copy of a moved source, so this job must not touch it.
    /// </param>
    public static CleanupPlan Select(
        IEnumerable<KilledRunFile> killedRunFiles,
        IEnumerable<string> completedSources,
        IEnumerable<string> presentBeforeStep,
        IReadOnlyDictionary<string, KillObservation> atKill,
        bool move,
        Func<string, bool> destinationExists,
        Func<string, bool> sourceStillExists,
        Func<string, bool> claimedByOtherJob)
    {
        var completed = new HashSet<string>(completedSources.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var present = new HashSet<string>(presentBeforeStep.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var delete = new List<CleanupTarget>();
        var left = new List<string>();

        foreach (var (file, policy) in killedRunFiles)
        {
            if (completed.Contains(WinPath.NormalizeForMatch(file.SourcePath)))
            {
                continue;
            }
            var destination = WinPath.NormalizeForMatch(file.DestinationPath);
            if (present.Contains(destination))
            {
                // The user's own file. It may be half overwritten, but deleting it would turn
                // a partial loss into a total one. Under Skip flags robocopy never writes a file
                // that existed when it started, so only an overwriting policy can damage it.
                if (RobocopyArgs.MayOverwriteExisting(policy))
                {
                    left.Add(file.DestinationPath);
                }
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

            var seen = atKill.TryGetValue(destination, out var observation) ? observation : default;
            switch (seen.Evidence)
            {
                case KillEvidence.OpenByRobocopy when seen.Identity.IsKnown:
                    delete.Add(new CleanupTarget(file.DestinationPath, seen.Identity));
                    break;
                case KillEvidence.Absent:
                    // Robocopy had not created it yet; a file there now is someone else's.
                    break;
                case KillEvidence.NotOpenByRobocopy:
                    // A file robocopy was not writing: a late arrival it skipped, a finished
                    // copy whose output line was never read, or what an earlier error left.
                    // None of these is a partial copy of this cancel, and the first is the
                    // user's data.
                    break;
                default:
                    // Nothing proves who wrote it. Most likely a partial copy, so the user is
                    // told it may be incomplete; deleting it could destroy someone else's file.
                    if (destinationExists(file.DestinationPath))
                    {
                        left.Add(file.DestinationPath);
                    }
                    break;
            }
        }
        return new CleanupPlan(delete, left);
    }
}
