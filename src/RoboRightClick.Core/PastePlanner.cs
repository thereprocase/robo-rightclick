namespace RoboRightClick.Core;

public enum TransferVerb
{
    Copy,
    Move,
}

/// <param name="IsDirectoryLink">A junction or directory symlink selected itself (not one found inside a tree).</param>
public sealed record SourceItem(string Path, bool IsDirectory, bool IsDirectoryLink = false);

public sealed record PasteRequest(IReadOnlyList<SourceItem> Sources, string Destination, TransferVerb Verb);

/// <summary>
/// A paste as it arrives from the clipboard or the CLI: raw, untrusted paths and nothing
/// resolved yet. Resolving it touches the disk, so it happens in the job's Scanning state
/// on a worker thread, never on the UI thread that also serves COM calls.
/// </summary>
public sealed record PasteOrder(IReadOnlyList<string> Sources, string Destination, TransferVerb Verb);

public enum ItemKind
{
    Missing,
    File,
    Directory,

    /// <summary>A junction or directory symlink, reported without following it.</summary>
    DirectoryLink,
}

/// <summary>
/// The facts about the live file system that planning depends on. The host
/// answers these from the real disk; tests answer them from a fixture.
/// </summary>
public interface IPlanningFacts
{
    bool Exists(string path);

    /// <summary>True when a rename between the two paths would succeed without copying data.</summary>
    bool SameVolume(string a, string b);

    /// <summary>What is at <paramref name="path"/>; a reparse point at the path itself is not followed.</summary>
    ItemKind KindOf(string path);

    /// <summary>
    /// The location with junctions, symlinks, 8.3 names and SUBST drives resolved
    /// (GetFinalPathNameByHandle), or the input when it cannot be resolved. Used only to
    /// compare locations, never as a robocopy argument.
    /// </summary>
    string FinalPath(string path);
}

public abstract record PlanStep;

/// <summary>One robocopy invocation.</summary>
/// <param name="FileNames">Empty means the whole directory tree (used with <paramref name="Recursive"/>).</param>
public sealed record RobocopyStep(
    string SourceDirectory,
    string DestinationDirectory,
    IReadOnlyList<string> FileNames,
    bool Recursive,
    bool Move) : PlanStep;

/// <summary>Same-volume move: a rename, exactly what Explorer does.</summary>
public sealed record RenameStep(string Source, string Destination) : PlanStep;

/// <summary>A single file copied in-process to a new name (robocopy cannot rename).</summary>
public sealed record DuplicateFileStep(string Source, string Destination) : PlanStep;

public sealed record PlanIssue(string Path, string Reason);

public sealed record PastePlan(
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<PlanIssue> Rejected,
    IReadOnlyList<PlanIssue> NoOps);

/// <summary>
/// Turns a paste request into the steps Explorer would take, expressed as
/// robocopy invocations wherever bulk data has to move.
/// </summary>
public static class PastePlanner
{
    public const string SubfolderReason = "The destination folder is a subfolder of the source folder.";
    public const string SameFolderMoveReason = "The source and destination are the same folder.";
    public const string RootReason = "Copying an entire drive is not supported.";
    public const string MissingReason = "This item could not be found. It may have been moved or deleted.";
    public const string DestinationReason = "The destination folder could not be found.";
    public const string LinkReason =
        "Folder links (junctions and symbolic links) can only be Robo-moved within the same drive. Use Explorer's Paste for these.";

    /// <summary>
    /// The entry point for a job: validates every path with <see cref="PathPolicy"/>,
    /// resolves what each source is, then plans. Refused items become
    /// <see cref="PastePlan.Rejected"/> entries and the rest still run, as in Explorer.
    /// </summary>
    public static PastePlan Plan(PasteOrder order, IPlanningFacts facts, int fileListBudget = DefaultFileListBudget)
    {
        var rejected = new List<PlanIssue>();
        var destinationKind = PathPolicy.IsAcceptable(order.Destination) ? facts.KindOf(order.Destination) : ItemKind.Missing;
        if (destinationKind is not (ItemKind.Directory or ItemKind.DirectoryLink))
        {
            var reason = PathPolicy.IsAcceptable(order.Destination) ? DestinationReason : PathPolicy.UnsupportedPathReason;
            return new PastePlan([], order.Sources.Select(s => new PlanIssue(s, reason)).ToList(), []);
        }

        var items = new List<SourceItem>();
        foreach (var path in order.Sources)
        {
            if (!PathPolicy.IsAcceptable(path))
            {
                rejected.Add(new(path, PathPolicy.UnsupportedPathReason));
                continue;
            }
            switch (facts.KindOf(path))
            {
                case ItemKind.Missing:
                    rejected.Add(new(path, MissingReason));
                    break;
                case ItemKind.File:
                    items.Add(new SourceItem(path, IsDirectory: false));
                    break;
                case ItemKind.Directory:
                    items.Add(new SourceItem(path, IsDirectory: true));
                    break;
                case ItemKind.DirectoryLink:
                    items.Add(new SourceItem(path, IsDirectory: true, IsDirectoryLink: true));
                    break;
            }
        }

        var plan = Plan(new PasteRequest(items, order.Destination, order.Verb), facts, fileListBudget);
        return plan with { Rejected = [.. rejected, .. plan.Rejected] };
    }

    /// <summary>
    /// Room left for file names on one robocopy command line. CreateProcess
    /// caps the whole line at 32,767 characters; the remainder covers the exe
    /// path, both directories, flags and user extra arguments.
    /// </summary>
    public const int DefaultFileListBudget = 24_000;

    public static PastePlan Plan(PasteRequest request, IPlanningFacts facts, int fileListBudget = DefaultFileListBudget)
    {
        var destination = WinPath.TrimTrailingSeparators(request.Destination);
        // The self/subfolder guards compare resolved locations too: a junction, 8.3 name
        // or SUBST drive can make the destination sit inside a source under a different
        // spelling, and robocopy would then recurse into its own output.
        var finalDestination = WinPath.TrimTrailingSeparators(facts.FinalPath(destination));
        var move = request.Verb == TransferVerb.Move;
        var steps = new List<PlanStep>();
        var rejected = new List<PlanIssue>();
        var noOps = new List<PlanIssue>();

        // Names this plan has already claimed in the destination, so two
        // duplicates created by the same paste can never pick the same name.
        var claimed = new HashSet<string>(WinPath.Comparer);
        bool IsTaken(string name) => claimed.Contains(name) || facts.Exists(WinPath.Combine(destination, name));

        // Loose files are batched per source folder so one robocopy run
        // (and its thread pool) covers all of them.
        var fileBatches = new Dictionary<string, List<string>>(WinPath.Comparer);
        var batchOrder = new List<string>();

        foreach (var source in request.Sources)
        {
            var path = WinPath.TrimTrailingSeparators(source.Path);
            if (WinPath.IsRoot(path))
            {
                rejected.Add(new(path, RootReason));
                continue;
            }

            var name = WinPath.GetFileName(path);
            var parent = WinPath.GetParent(path);
            var finalParent = WinPath.TrimTrailingSeparators(facts.FinalPath(parent));
            // A link itself is not followed: its location is its parent's plus its name.
            var finalPath = source.IsDirectoryLink
                ? WinPath.Combine(finalParent, name)
                : WinPath.TrimTrailingSeparators(facts.FinalPath(path));

            if (source.IsDirectory && (IsSelfOrUnder(destination, path) || IsSelfOrUnder(finalDestination, finalPath)))
            {
                rejected.Add(new(path, SubfolderReason));
                continue;
            }

            var sameFolder = WinPath.AreSame(parent, destination) || WinPath.AreSame(finalParent, finalDestination);

            if (source.IsDirectoryLink)
            {
                // Robocopy follows a link given as its source root (/XJD only skips links
                // inside a tree), so a cross-volume /MOVE would empty the link's target.
                // Only a same-volume rename, which moves the link itself, is safe.
                var linkTarget = WinPath.Combine(destination, name);
                if (move && !sameFolder && facts.SameVolume(path, destination) && !facts.Exists(linkTarget))
                {
                    claimed.Add(name);
                    steps.Add(new RenameStep(path, linkTarget));
                }
                else if (move && sameFolder)
                {
                    noOps.Add(new(path, SameFolderMoveReason));
                }
                else
                {
                    rejected.Add(new(path, LinkReason));
                }
                continue;
            }

            if (sameFolder)
            {
                if (move)
                {
                    noOps.Add(new(path, SameFolderMoveReason));
                    continue;
                }
                var copyName = DuplicateNamer.CopyName(name, source.IsDirectory, IsTaken);
                claimed.Add(copyName);
                var copyTarget = WinPath.Combine(destination, copyName);
                steps.Add(source.IsDirectory
                    ? new RobocopyStep(path, copyTarget, [], Recursive: true, Move: false)
                    : new DuplicateFileStep(path, copyTarget));
                continue;
            }

            var target = WinPath.Combine(destination, name);
            claimed.Add(name);

            if (move && facts.SameVolume(path, destination) && !facts.Exists(target))
            {
                steps.Add(new RenameStep(path, target));
                continue;
            }

            if (source.IsDirectory)
            {
                steps.Add(new RobocopyStep(path, target, [], Recursive: true, Move: move));
                continue;
            }

            if (!fileBatches.TryGetValue(parent, out var batch))
            {
                batch = [];
                fileBatches[parent] = batch;
                batchOrder.Add(parent);
            }
            batch.Add(name);
        }

        foreach (var parent in batchOrder)
        {
            foreach (var chunk in ExecutionPlanner.ChunkNamesByLength(fileBatches[parent], fileListBudget))
            {
                steps.Add(new RobocopyStep(parent, destination, chunk, Recursive: false, Move: move));
            }
        }

        return new PastePlan(steps, rejected, noOps);
    }

    private static bool IsSelfOrUnder(string candidate, string ancestor) =>
        WinPath.AreSame(candidate, ancestor) || WinPath.IsStrictlyUnder(candidate, ancestor);
}
