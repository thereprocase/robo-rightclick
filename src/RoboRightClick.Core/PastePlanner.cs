namespace RoboRightClick.Core;

public enum TransferVerb
{
    Copy,
    Move,
}

public sealed record SourceItem(string Path, bool IsDirectory);

public sealed record PasteRequest(IReadOnlyList<SourceItem> Sources, string Destination, TransferVerb Verb);

/// <summary>
/// The facts about the live file system that planning depends on. The host
/// answers these from the real disk; tests answer them from a fixture.
/// </summary>
public interface IPlanningFacts
{
    bool Exists(string path);

    /// <summary>True when a rename between the two paths would succeed without copying data.</summary>
    bool SameVolume(string a, string b);
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

    /// <summary>
    /// Room left for file names on one robocopy command line. CreateProcess
    /// caps the whole line at 32,767 characters; the remainder covers the exe
    /// path, both directories, flags and user extra arguments.
    /// </summary>
    public const int DefaultFileListBudget = 24_000;

    public static PastePlan Plan(PasteRequest request, IPlanningFacts facts, int fileListBudget = DefaultFileListBudget)
    {
        var destination = WinPath.TrimTrailingSeparators(request.Destination);
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

            if (source.IsDirectory && (WinPath.AreSame(destination, path) || WinPath.IsStrictlyUnder(destination, path)))
            {
                rejected.Add(new(path, SubfolderReason));
                continue;
            }

            if (WinPath.AreSame(parent, destination))
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
            foreach (var chunk in ChunkByLength(fileBatches[parent], fileListBudget))
            {
                steps.Add(new RobocopyStep(parent, destination, chunk, Recursive: false, Move: move));
            }
        }

        return new PastePlan(steps, rejected, noOps);
    }

    private static IEnumerable<IReadOnlyList<string>> ChunkByLength(List<string> names, int budget)
    {
        var chunk = new List<string>();
        var used = 0;
        foreach (var name in names)
        {
            var cost = RobocopyArgs.Quote(name).Length + 1;
            if (chunk.Count > 0 && used + cost > budget)
            {
                yield return chunk;
                chunk = [];
                used = 0;
            }
            chunk.Add(name);
            used += cost;
        }
        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }
}
