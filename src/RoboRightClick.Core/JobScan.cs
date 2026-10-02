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
/// (FileSystemFacts); tests answer from a fixture. Enumeration errors (access denied
/// on a subfolder) are the host's to report as <see cref="ScanEntry"/>-less gaps;
/// robocopy will hit and report the same error during the run.
/// </summary>
public interface IScanFacts
{
    IEnumerable<ScanEntry> List(string directory);

    /// <summary>Facts about the file at <paramref name="path"/>, or null when no file is there.</summary>
    FileFacts? FileAt(string path);
}

/// <summary>What one plan step will write.</summary>
/// <param name="Files">Every destination file the step creates or overwrites.</param>
/// <param name="LinkFolders">Destination paths of directory links the step skips; recreated empty in Finalizing.</param>
public sealed record StepScan(PlanStep Step, IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> LinkFolders);

/// <summary>
/// The result of the Scanning state. Pre-existing destinations are exactly the
/// conflicts' destination paths; cancel cleanup must never delete those.
/// </summary>
public sealed record ScanResult(PastePlan Plan, IReadOnlyList<StepScan> Steps, IReadOnlyList<FileConflict> Conflicts)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);
}

public static class JobScanner
{
    /// <summary>
    /// Expands a plan into the files it will write (Explorer's "Calculating…").
    /// For a <see cref="RobocopyStep"/>: the named files, or the whole tree when
    /// recursive, mapped to DestinationDirectory + relative path; directory links are
    /// not descended into and land in LinkFolders. For a <see cref="DuplicateFileStep"/>:
    /// its single file. A <see cref="RenameStep"/> writes no data and lists no files.
    /// Conflicts come from <see cref="ConflictScan.Find"/> over all planned files.
    /// </summary>
    public static ScanResult Scan(PastePlan plan, IScanFacts facts, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
