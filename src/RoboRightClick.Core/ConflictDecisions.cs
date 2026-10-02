namespace RoboRightClick.Core;

/// <summary>Per-file answer in Explorer's "Let me decide" list.</summary>
public enum FileDecision
{
    /// <summary>Only the source box ticked: overwrite the destination.</summary>
    Replace,

    /// <summary>Only the destination box ticked (or neither): leave it, do not copy.</summary>
    Skip,

    /// <summary>Both boxes ticked: copy the source under <see cref="DuplicateNamer.KeepBothName"/>.</summary>
    KeepBoth,
}

/// <summary>The answer to the conflict dialog. Null wherever it is passed means the user canceled the job.</summary>
public abstract record ConflictChoice
{
    private ConflictChoice()
    {
    }

    public sealed record ReplaceAll : ConflictChoice;

    public sealed record SkipAll : ConflictChoice;

    /// <param name="ByDestination">Decision per conflicting destination path; a missing entry means Skip.</param>
    public sealed record DecideEach(IReadOnlyDictionary<string, FileDecision> ByDestination) : ConflictChoice;
}

/// <summary>
/// A "keep both" file: copied (or, for a cut, moved) in-process to a new name, because
/// robocopy cannot rename. Robocopy excludes the source with /XF.
/// </summary>
public sealed record KeepBothStep(string Source, string Destination, bool Move) : PlanStep;

/// <summary>One step as it will actually run.</summary>
/// <param name="Policy">Conflict flags for a robocopy step; ignored by other steps.</param>
/// <param name="ExcludedFiles">Full source paths passed to robocopy as /XF.</param>
/// <param name="Files">What this step writes; the cancel-cleanup and retry inputs.</param>
public sealed record ExecutionStep(
    PlanStep Step,
    ConflictPolicy Policy,
    IReadOnlyList<string> ExcludedFiles,
    IReadOnlyList<PlannedFile> Files);

public sealed record ExecutionPlan(
    IReadOnlyList<ExecutionStep> Steps,
    IReadOnlyList<string> PreExistingDestinations,
    IReadOnlyList<string> LinkFolders,
    IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);
}

public static class ExecutionPlanner
{
    /// <summary>
    /// Turns a scan plus the conflict answer into the steps that run.
    /// The policy comes from <see cref="ConflictScan.Resolve"/>. ReplaceAll and SkipAll
    /// map to that policy on every robocopy step. DecideEach runs robocopy with Replace
    /// and /XF for every Skip and KeepBoth source, then appends one
    /// <see cref="KeepBothStep"/> per KeepBoth file, named with
    /// <see cref="DuplicateNamer.KeepBothName"/> against <paramref name="destinationTaken"/>
    /// and against names this plan already claimed. When the /XF list would push a command
    /// line past <see cref="PastePlanner.DefaultFileListBudget"/>, the step is refused as a
    /// <see cref="PlanIssue"/> rather than silently replacing files the user chose to keep.
    /// A cut never yields <c>KeepBothStep(Move: true)</c> until an ADR accepts an OS move
    /// (MoveFileWithProgress with MOVEFILE_COPY_ALLOWED) as a source-deleting path next to
    /// robocopy /MOV (product invariant 1); until then the dialog does not offer keep-both
    /// for a cut, and KeepBoth there is treated as Skip.
    /// </summary>
    public static ExecutionPlan Apply(
        ScanResult scan,
        ConflictPolicy configured,
        ConflictChoice? choice,
        Func<string, bool> destinationTaken) =>
        throw new NotImplementedException();
}
