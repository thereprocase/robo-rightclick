namespace RoboRightClick.Core;

public enum ScanEntryKind
{
    File,
    Directory,

    /// <summary>
    /// A junction or directory symlink. Robocopy skips these (/XJD) and Explorer leaves
    /// an empty folder with the link's name, so the job recreates that empty folder.
    /// </summary>
    DirectoryLink,
}

/// <summary>One child of a directory. <paramref name="File"/> is set only for files.</summary>
public sealed record ScanEntry(string Name, ScanEntryKind Kind, FileFacts? File);

/// <summary>
/// The file-system facts the Scanning state needs. The host answers from disk
/// (FileSystemFacts, with FindFirstFileEx-style enumeration so one directory read yields
/// names, kinds, sizes and times); tests answer from a fixture.
/// </summary>
public interface IScanFacts
{
    /// <summary>
    /// Children of <paramref name="directory"/>, enumerated lazily without following
    /// reparse points; null when it does not exist or cannot be opened (robocopy reports
    /// the same error during the run).
    /// </summary>
    IEnumerable<ScanEntry>? List(string directory);

    /// <summary>Facts about the file at <paramref name="path"/>, or null when no file is there.</summary>
    FileFacts? FileAt(string path);
}

/// <summary>Running totals while scanning, for Explorer's "Discovered N items (X GB)".</summary>
public readonly record struct ScanProgress(long Files, long Bytes);

/// <summary>What one plan step will write.</summary>
/// <param name="Files">Every destination file the step creates or overwrites.</param>
/// <param name="LinkFolders">Destination paths of directory links the step skips; recreated empty in Finalizing.</param>
public sealed record StepScan(PlanStep Step, IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> LinkFolders);

/// <summary>
/// The result of the Scanning state. Pre-existing destinations are exactly the
/// conflicts' destination paths; cancel cleanup must never delete those.
/// </summary>
/// <param name="Issues">The paste plan's rejections plus anything the scan refused, such as a file whose destination name is taken by a folder.</param>
public sealed record ScanResult(
    PastePlan Plan,
    IReadOnlyList<StepScan> Steps,
    IReadOnlyList<FileConflict> Conflicts,
    IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);
}

public static class JobScanner
{
    public const string FolderInTheWayReason = "A folder with this name already exists at the destination.";

    /// <summary>
    /// Expands a plan into the files it will write (Explorer's "Calculating…").
    /// For a <see cref="RobocopyStep"/>: the named files (one <see cref="IScanFacts.FileAt"/>
    /// each), or the whole tree when recursive, mapped to DestinationDirectory + relative
    /// path; directory links are not descended into and land in LinkFolders. For a
    /// <see cref="DuplicateFileStep"/>: its single file. A <see cref="RenameStep"/> writes no
    /// data and lists no files.
    /// </summary>
    /// <remarks>
    /// Conflicts are found by listing each destination directory once and matching names,
    /// not by one stat per planned file: a directory whose destination does not exist is not
    /// listed at all, nor is anything below it, so pasting into a new folder costs no extra
    /// I/O. A destination name held by a folder where the source is a file becomes an issue
    /// (<see cref="FolderInTheWayReason"/>) and the file leaves the plan; a destination file
    /// where the source is a folder likewise. <see cref="FileConflict.KeepBothAllowed"/> is
    /// false for a conflict in a move step whose source and destination are on different
    /// volumes (<see cref="IPlanningFacts.SameVolume"/>). <paramref name="progress"/> is
    /// reported at most every 1,000 files and at the end; <paramref name="cancellationToken"/>
    /// is checked per directory and every 1,000 entries.
    /// </remarks>
    public static ScanResult Scan(
        PastePlan plan,
        IScanFacts facts,
        IPlanningFacts planning,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
