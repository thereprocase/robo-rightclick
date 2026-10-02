namespace RoboRightClick.Core;

public sealed record FileFacts(long Size, DateTimeOffset LastWriteUtc);

/// <summary>One file a plan will write, as discovered while scanning.</summary>
public sealed record PlannedFile(string SourcePath, string DestinationPath, FileFacts Source);

public sealed record FileConflict(string SourcePath, string DestinationPath, FileFacts Source, FileFacts Existing)
{
    /// <summary>
    /// Whether "keep both" can be offered for this file. False for a cut across volumes:
    /// keeping both there needs an OS move that deletes the source, which invariant 1 does
    /// not allow. A same-volume cut keeps both with a rename, and a copy always can.
    /// </summary>
    public bool KeepBothAllowed { get; init; } = true;

    public bool SourceIsNewer => Source.LastWriteUtc > Existing.LastWriteUtc;
    public bool LooksIdentical => Source.Size == Existing.Size && Source.LastWriteUtc == Existing.LastWriteUtc;
}

public static class ConflictScan
{
    /// <summary>
    /// Every planned file whose destination already exists. Explorer asks about
    /// these even when the two files look identical, so no conflict is
    /// filtered out here; the dialog shows sizes and dates and the user decides.
    /// </summary>
    public static IReadOnlyList<FileConflict> Find(IEnumerable<PlannedFile> files, Func<string, FileFacts?> existingAt)
    {
        var conflicts = new List<FileConflict>();
        foreach (var file in files)
        {
            if (existingAt(file.DestinationPath) is { } existing)
            {
                conflicts.Add(new FileConflict(file.SourcePath, file.DestinationPath, file.Source, existing));
            }
        }
        return conflicts;
    }

    /// <summary>
    /// The job-wide answer to "which policy applies": the configured default unless it is
    /// Ask, in which case the user's choice, or Ask itself when there was nothing to ask.
    /// </summary>
    /// <remarks>
    /// Not the per-step policy. <see cref="ExecutionPlanner.Apply"/> decides that, and runs
    /// every step of a conflict-free scan with Ask whatever the configured default, whose
    /// flags (<see cref="RobocopyArgs.ConflictFlags"/>) skip a file that appears after the
    /// scan instead of overwriting it unasked. Use the planner's steps, not this, to run a job.
    /// </remarks>
    public static ConflictPolicy Resolve(ConflictPolicy configured, int conflictCount, ConflictPolicy? userChoice)
    {
        if (configured != ConflictPolicy.Ask)
        {
            return configured;
        }
        if (conflictCount == 0)
        {
            return ConflictPolicy.Ask;
        }
        return userChoice ?? throw new InvalidOperationException("Conflicts need a decision before the job can run.");
    }
}
