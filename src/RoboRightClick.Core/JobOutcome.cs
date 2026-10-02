namespace RoboRightClick.Core;

/// <summary>What one executed step produced.</summary>
/// <param name="CompletedSources">Source paths the step finished (robocopy FileReported, or an in-process copy/rename).</param>
/// <param name="ExitCode">Robocopy's exit code; null for in-process steps.</param>
/// <param name="Failure">
/// Set when the step could not run at all: launch failure, an untrusted pipe client, an
/// exception. Producers must pass one plain sentence with no path or file name in it: a
/// Win32 system message for an error code, or a fixed host sentence, never an exception's
/// Message (which often embeds a path). <see cref="FailureText"/> filters it again and
/// falls back to a fixed sentence, but that filter is a heuristic, not the guarantee.
/// </param>
public sealed record StepOutcome(
    IReadOnlyList<string> CompletedSources,
    IReadOnlyList<ErrorReported> Errors,
    RobocopyExitCode? ExitCode,
    string? Failure)
{
    /// <summary>
    /// A rename failed with ERROR_NOT_SAME_DEVICE: the planner's same-volume answer was
    /// wrong (a mount point, for example). Not an error: the job re-plans the item as a
    /// robocopy move, so this never counts towards DoneWithErrors.
    /// </summary>
    public bool ReplanAsMove { get; init; }
}

public static class JobOutcome
{
    /// <summary>
    /// The terminal state after Finalizing. Failed is reserved for jobs where nothing
    /// could run: per-file errors are DoneWithErrors, which is what offers "Try again".
    /// </summary>
    /// <param name="planIssues">Items refused at planning (into own subfolder, a drive root); Explorer reports these as errors.</param>
    public static JobState FinalState(IReadOnlyList<StepOutcome> outcomes, bool canceled, int planIssues)
    {
        if (canceled)
        {
            return JobState.Canceled;
        }

        var anyCompleted = outcomes.Any(o => o.CompletedSources.Count > 0);
        var anyBroken = outcomes.Any(o => o.Failure is not null || o.ExitCode is { FatalError: true });
        if (anyBroken && !anyCompleted)
        {
            return JobState.Failed;
        }

        var anyErrors = anyBroken
            || planIssues > 0
            || outcomes.Any(o => o.Errors.Count > 0 || o.ExitCode is { SomeCopiesFailed: true });
        return anyErrors ? JobState.DoneWithErrors : JobState.Done;
    }
}

/// <summary>
/// Plain-language reasons for a Failed job (<see cref="JobSnapshot.FailureReason"/>). Built
/// only from Windows error codes, robocopy operation names and system messages, never from
/// a path, so the text is safe in every mode.
/// </summary>
public static class FailureText
{
    /// <summary>
    /// One sentence for the first thing that broke: a launch failure, an untrusted pipe
    /// client, robocopy exit 16 with its first ERROR ("Access to the destination folder was
    /// denied (error 5)."), or a fatal exit with no ERROR line ("Robocopy stopped with a
    /// fatal error (exit code 16)."). Common codes get their own wording: 2/3 not found, 5
    /// access denied, 32 in use, 53/67 network path not found, 112 disk full, 1314 privilege.
    /// </summary>
    /// <remarks>
    /// <see cref="StepOutcome.Failure"/> and system messages are free text from outside Core,
    /// so they are used only when they carry nothing that looks like a path or a quoted
    /// name; otherwise a fixed sentence stands in. Losing detail is the safe direction.
    /// </remarks>
    public static string Describe(StepOutcome outcome)
    {
        if (outcome.Failure is { } failure)
        {
            var text = failure.Trim();
            return text.Length > 0 && IsPathFree(text) ? AsSentence(text) : CouldNotStart;
        }
        if (outcome.Errors.Count > 0)
        {
            return ForError(outcome.Errors[0]);
        }
        if (outcome.ExitCode is { } code)
        {
            if (code.Value < 0)
            {
                return "Robocopy stopped unexpectedly.";
            }
            if (code.FatalError)
            {
                return $"Robocopy stopped with a fatal error (exit code {code.Value}).";
            }
            if (code.SomeCopiesFailed)
            {
                return $"Some files could not be copied (exit code {code.Value}).";
            }
        }
        return "The transfer did not finish.";
    }

    private const string CouldNotStart = "The transfer could not be started.";

    private static string ForError(ErrorReported error)
    {
        var known = error.Code switch
        {
            2 or 3 => "The source or destination could not be found",
            5 => Side(error.Operation) switch
            {
                Location.Destination => "Access to the destination folder was denied",
                Location.Source => "Access to the source was denied",
                _ => "Access to a file was denied",
            },
            32 => "A file is in use by another program",
            53 or 67 => "The network path was not found",
            112 => "There is not enough space on the destination disk",
            1314 => "A required privilege is not held",
            _ => null,
        };
        if (known is not null)
        {
            return $"{known} (error {error.Code}).";
        }
        var message = error.Message.Trim();
        return message.Length > 0 && IsPathFree(message)
            ? $"Windows error {error.Code}: {AsSentence(message)}"
            : $"Windows error {error.Code}.";
    }

    private enum Location
    {
        Unknown,
        Source,
        Destination,
    }

    /// <summary>
    /// Which side an operation names: "Accessing Destination Directory", "Creating Destination
    /// Directory", "Scanning Source Directory". "Copying File" names neither, and guessing
    /// would tell the user to look in the wrong place.
    /// </summary>
    private static Location Side(string operation)
    {
        if (operation.Contains("Destination", StringComparison.OrdinalIgnoreCase))
        {
            return Location.Destination;
        }
        return operation.Contains("Source", StringComparison.OrdinalIgnoreCase) ? Location.Source : Location.Unknown;
    }

    /// <summary>
    /// False for anything that could carry a path or a file name
    /// (<see cref="PathHeuristic.IsPathFree"/>), or for text longer than
    /// <see cref="PathHeuristic.MaxPassedThroughLength"/>.
    /// </summary>
    private static bool IsPathFree(string text) => PathHeuristic.IsPathFree(text);

    private static string AsSentence(string text) => text[^1] is '.' or '!' or '?' ? text : text + ".";
}

public static class RetryPlanner
{
    /// <summary>
    /// The child job for "Try again (N)": the files <see cref="StepLedger.Retryable"/>
    /// returned, as non-recursive robocopy steps grouped by (source folder, destination
    /// folder), with the original step's Move flag, chunked like <see cref="PastePlanner"/>.
    /// Policy is Replace: each of these files either failed with an error robocopy reported
    /// (so robocopy touched it) or was in flight when its run died, and robocopy pre-allocates
    /// full-length files, so a partial copy must not be mistaken for a finished one.
    /// Failed in-process steps (rename, duplicate, keep-both) are repeated as they were.
    /// <see cref="ExecutionPlan.PresentBeforeRun"/> carries over from the original, and
    /// <see cref="ExecutionPlan.LinkFolders"/> and Kept are empty. Returns null when there is
    /// nothing to repeat: <paramref name="retryable"/> and <paramref name="failedInProcessSteps"/>
    /// both empty. (A job whose only failure was a keep-both copy still offers "Try again".)
    /// Robocopy steps come first, then the in-process steps in their original order, as
    /// keep-both steps ran last originally.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A retryable entry that is not a file of a robocopy step of <paramref name="original"/>
    /// (robocopy cannot write it under another name), or an index outside the plan.
    /// </exception>
    public static ExecutionPlan? ForFailures(
        ExecutionPlan original,
        IReadOnlyList<(int StepIndex, PlannedFile File)> retryable,
        IReadOnlyList<int> failedInProcessSteps)
    {
        if (retryable.Count == 0 && failedInProcessSteps.Count == 0)
        {
            return null;
        }

        // Grouped by source folder, destination folder and verb, in first-failure order.
        var groups = new Dictionary<string, (string Source, string Destination, bool Move, List<PlannedFile> Files)>(WinPath.Comparer);
        var order = new List<string>();
        foreach (var (stepIndex, file) in retryable)
        {
            if ((uint)stepIndex >= (uint)original.Steps.Count || original.Steps[stepIndex].Step is not RobocopyStep step)
            {
                throw new ArgumentException("Only files of the plan's robocopy steps can be retried file by file.", nameof(retryable));
            }
            if (!WinPath.Comparer.Equals(WinPath.GetFileName(file.SourcePath), WinPath.GetFileName(file.DestinationPath)))
            {
                throw new ArgumentException("A robocopy retry cannot write a file under a different name.", nameof(retryable));
            }
            var source = WinPath.GetParent(file.SourcePath);
            var destination = WinPath.GetParent(file.DestinationPath);
            var key = source + "\0" + destination + "\0" + (step.Move ? "move" : "copy");
            if (!groups.TryGetValue(key, out var group))
            {
                group = (source, destination, step.Move, []);
                groups[key] = group;
                order.Add(key);
            }
            group.Files.Add(file);
        }

        var steps = new List<ExecutionStep>();
        foreach (var key in order)
        {
            var (source, destination, move, files) = groups[key];
            foreach (var chunk in ExecutionPlanner.ChunkByNameLength(files, PastePlanner.DefaultFileListBudget))
            {
                var names = chunk.Select(f => WinPath.GetFileName(f.SourcePath)).ToList();
                steps.Add(new ExecutionStep(
                    new RobocopyStep(source, destination, names, Recursive: false, Move: move),
                    ConflictPolicy.Replace,
                    chunk));
            }
        }

        foreach (var index in failedInProcessSteps.Distinct().Order())
        {
            if ((uint)index >= (uint)original.Steps.Count || original.Steps[index].Step is RobocopyStep)
            {
                throw new ArgumentException("Only in-process steps are repeated whole.", nameof(failedInProcessSteps));
            }
            steps.Add(original.Steps[index]);
        }

        return new ExecutionPlan(steps, original.PresentBeforeRun, [], [], []);
    }
}
