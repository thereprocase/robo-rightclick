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
/// A "keep both" file, written in-process under a new name because robocopy cannot rename.
/// A copy uses CopyFileEx (COPY_FILE_FAIL_IF_EXISTS). A cut is allowed only within one
/// volume and runs as MoveFileEx with flags 0, a rename, which invariant 1 already permits;
/// a cross-volume cut never produces this step (<see cref="FileConflict.KeepBothAllowed"/>).
/// </summary>
public sealed record KeepBothStep(string Source, string Destination, bool Move) : PlanStep;

/// <summary>One step as it will actually run.</summary>
/// <param name="Policy">Conflict flags for a robocopy step (<see cref="RobocopyArgs.ConflictFlags"/>); ignored by other steps.</param>
/// <param name="Files">
/// Exactly the files this step is expected to report as done: every file the user chose to
/// keep is already left out. Progress totals, the ledger, cancel cleanup and retry all read
/// this list, so it must never include a file the step will not touch.
/// </param>
public sealed record ExecutionStep(PlanStep Step, ConflictPolicy Policy, IReadOnlyList<PlannedFile> Files);

/// <param name="PresentBeforeRun">Destinations the scan found already present (the conflicts). Cancel cleanup never deletes these.</param>
/// <param name="LinkFolders">Destination paths of skipped directory links; recreated empty in Finalizing (docs/parity.md).</param>
/// <param name="Kept">Conflicting files the user chose to leave alone (Skip, or not newer under KeepNewer); listed in the summary.</param>
/// <param name="Issues">Items refused at planning or scanning; Explorer reports these as errors.</param>
public sealed record ExecutionPlan(
    IReadOnlyList<ExecutionStep> Steps,
    IReadOnlyList<string> PresentBeforeRun,
    IReadOnlyList<string> LinkFolders,
    IReadOnlyList<FileConflict> Kept,
    IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes => Steps.Sum(s => s.Files.Sum(f => f.Source.Size));

    public long TotalFiles => Steps.Sum(s => (long)s.Files.Count);

    /// <summary>Nothing to run and nothing refused: every item is already where it was asked to go.</summary>
    public bool IsNoOp => Steps.Count == 0 && Issues.Count == 0;
}

public static class ExecutionPlanner
{
    /// <summary>
    /// Turns a scan plus the conflict answer into the steps that run. A file the user chose
    /// to keep is protected by construction (it is left out of every robocopy step) and
    /// never by robocopy's class filters or /XF: under /MOV robocopy can delete the source
    /// of a "same" file it skipped (LIKELY; M4 checks it), and /XF matching on full paths is
    /// unverified. Nothing here depends on either.
    /// </summary>
    /// <remarks>
    /// <para>Decision per conflict. Configured Replace / Skip apply to every conflict;
    /// KeepNewer replaces where <see cref="FileConflict.SourceIsNewer"/> and keeps the rest;
    /// Ask with conflicts takes <paramref name="choice"/> (ReplaceAll, SkipAll, or DecideEach,
    /// where a missing entry is Skip and KeepBoth where <see cref="FileConflict.KeepBothAllowed"/>
    /// is false is Skip). Ask with no conflicts runs every step with policy Ask, whose flags
    /// skip anything that appeared after the scan.</para>
    /// <para>Uniform cases keep the plan's steps unchanged: every conflict Replace (policy
    /// Replace), or a copy where every conflict is Skip (policy Skip) or decided by KeepNewer
    /// (policy KeepNewer). Robocopy's class filters are safe there because a copy deletes
    /// nothing.</para>
    /// <para>Every other case is split. Let K be the source directories holding a conflict
    /// that is not replaced (kept or keep-both). A file-batch step becomes: the Replace names
    /// (policy Replace), the non-conflicting names (policy Ask), each chunked like
    /// <see cref="PastePlanner"/>; kept names are dropped. A recursive step is cut along K:
    /// each directory on the path from the step's root to a directory in K becomes file-batch
    /// steps as above for its own files, and each child directory not on such a path becomes
    /// its own recursive step, policy Replace if it holds a replaced conflict, else Ask.
    /// Because every split directory still contains a kept file, a cut never leaves an
    /// emptied source folder behind. Keep-both files get one <see cref="KeepBothStep"/>
    /// each, named with <see cref="DuplicateNamer.KeepBothName"/> against
    /// <paramref name="destinationTaken"/> and names this plan already claimed.</para>
    /// <para>Steps come out in the scan's order (split pieces in place of their step), then
    /// the keep-both steps. Scan issues pass through to <see cref="ExecutionPlan.Issues"/>
    /// along with the paste plan's rejections.</para>
    /// </remarks>
    public static ExecutionPlan Apply(
        ScanResult scan,
        ConflictPolicy configured,
        ConflictChoice? choice,
        Func<string, bool> destinationTaken,
        int fileListBudget = PastePlanner.DefaultFileListBudget) =>
        throw new NotImplementedException();
}
