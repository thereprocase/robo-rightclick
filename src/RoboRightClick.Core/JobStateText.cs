using System.Globalization;

namespace RoboRightClick.Core;

/// <summary>
/// The semantic colour family of a job's state (docs/gridline.md): cyan for live work,
/// amber when the job waits on the user or is paused, green for done, red for failure.
/// The host maps each tone to a Gridline colour.
/// </summary>
public enum StateTone
{
    Neutral,
    Live,
    Attention,
    Positive,
    Danger,
}

/// <summary>The counts block at the top of the Jobs window.</summary>
public sealed record JobCounts(int Active, int Paused, int NeedAttention, int Finished);

/// <summary>
/// Every sentence and label the progress window, Jobs window and conflict dialog show
/// about a job. Window text may name files in every mode (it stays in memory); window
/// titles may not in ephemeral mode, because titles are visible to other programs and the
/// task switcher.
/// </summary>
public static class JobStateText
{
    /// <summary>
    /// What the job is doing, as a sentence. <paramref name="maxConcurrentJobs"/> fills in
    /// the queue limit; <paramref name="pauseLatched"/> says a pause was requested before
    /// the job reached Running (it applies when copying starts).
    /// </summary>
    public static string For(JobSnapshot job, int maxConcurrentJobs = 0, bool pauseLatched = false)
    {
        if (job.CancelRequested && !JobStates.IsTerminal(job.State))
        {
            return "Canceling…";
        }
        if (job.RetriedBy is not null && JobStates.IsTerminal(job.State))
        {
            return "Tried again: see the newer job";
        }
        switch (job.State)
        {
            case JobState.Queued when pauseLatched:
                // The label already says PAUSED; the text says what the job is waiting for.
                return QueueReason(job, maxConcurrentJobs) is { } waitingFor
                    ? "Paused; " + char.ToLowerInvariant(waitingFor[0]) + waitingFor[1..]
                    : "Paused before starting";
            case JobState.Queued:
                return QueueReason(job, maxConcurrentJobs) ?? "Starting…";
            case JobState.Scanning:
                var found = job.TotalFiles == 0
                    ? "Scanning…"
                    : $"Discovered {DisplayText.Items(job.TotalFiles)} ({DisplayText.Bytes(job.TotalBytes)})";
                return pauseLatched ? found + "; will pause before copying" : found;
            case JobState.AwaitingDecision:
                return "Waiting for you to choose Replace or Skip";
            case JobState.Running:
                return job.Verb == TransferVerb.Move ? "Moving" : "Copying";
            case JobState.Paused:
                return "Paused";
            case JobState.Finalizing:
                return "Finishing…";
            case JobState.Done when job.MayBeIncomplete > 0:
                return job.MayBeIncomplete == 1
                    ? "Done; 1 file may be incomplete"
                    : string.Create(CultureInfo.InvariantCulture, $"Done; {job.MayBeIncomplete:N0} files may be incomplete");
            case JobState.Done:
                return job.NoOp ? "Nothing to do: every item is already there" : "Done";
            case JobState.DoneWithErrors:
                var problems = job.ErrorCount + job.RefusedCount;
                return problems == 1
                    ? "Done, 1 item had a problem"
                    : string.Create(CultureInfo.InvariantCulture, $"Done, {problems:N0} items had problems");
            case JobState.Failed:
                return job.FailureReason is { Length: > 0 } reason ? "Failed: " + reason : "Failed";
            case JobState.Canceled:
                // Damaged (the cancel interrupted them) and may-be-incomplete (robocopy failed
                // on them first) never name the same file.
                return (job.DamagedOnCancel + job.MayBeIncomplete, job.ErrorCount) switch
                {
                    (1, _) => "Canceled; 1 file may be incomplete",
                    (> 1 and var n, _) => string.Create(CultureInfo.InvariantCulture, $"Canceled; {n:N0} files may be incomplete"),
                    (_, 1) => "Canceled; 1 item had a problem",
                    (_, > 1 and var errors) => string.Create(CultureInfo.InvariantCulture, $"Canceled; {errors:N0} items had problems"),
                    _ => "Canceled",
                };
            default:
                return job.State.ToString();
        }
    }

    /// <summary>Why a queued job has not started, or null when it is about to.</summary>
    private static string? QueueReason(JobSnapshot job, int maxConcurrentJobs) => job.Wait switch
    {
        JobWait.OverlappingJob => "Waiting for another paste into this folder",
        JobWait.ConcurrencyLimit when maxConcurrentJobs > 0 =>
            string.Create(CultureInfo.InvariantCulture, $"Waiting (limit of {maxConcurrentJobs} {(maxConcurrentJobs == 1 ? "job" : "jobs")})"),
        JobWait.ConcurrencyLimit => "Waiting for a free job slot",
        JobWait.ScanLimit => "Waiting to scan",
        _ => null,
    };

    /// <summary>The short UPPERCASE state label drawn in the state's colour.</summary>
    public static string Label(JobSnapshot job, bool pauseLatched = false)
    {
        if (job.CancelRequested && !JobStates.IsTerminal(job.State))
        {
            return "CANCELING";
        }
        if (job.RetriedBy is not null && JobStates.IsTerminal(job.State))
        {
            return "RETRIED";
        }
        return job.State switch
        {
            JobState.Queued when pauseLatched => "PAUSED",
            JobState.Queued => "QUEUED",
            JobState.Scanning => "SCANNING",
            JobState.AwaitingDecision => "DECIDE",
            JobState.Running => "RUNNING",
            JobState.Paused => "PAUSED",
            JobState.Finalizing => "FINISHING",
            JobState.Done => "DONE",
            JobState.DoneWithErrors => "ERRORS",
            JobState.Failed => "FAILED",
            JobState.Canceled => "CANCELED",
            _ => job.State.ToString().ToUpperInvariant(),
        };
    }

    public static StateTone Tone(JobSnapshot job, bool pauseLatched = false)
    {
        if (job.CancelRequested && !JobStates.IsTerminal(job.State))
        {
            return StateTone.Neutral;
        }
        if (job.RetriedBy is not null && JobStates.IsTerminal(job.State))
        {
            // The newer job carries the outcome now; this row no longer needs the user.
            return StateTone.Neutral;
        }
        return job.State switch
        {
            JobState.Queued when pauseLatched => StateTone.Attention,
            JobState.AwaitingDecision or JobState.Paused => StateTone.Attention,
            JobState.Running or JobState.Finalizing => StateTone.Live,
            JobState.Done when job.MayBeIncomplete > 0 => StateTone.Attention,
            JobState.Done => StateTone.Positive,
            JobState.DoneWithErrors or JobState.Failed => StateTone.Danger,
            JobState.Canceled when job.OutcomeNeedsUser => StateTone.Attention,
            _ => StateTone.Neutral,
        };
    }

    /// <summary>
    /// The error summary's primary button, from what the button does
    /// (<see cref="RetryRules.ActionFor"/>): "Try again (N)" with N =
    /// <see cref="JobSnapshot.RetryCount"/> when it repeats files, "Finish copying/moving them"
    /// when it repeats the files a cancel left possibly incomplete, "Try again" for a Failed job
    /// and for a whole re-run of a finished one, "Paste everything again" for a whole re-run of
    /// a canceled one (it redoes what the cancel stopped), or null when there is nothing to try
    /// again (errors no retry can repeat, such as a link folder that could not be created),
    /// including once "Try again" has been used (<see cref="JobSnapshot.RetriedBy"/>).
    /// </summary>
    public static string? TryAgainLabel(JobSnapshot job) => RetryRules.ActionFor(job) switch
    {
        RetryAction.WholePaste when job.State == JobState.Canceled => WholePasteLabel,
        RetryAction.WholePaste => "Try again",
        RetryAction.RepeatFiles => job.State switch
        {
            JobState.Failed => "Try again",
            JobState.Canceled when job.DamagedOnCancel > 0 => FinishLabel(job),
            _ => string.Create(CultureInfo.InvariantCulture, $"Try again ({job.RetryCount:N0})"),
        },
        _ => null,
    };

    /// <summary>The button of a whole re-run of a canceled paste; never "Finish ... them", which would promise a few files.</summary>
    public const string WholePasteLabel = "Paste everything again";

    /// <summary>Repeats the files a cancel left possibly incomplete.</summary>
    public static string FinishLabel(JobSnapshot job) => job.Verb == TransferVerb.Move ? "Finish moving them" : "Finish copying them";

    /// <summary>
    /// The error summary's heading. A Failed job whose robocopy run started says the paste
    /// stopped and that files may be incomplete, never "Nothing was copied": robocopy
    /// allocates full length first, so a file it was writing can look whole. A finished or
    /// canceled job whose "Try again" re-runs the whole paste says so. A job tried again says
    /// so first.
    /// </summary>
    public static string SummaryHeading(JobSnapshot job)
    {
        var done = job.Verb == TransferVerb.Move ? "moved" : "copied";
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;
        var heading = job.State switch
        {
            JobState.Failed when job.MayBeIncomplete > 0 => $"The paste into {into} stopped. Files at the destination may be incomplete.",
            JobState.Failed => $"Nothing was {done} to {into}.",
            JobState.Canceled when job.DamagedOnCancel + job.MayBeIncomplete > 0 => $"The paste into {into} was canceled. Files at the destination may be incomplete.",
            JobState.Canceled => $"The paste into {into} was canceled.",
            JobState.Done when job.MayBeIncomplete > 0 => job.MayBeIncomplete == 1
                ? $"Everything else was {done} to {into}. 1 file the earlier paste may have left incomplete was kept as it is."
                : string.Create(CultureInfo.InvariantCulture, $"Everything else was {done} to {into}. {job.MayBeIncomplete:N0} files the earlier paste may have left incomplete were kept as they are."),
            JobState.Done => job.SkippedAppeared == 1
                ? $"Everything was {done} to {into} except 1 file whose name appeared there during the paste."
                : string.Create(CultureInfo.InvariantCulture, $"Everything was {done} to {into} except {job.SkippedAppeared:N0} files whose names appeared there during the paste."),
            _ => (job.ErrorCount + job.RefusedCount) switch
            {
                1 => $"1 item could not be {done} to {into}. Everything else finished.",
                var n => string.Create(CultureInfo.InvariantCulture, $"{n:N0} items could not be {done} to {into}. Everything else finished."),
            },
        };
        if (job.RetriedBy is not null)
        {
            return heading + " Tried again: see the newer job in the Jobs window.";
        }
        return RetryRules.ActionFor(job) == RetryAction.WholePaste && job.State != JobState.Failed
            ? heading + " " + WholePasteSentence(job)
            : heading;
    }

    /// <summary>
    /// Why the button repeats everything: the job could not tell which of robocopy's files
    /// failed. After a cancel it says outright that the canceled work runs again.
    /// </summary>
    private static string WholePasteSentence(JobSnapshot job) => job.State == JobState.Canceled
        ? $"{WholePasteLabel} runs the whole paste again with a new scan, including what you canceled: the job could not tell which files robocopy had finished."
        : "Try again runs the whole paste again with a new scan: the job could not tell which files robocopy had finished.";

    /// <summary>
    /// The explanation above the error summary's "May be incomplete" list
    /// (<see cref="JobSnapshot.MayBeIncomplete"/>), from what was left and why, not from the end
    /// state alone: files robocopy failed on or stopped inside, and files the earlier paste may
    /// have left half written that the user chose to keep (<see cref="JobSnapshot.KeptIncomplete"/>).
    /// The advice points at "Try again" only when that button repeats those files
    /// (<see cref="RetryRules.RepeatsMayBeIncomplete"/>); kept files are never repeated by it.
    /// </summary>
    public static string MayBeIncompleteText(JobSnapshot job)
    {
        var kept = Math.Clamp(job.KeptIncomplete, 0, job.MayBeIncomplete);
        var stopped = job.MayBeIncomplete - kept;
        var sentences = new List<string>();
        if (stopped > 0)
        {
            var files = stopped == 1 ? "1 file was" : string.Create(CultureInfo.InvariantCulture, $"{stopped:N0} files were");
            sentences.Add(job.State == JobState.Canceled
                ? $"{files} not finished: robocopy reported an error on them, or stopped while writing them, before you canceled."
                : $"{files} not finished: robocopy reported an error on them, or stopped while writing them.");
        }
        if (kept > 0)
        {
            sentences.Add(kept == 1
                ? "1 file the earlier paste may have left incomplete was kept, as you chose."
                : string.Create(CultureInfo.InvariantCulture, $"{kept:N0} files the earlier paste may have left incomplete were kept, as you chose."));
        }
        sentences.Add("They can look complete but hold only part of the data.");
        if (stopped > 0)
        {
            sentences.Add(RetryRules.RepeatsMayBeIncomplete(job) && TryAgainLabel(job) is { } label
                ? $"{label}, or check them before you use them."
                : "Check them before you use them.");
        }
        if (kept > 0)
        {
            sentences.Add($"To replace the kept {(kept == 1 ? "file" : "files")}, {VerbName(job.Verb)} the source again and choose Replace.");
        }
        return string.Join(" ", sentences);
    }

    /// <summary>
    /// The explanation above the error summary's list of files a cancel left possibly
    /// incomplete (<see cref="JobSnapshot.DamagedOnCancel"/>). It names "Finish copying/moving
    /// them" only when that is the button: a cut whose sources are already gone has nothing to
    /// finish, and no button.
    /// </summary>
    public static string DamagedText(JobSnapshot job)
    {
        var files = job.DamagedOnCancel == 1 ? "1 file was" : string.Create(CultureInfo.InvariantCulture, $"{job.DamagedOnCancel:N0} files were");
        var advice = TryAgainLabel(job) == FinishLabel(job)
            ? $"{FinishLabel(job)}, or check them before you use them."
            : "Check them before you use them.";
        return $"{files} being written when you canceled. They can look complete but hold only part of the data. {advice}";
    }

    /// <summary>The menu name of the verb the user started with: a cut pastes as a move.</summary>
    public static string VerbName(TransferVerb verb) => verb == TransferVerb.Move ? "Robo-Cut" : "Robo-Copy";

    /// <summary>
    /// A window title naming the job: "Robo-Copy: 3 items → Archive", or the item's name when
    /// there is one. Ephemeral jobs get "Robo-Copy job": titles reach the task switcher and
    /// other programs, so they never carry names in that mode.
    /// </summary>
    public static string Title(JobSnapshot job)
    {
        var verb = VerbName(job.Verb);
        if (job.Logging == LoggingMode.Ephemeral)
        {
            return verb + " job";
        }
        var what = job.Sources.Count == 1 ? DisplayText.SourcesSummary(job.Sources) : DisplayText.Items(job.Sources.Count);
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;
        return $"{verb}: {what} → {into}";
    }

    /// <summary>Whole percent done, by bytes when there are any, else by files; 100 only once the job is Done.</summary>
    public static int Percent(JobSnapshot job)
    {
        if (job.State == JobState.Done)
        {
            return 100;
        }
        double fraction = job.TotalBytes > 0 ? (double)job.DoneBytes / job.TotalBytes
            : job.TotalFiles > 0 ? (double)job.DoneFiles / job.TotalFiles
            : 0;
        return Math.Clamp((int)Math.Floor(fraction * 100), 0, 99);
    }

    /// <summary>"512 KB of 1.00 MB · 3 of 4 files", or empty before the scan has totals.</summary>
    public static string Amounts(JobSnapshot job)
    {
        if (job.TotalFiles == 0 && job.TotalBytes == 0)
        {
            return string.Empty;
        }
        var files = job.TotalFiles == 1 ? "file" : "files";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayText.Bytes(job.DoneBytes)} of {DisplayText.Bytes(job.TotalBytes)} · {job.DoneFiles:N0} of {job.TotalFiles:N0} {files}");
    }

    /// <summary>"1.20 GB/s · 4 min left" while data moves; empty otherwise (no speed while paused).</summary>
    public static string Rate(JobSnapshot job)
    {
        if (job.State != JobState.Running || job.CancelRequested)
        {
            return string.Empty;
        }
        var parts = new List<string>();
        if (job.BytesPerSecond is double speed and > 0)
        {
            parts.Add(DisplayText.Speed(speed));
        }
        if (job.Remaining is { } remaining)
        {
            parts.Add(DisplayText.Duration(remaining) + " left");
        }
        return string.Join(" · ", parts);
    }

    public static JobCounts Counts(IReadOnlyList<JobSnapshot> jobs) => new(
        Active: jobs.Count(j => !JobStates.IsTerminal(j.State)),
        Paused: jobs.Count(j => j.State == JobState.Paused),
        NeedAttention: jobs.Count(j => j.NeedsAttention),
        Finished: jobs.Count(j => JobStates.IsTerminal(j.State)));

    /// <summary>
    /// The Jobs window's status bar cells, UPPERCASE as Gridline draws them:
    /// "3 JOBS", "1.20 GB/S", "ETA 4 MIN", "1 NEEDS ATTENTION", "EPHEMERAL". Path-free.
    /// </summary>
    public static IReadOnlyList<string> StatusCells(IReadOnlyList<JobSnapshot> jobs, LoggingMode currentMode)
    {
        var cells = new List<string>();
        var active = jobs.Where(j => !JobStates.IsTerminal(j.State)).ToList();
        cells.Add(active.Count switch
        {
            0 => "NO ACTIVE JOBS",
            1 => "1 JOB",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} JOBS"),
        });
        var running = active.Where(j => j.State == JobState.Running && !j.CancelRequested).ToList();
        var speed = running.Sum(j => j.BytesPerSecond ?? 0);
        if (speed > 0)
        {
            cells.Add(DisplayText.Speed(speed).ToUpperInvariant());
        }
        if (running.Select(j => j.Remaining).Where(r => r is not null).Max() is { } eta)
        {
            cells.Add("ETA " + DisplayText.Duration(eta).ToUpperInvariant());
        }
        var attention = jobs.Count(j => j.NeedsAttention);
        if (attention > 0)
        {
            cells.Add(attention == 1 ? "1 NEEDS ATTENTION" : string.Create(CultureInfo.InvariantCulture, $"{attention} NEED ATTENTION"));
        }
        if (currentMode == LoggingMode.Ephemeral)
        {
            cells.Add("EPHEMERAL");
        }
        return cells;
    }
}
