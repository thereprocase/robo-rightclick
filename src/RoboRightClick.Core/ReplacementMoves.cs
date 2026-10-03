namespace RoboRightClick.Core;

/// <summary>
/// The robocopy moves that stand in for renames which turned out to cross volumes
/// (<see cref="StepOutcome.ReplanAsMove"/>: a mount point, or two shares of one NAS whose
/// volume serials match). The job runs them as a second plan after its first.
/// </summary>
public static class ReplacementMovePlanner
{
    /// <summary>
    /// A same-volume rename turned out to cross volumes for an item robocopy cannot move:
    /// robocopy cannot rename, so a keep-both name cannot be kept, and an OS move would be an
    /// app-initiated source deletion (invariant 1).
    /// </summary>
    public const string CannotMoveUnderNewNameReason =
        "This item could not be moved under a new name, because the destination turned out to be on a different drive.";

    /// <summary>
    /// Each re-planned rename scanned again on its own and planned as a robocopy move. Ask
    /// with SkipAll is the documented way to get Skip semantics both with and without
    /// conflicts: kept names are left out by construction (so a /MOV run never sees them), and
    /// anything that appears later is skipped by Ask's flags. Refused, as plan issues: a rename
    /// to a different name (<see cref="CannotMoveUnderNewNameReason"/>), a folder link
    /// (<see cref="PastePlanner.LinkReason"/>: robocopy follows a link given as its source root
    /// and /MOVE would empty the target), a file whose name robocopy would read as a switch
    /// (<see cref="PastePlanner.SwitchLikeNameReason"/>; the planner let it through because a
    /// rename never names it to robocopy), and a source that is gone.
    /// </summary>
    public static ExecutionPlan Plan(
        IReadOnlyList<RenameStep> renames,
        IPlanningFacts planning,
        IScanFacts scanning,
        Func<string, bool> destinationTaken,
        CancellationToken cancellationToken)
    {
        var steps = new List<PlanStep>();
        var refused = new List<PlanIssue>();
        foreach (var rename in renames)
        {
            var name = WinPath.GetFileName(rename.Source);
            if (!WinPath.Comparer.Equals(name, WinPath.GetFileName(rename.Destination)))
            {
                refused.Add(new PlanIssue(rename.Source, CannotMoveUnderNewNameReason));
                continue;
            }
            switch (planning.KindOf(rename.Source))
            {
                case ItemKind.File when RobocopyArgs.IsSwitchLikeName(name):
                    refused.Add(new PlanIssue(rename.Source, PastePlanner.SwitchLikeNameReason));
                    break;
                case ItemKind.File:
                    steps.Add(new RobocopyStep(
                        WinPath.GetParent(rename.Source), WinPath.GetParent(rename.Destination), [name], Recursive: false, Move: true));
                    break;
                case ItemKind.Directory:
                    steps.Add(new RobocopyStep(rename.Source, rename.Destination, [], Recursive: true, Move: true));
                    break;
                case ItemKind.DirectoryLink:
                    refused.Add(new PlanIssue(rename.Source, PastePlanner.LinkReason));
                    break;
                default:
                    refused.Add(new PlanIssue(rename.Source, PastePlanner.MissingReason));
                    break;
            }
        }
        var scan = JobScanner.Scan(new PastePlan(steps, refused, []), scanning, planning, null, cancellationToken);
        return ExecutionPlanner.Apply(scan, ConflictPolicy.Ask, new ConflictChoice.SkipAll(), destinationTaken);
    }
}
