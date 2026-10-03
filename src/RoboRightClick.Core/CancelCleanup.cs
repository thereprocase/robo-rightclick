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

/// <summary>
/// What a live check found at a path. Only "not found" is <see cref="Absent"/>: a check that
/// failed for any other reason (a share that does not answer, access denied, a drive that is
/// not ready) proves nothing, and is <see cref="Unknown"/>. Cancel cleanup treats Unknown as
/// present on both sides, so a file it cannot see is reported rather than passed over.
/// </summary>
public enum PathPresence
{
    Absent,
    Present,
    Unknown,
}

/// <summary>
/// A 64-bit hash of a path, equal for paths <see cref="WinPath.Comparer"/> calls equal (FNV-1a
/// over the upper-cased UTF-16 units). For sets that only ever answer "maybe" safely: a
/// collision can only make a lookup say yes for a path that was never added.
/// </summary>
public static class PathHash
{
    public static ulong Of(string path)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in path)
        {
            hash = (hash ^ char.ToUpperInvariant(c)) * 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// What the host saw at a killed run's destinations while robocopy was suspended, by
/// <see cref="WinPath.NormalizeForMatch"/> path. A cancel 5% into a million-file paste finds
/// most of them absent, so absent paths are kept only as <see cref="PathHash"/> values: a
/// collision reads a path as absent, which leads to doing nothing, never to a delete. Every
/// other observation is kept whole (the identity is what a delete checks). A path never
/// looked at is <see cref="KillEvidence.Unknown"/>. Not thread-safe; the job guards it.
/// </summary>
public sealed class KillObservations
{
    private readonly Dictionary<string, KillObservation> _seen = new(WinPath.Comparer);
    private readonly HashSet<ulong> _absent = [];

    /// <summary>How many paths were recorded as absent (exact unless two hashes collided).</summary>
    public int AbsentCount => _absent.Count;

    public int Count(KillEvidence evidence) => evidence == KillEvidence.Absent
        ? _absent.Count
        : _seen.Values.Count(o => o.Evidence == evidence);

    public int Total => _seen.Count + _absent.Count;

    /// <summary>The observation for <paramref name="path"/>; get and set take a raw path and normalize it.</summary>
    public KillObservation this[string path]
    {
        get => Of(path);
        set => Record(path, value);
    }

    public void Record(string path, KillObservation observation)
    {
        var key = WinPath.NormalizeForMatch(path);
        if (observation.Evidence == KillEvidence.Absent)
        {
            _seen.Remove(key);
            _absent.Add(PathHash.Of(key));
        }
        else
        {
            _seen[key] = observation;
        }
    }

    public KillObservation Of(string path)
    {
        var key = WinPath.NormalizeForMatch(path);
        if (_seen.TryGetValue(key, out var observation))
        {
            return observation;
        }
        return _absent.Contains(PathHash.Of(key)) ? new KillObservation(KillEvidence.Absent, default) : default;
    }

    public void Clear()
    {
        _seen.Clear();
        _absent.Clear();
        _seen.TrimExcess();
        _absent.TrimExcess();
    }
}

/// <summary>
/// The destinations one job planned, for <see cref="CancelCleanup"/>'s "claimed by another
/// job" check. Only a job that has not ended answers it: a finished job's file was already at
/// its destination when a later job's step started, so that job's presence check protects it
/// (<c>presentBeforeStep</c>), and an ended job's claims would only hide a later job's own
/// partial copy from both the delete and the report. The job clears its set when it ends.
/// Not thread-safe; the job guards it.
/// </summary>
public sealed class ClaimSet
{
    private readonly HashSet<string> _files = new(WinPath.Comparer);
    private readonly List<string> _roots = [];

    /// <summary>A file's destination (normalized here).</summary>
    public void AddFile(string destinationPath) => _files.Add(WinPath.NormalizeForMatch(destinationPath));

    /// <summary>An item moved whole by a rename: everything under it is claimed too.</summary>
    public void AddRoot(string destinationPath) => _roots.Add(WinPath.NormalizeForMatch(destinationPath));

    public bool Contains(string destinationPath)
    {
        var path = WinPath.NormalizeForMatch(destinationPath);
        return _files.Contains(path) || _roots.Any(root => WinPath.AreSame(path, root) || WinPath.IsStrictlyUnder(path, root));
    }

    /// <summary>Drops every claim (the job has ended) and the memory that held them.</summary>
    public void Clear()
    {
        _files.Clear();
        _files.TrimExcess();
        _roots.Clear();
        _roots.TrimExcess();
    }

    /// <summary>True when nothing is claimed.</summary>
    public bool IsEmpty => _files.Count == 0 && _roots.Count == 0;
}

/// <summary>A planned file of a robocopy run the cancel killed, with that run's conflict policy.</summary>
public sealed record KilledRunFile(PlannedFile File, ConflictPolicy Policy);

/// <summary>A partial file to delete, but only if the file at the path is still this one.</summary>
public sealed record CleanupTarget(string Path, FileIdentity Identity);

/// <param name="Delete">Partial copies robocopy was writing when it was killed; deleted only while their identity still matches.</param>
/// <param name="LeftInPlace">
/// Files that may hold part of what robocopy was writing: destinations that existed before
/// their step under a policy that overwrites (robocopy allocates full length first, so they can
/// look complete), new files nobody could prove robocopy was writing, and partial copies that
/// may not be deleted (claimed by another active job, or deleting is off for this cancel).
/// Reported, never deleted.
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
    /// What the host saw at each destination while robocopy was suspended for the kill, asked
    /// with the <see cref="WinPath.NormalizeForMatch"/> path (<see cref="KillObservations.Of"/>).
    /// A path never looked at is <see cref="KillEvidence.Unknown"/>.
    /// </param>
    /// <param name="move">True for a cut.</param>
    /// <param name="destinationPresence">
    /// Live check by the host after robocopy has exited; asked only where the evidence is not
    /// absent or not-open. Anything but <see cref="PathPresence.Absent"/> reports the file.
    /// </param>
    /// <param name="sourcePresence">
    /// Live check by the host, made after robocopy has exited. Only
    /// <see cref="PathPresence.Absent"/> means a cut finished the file; a source that cannot be
    /// checked (an unreachable share) may still hold the only complete copy.
    /// </param>
    /// <param name="claimedByOtherJob">
    /// True when another job of this session that has not ended plans to write this
    /// destination (<see cref="ClaimSet"/>). Its file may be the only copy of a moved source, so
    /// this job never deletes it; it is reported as left in place, so the user still hears
    /// that it may be incomplete.
    /// </param>
    /// <param name="deleteAllowed">
    /// False when the job's ledger could not match robocopy's paths
    /// (<see cref="StepLedger.PathsUnreliable"/>): nothing is deleted, and every file that
    /// would have been is reported as left in place instead, so the cancel still says which
    /// files may be incomplete.
    /// </param>
    /// <param name="namesAfterExit">
    /// The names in a destination folder, listed after robocopy has exited: an empty set for a
    /// folder that does not exist, null when the folder could not be listed. Suspending a
    /// process is asynchronous: a thread inside a file-system call (creating a file on a slow
    /// share) finishes that call before it stops, so the look at the kill can miss a file
    /// robocopy was creating. A path seen absent at the kill whose name is there after the exit
    /// (or whose folder cannot be listed) is therefore treated as unknown evidence: reported
    /// when it exists, never deleted. Null skips the second look.
    /// </param>
    /// <remarks>
    /// The kill evidence is read before <paramref name="claimedByOtherJob"/> and
    /// <paramref name="sourcePresence"/>: not-open evidence never deletes or reports anything
    /// whatever those say, and neither does absent evidence that the second look confirms. A
    /// cancel early in a large paste has hundreds of thousands of such files, each of which
    /// would otherwise cost a stat on the source volume and a pass over every active job; the
    /// second look costs one listing per destination folder, not one stat per file.
    /// </remarks>
    public static CleanupPlan Select(
        IEnumerable<KilledRunFile> killedRunFiles,
        IEnumerable<string> completedSources,
        IEnumerable<string> presentBeforeStep,
        Func<string, KillObservation> atKill,
        bool move,
        Func<string, PathPresence> destinationPresence,
        Func<string, PathPresence> sourcePresence,
        Func<string, bool> claimedByOtherJob,
        bool deleteAllowed = true,
        Func<string, IReadOnlySet<string>?>? namesAfterExit = null)
    {
        var completed = new HashSet<string>(completedSources.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var present = new HashSet<string>(presentBeforeStep.Select(WinPath.NormalizeForMatch), WinPath.Comparer);
        var delete = new List<CleanupTarget>();
        var left = new List<string>();
        var listed = new Dictionary<string, IReadOnlySet<string>?>(WinPath.Comparer);

        // True when a path seen absent at the kill may have been created after that look.
        bool AppearedAfterKill(string destinationPath)
        {
            if (namesAfterExit is null)
            {
                return false;
            }
            var folder = WinPath.GetParent(destinationPath);
            if (!listed.TryGetValue(folder, out var names))
            {
                names = namesAfterExit(folder);
                listed[folder] = names;
            }
            return names is null || names.Contains(WinPath.GetFileName(destinationPath));
        }

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
            var seen = atKill(destination);
            if (seen.Evidence == KillEvidence.Absent && AppearedAfterKill(file.DestinationPath))
            {
                // Created after the look (see namesAfterExit): nothing proves whose it is.
                seen = default;
            }
            if (seen.Evidence is KillEvidence.Absent or KillEvidence.NotOpenByRobocopy)
            {
                // Absent: robocopy had not created it yet; a file there now is someone else's.
                // Not open: a file robocopy was not writing: a late arrival it skipped, a
                // finished copy whose output line was never read, or what an earlier error
                // left. None of these is a partial copy of this cancel, and the first is the
                // user's data. Nothing below could change that, so nothing more is asked.
                continue;
            }
            var source = move ? sourcePresence(file.SourcePath) : PathPresence.Present;
            if (source == PathPresence.Absent)
            {
                // Robocopy /MOV deletes a source only after its copy finished, and the
                // kill can land between that delete and the file's output line. The
                // destination is then the only copy left, and it is complete. A source that
                // cannot be checked is not proof of that: the file goes on to be reported.
                continue;
            }

            // A cut whose source cannot be checked keeps its destination too: if the source was
            // in fact moved, that file is the only copy.
            var proven = seen.Evidence == KillEvidence.OpenByRobocopy && seen.Identity.IsKnown && source == PathPresence.Present;
            if (proven && deleteAllowed && !claimedByOtherJob(file.DestinationPath))
            {
                delete.Add(new CleanupTarget(file.DestinationPath, seen.Identity));
            }
            else if (destinationPresence(file.DestinationPath) != PathPresence.Absent)
            {
                // Not proven ours, or ours but not to be deleted (another active paste plans
                // this name, or the ledger lost track of robocopy's paths). Most likely a
                // partial copy, so the user is told it may be incomplete; deleting it could
                // destroy someone else's file. A destination that cannot be checked is
                // reported too: silence would hide a file that is there.
                left.Add(file.DestinationPath);
            }
        }
        return new CleanupPlan(delete, left);
    }
}
