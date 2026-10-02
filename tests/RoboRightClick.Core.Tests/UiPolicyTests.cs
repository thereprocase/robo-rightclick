using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

// Policies behind the progress window, toasts, the conflict list and job state text.
// The path-freedom of batched toasts and the never-destroy-a-kept-file rule of the
// conflict list were each broken on purpose once and their tests seen failing
// (CLAUDE.md, verification honesty).

public class ProgressWindowPolicyTests
{
    private static JobSnapshot Job(JobState state) => DisplayAndTrayTests.Job(state);

    [Fact]
    public void Opens_one_second_after_creation() =>
        Assert.Equal(TimeSpan.FromSeconds(1), ProgressWindowPolicy.OpenDelay);

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Scanning)]
    [InlineData(JobState.AwaitingDecision)]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Paused)]
    [InlineData(JobState.Finalizing)]
    public void Opens_for_a_job_still_going_when_the_setting_is_on(JobState state)
    {
        Assert.True(ProgressWindowPolicy.ShouldOpen(Job(state), showProgressWindow: true));
        Assert.False(ProgressWindowPolicy.ShouldOpen(Job(state), showProgressWindow: false));
    }

    [Fact]
    public void Does_not_open_for_a_job_that_already_ended_cleanly()
    {
        Assert.False(ProgressWindowPolicy.ShouldOpen(Job(JobState.Done), showProgressWindow: true));
        Assert.False(ProgressWindowPolicy.ShouldOpen(Job(JobState.Done) with { NoOp = true }, showProgressWindow: true));
        Assert.False(ProgressWindowPolicy.ShouldOpen(Job(JobState.Canceled), showProgressWindow: true));
    }

    // Explorer shows its error dialog however fast a copy fails; a toast alone can be filed
    // away unseen by Do Not Disturb.
    [Fact]
    public void A_job_that_failed_before_the_delay_opens_straight_into_its_summary()
    {
        var outcomes = new[]
        {
            Job(JobState.DoneWithErrors),
            Job(JobState.Failed),
            Job(JobState.Canceled) with { DamagedOnCancel = 1 },
        };
        foreach (var job in outcomes)
        {
            Assert.True(ProgressWindowPolicy.ShouldOpen(job, showProgressWindow: true));
            Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(job));
            Assert.False(ProgressWindowPolicy.ShouldOpen(job, showProgressWindow: false));
            Assert.False(ProgressWindowPolicy.ShouldOpen(job with { Acknowledged = true }, showProgressWindow: true));
        }
    }

    [Fact]
    public void Does_not_open_for_a_job_that_no_longer_exists() =>
        Assert.False(ProgressWindowPolicy.ShouldOpen(null, showProgressWindow: true));

    [Fact]
    public void Clean_outcomes_close_and_outcomes_needing_the_user_show_the_summary()
    {
        Assert.Equal(ProgressWindowAction.Close, ProgressWindowPolicy.OnTerminal(Job(JobState.Done)));
        Assert.Equal(ProgressWindowAction.Close, ProgressWindowPolicy.OnTerminal(Job(JobState.Done) with { NoOp = true }));
        Assert.Equal(ProgressWindowAction.Close, ProgressWindowPolicy.OnTerminal(Job(JobState.Canceled)));
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(Job(JobState.DoneWithErrors)));
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(Job(JobState.Failed)));
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(Job(JobState.Canceled) with { DamagedOnCancel = 1 }));
    }

    [Fact]
    public void A_done_job_that_skipped_late_arrivals_reports_them()
    {
        // Explorer would have asked about these files; skipping them silently would leave the
        // user believing the destination holds the pasted versions.
        var job = Job(JobState.Done) with { SkippedAppeared = 2 };
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(job));
        Assert.True(ProgressWindowPolicy.ShouldOpen(job, showProgressWindow: true));
        Assert.False(ProgressWindowPolicy.ShouldOpen(job with { Acknowledged = true }, showProgressWindow: true));
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Finalizing)]
    public void A_job_still_going_keeps_its_window(JobState state) =>
        Assert.Equal(ProgressWindowAction.Stay, ProgressWindowPolicy.OnTerminal(Job(state)));
}

public class ToastBatchTests
{
    private static readonly string[] PathFragments = ["secret", "alpha", "beta", @"D:\", @"C:\", "dst", "src"];

    private static JobSnapshot Job(JobState state, LoggingMode mode = LoggingMode.Normal) => DisplayAndTrayTests.Job(state, mode);

    [Fact]
    public void Collects_finishes_for_two_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2), ToastBatch.Window);

    [Fact]
    public void A_single_job_keeps_its_own_toast()
    {
        var job = Job(JobState.DoneWithErrors);
        Assert.Equal(ToastText.ForFinished(job, notifyOnComplete: true), ToastBatch.Combine([job], notifyOnComplete: true));

        var ephemeral = Job(JobState.Done, LoggingMode.Ephemeral);
        Assert.Equal(ToastText.ForFinished(ephemeral, notifyOnComplete: true), ToastBatch.Combine([ephemeral], notifyOnComplete: true));
    }

    [Fact]
    public void Jobs_that_would_not_toast_alone_do_not_toast_in_a_batch()
    {
        Assert.Null(ToastBatch.Combine([], notifyOnComplete: true));
        Assert.Null(ToastBatch.Combine([Job(JobState.Canceled), Job(JobState.Done) with { NoOp = true }], notifyOnComplete: true));
        Assert.Null(ToastBatch.Combine([Job(JobState.Done), Job(JobState.Done)], notifyOnComplete: false));

        // One error among silent finishes is reported as that job's own toast.
        var failed = Job(JobState.Failed);
        Assert.Equal(
            ToastText.ForFinished(failed, notifyOnComplete: false),
            ToastBatch.Combine([Job(JobState.Done), failed, Job(JobState.Canceled)], notifyOnComplete: false));
    }

    [Fact]
    public void Several_jobs_get_a_count_and_the_worst_kind()
    {
        var toast = ToastBatch.Combine([Job(JobState.Done), Job(JobState.DoneWithErrors), Job(JobState.Done)], notifyOnComplete: true)!;
        Assert.Equal("3 pastes finished, 1 with errors", toast.Title);
        Assert.Equal(ToastKind.Warning, toast.Kind);

        var mixed = ToastBatch.Combine(
            [Job(JobState.Done), Job(JobState.Failed), Job(JobState.Canceled) with { DamagedOnCancel = 2 }],
            notifyOnComplete: true)!;
        Assert.Equal("3 pastes finished, 1 failed, 1 canceled", mixed.Title);
        Assert.Equal(ToastKind.Error, mixed.Kind);

        var clean = ToastBatch.Combine([Job(JobState.Done), Job(JobState.Done)], notifyOnComplete: true)!;
        Assert.Equal("2 pastes finished", clean.Title);
        Assert.Equal(ToastKind.Info, clean.Kind);
    }

    public static TheoryData<LoggingMode[]> Batches => new()
    {
        new[] { LoggingMode.Normal, LoggingMode.Normal },
        new[] { LoggingMode.Normal, LoggingMode.Ephemeral },
        new[] { LoggingMode.Ephemeral, LoggingMode.Normal, LoggingMode.Normal },
        new[] { LoggingMode.Ephemeral, LoggingMode.Ephemeral },
    };

    [Theory]
    [MemberData(nameof(Batches))]
    public void Batched_toasts_name_no_file_or_folder_in_any_mix_of_modes(LoggingMode[] modes)
    {
        var states = new[] { JobState.Done, JobState.DoneWithErrors, JobState.Failed };
        var jobs = modes.Select((mode, i) => Job(states[i % states.Length], mode) with
        {
            RefusalReason = "secret refusal",
            FailureReason = @"Access to C:\secret was denied.",
        }).ToList();

        var toast = ToastBatch.Combine(jobs, notifyOnComplete: true)!;
        Assert.NotNull(toast);
        foreach (var fragment in PathFragments)
        {
            Assert.DoesNotContain(fragment, toast.Title + "\n" + toast.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(ToastKind.Info, ToastKind.Info, true)]
    [InlineData(ToastKind.Info, ToastKind.Warning, true)]
    [InlineData(ToastKind.Info, ToastKind.Error, true)]
    [InlineData(ToastKind.Warning, ToastKind.Info, false)]
    [InlineData(ToastKind.Error, ToastKind.Info, false)]
    [InlineData(ToastKind.Warning, ToastKind.Error, true)]
    [InlineData(ToastKind.Error, ToastKind.Warning, true)]
    [InlineData(ToastKind.Error, ToastKind.Error, true)]
    public void A_warning_or_error_is_never_replaced_by_an_info(ToastKind showing, ToastKind incoming, bool replace)
    {
        Assert.Equal(replace, ToastBatch.ShouldReplace(showing, incoming));
        Assert.Equal(replace, ToastBatch.ShouldReplace(showing, TimeSpan.FromSeconds(5), incoming));
    }

    [Fact]
    public void A_toast_never_reported_closed_stops_blocking_after_the_assumed_time_on_screen()
    {
        var justBefore = ToastBatch.AssumedOnScreen - TimeSpan.FromMilliseconds(1);
        Assert.False(ToastBatch.ShouldReplace(ToastKind.Error, justBefore, ToastKind.Info));
        Assert.False(ToastBatch.ShouldReplace(ToastKind.Warning, justBefore, ToastKind.Info));
        Assert.True(ToastBatch.ShouldReplace(ToastKind.Error, ToastBatch.AssumedOnScreen, ToastKind.Info));
        Assert.True(ToastBatch.ShouldReplace(ToastKind.Warning, TimeSpan.FromMinutes(10), ToastKind.Info));
        Assert.InRange(ToastBatch.AssumedOnScreen, ToastBatch.Window, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Fit_trims_to_what_a_balloon_holds()
    {
        var fitted = ToastBatch.Fit(new Toast(new string('t', 100), new string('b', 400), ToastKind.Info));
        Assert.Equal(ToastBatch.MaxTitleLength, fitted.Title.Length);
        Assert.Equal(ToastBatch.MaxBodyLength, fitted.Body.Length);
        Assert.EndsWith("…", fitted.Body);

        var small = new Toast("Copy finished", "2 items to Archive", ToastKind.Info);
        Assert.Equal(small, ToastBatch.Fit(small));
    }
}

public class ConflictSelectionTests
{
    private static FileConflict Conflict(string destination, bool keepBothAllowed = true) => new(
        @"C:\src\" + WinPath.GetFileName(destination),
        destination,
        new FileFacts(10, DateTimeOffset.UnixEpoch.AddDays(2)),
        new FileFacts(20, DateTimeOffset.UnixEpoch.AddDays(1)))
    {
        KeepBothAllowed = keepBothAllowed,
    };

    [Theory]
    [InlineData(true, false, true, FileDecision.Replace)]
    [InlineData(true, false, false, FileDecision.Replace)]
    [InlineData(false, true, true, FileDecision.Skip)]
    [InlineData(false, false, true, FileDecision.Skip)]
    [InlineData(false, false, false, FileDecision.Skip)]
    [InlineData(true, true, true, FileDecision.KeepBoth)]
    public void Ticks_map_to_explorers_decisions(bool source, bool destination, bool keepBothAllowed, FileDecision expected) =>
        Assert.Equal(expected, ConflictSelection.Decision(source, destination, keepBothAllowed));

    [Fact]
    public void Both_ticked_where_keep_both_is_unavailable_never_overwrites_the_kept_file() =>
        Assert.Equal(FileDecision.Skip, ConflictSelection.Decision(true, true, keepBothAllowed: false));

    [Fact]
    public void Build_keys_every_row_by_destination()
    {
        var choice = ConflictSelection.Build(
        [
            new ConflictRow(Conflict(@"D:\dst\a.txt"), SourceChecked: true, DestinationChecked: false),
            new ConflictRow(Conflict(@"D:\dst\b.txt"), SourceChecked: false, DestinationChecked: true),
            new ConflictRow(Conflict(@"D:\dst\c.txt"), SourceChecked: true, DestinationChecked: true),
            new ConflictRow(Conflict(@"D:\dst\d.txt", keepBothAllowed: false), SourceChecked: true, DestinationChecked: true),
        ]);
        Assert.Equal(4, choice.ByDestination.Count);
        Assert.Equal(FileDecision.Replace, choice.ByDestination[@"D:\dst\a.txt"]);
        Assert.Equal(FileDecision.Skip, choice.ByDestination[@"D:\dst\b.txt"]);
        Assert.Equal(FileDecision.KeepBoth, choice.ByDestination[@"D:\dst\c.txt"]);
        Assert.Equal(FileDecision.Skip, choice.ByDestination[@"D:\dst\d.txt"]);
        Assert.Equal(FileDecision.Replace, choice.ByDestination[@"d:\DST\A.TXT"]);
    }

    [Fact]
    public void Two_rows_for_one_destination_keep_the_less_destructive_decision()
    {
        var choice = ConflictSelection.Build(
        [
            new ConflictRow(Conflict(@"D:\dst\a.txt"), SourceChecked: true, DestinationChecked: false),
            new ConflictRow(Conflict(@"D:\DST\A.txt"), SourceChecked: false, DestinationChecked: false),
        ]);
        Assert.Equal(FileDecision.Skip, Assert.Single(choice.ByDestination).Value);
    }

    [Fact]
    public void Summary_says_what_continue_will_do()
    {
        Assert.Equal(
            "2 files replaced, 1 file kept both, 1 file skipped",
            ConflictSelection.Summary([FileDecision.Replace, FileDecision.KeepBoth, FileDecision.Skip, FileDecision.Replace]));
        Assert.Equal("1,200 files skipped", ConflictSelection.Summary(Enumerable.Repeat(FileDecision.Skip, 1200)));
    }
}

public class JobStateTextTests
{
    private static JobSnapshot Job(JobState state, LoggingMode mode = LoggingMode.Normal) => DisplayAndTrayTests.Job(state, mode);

    [Theory]
    [InlineData(JobWait.OverlappingJob, 0, "Waiting for another paste into this folder")]
    [InlineData(JobWait.ConcurrencyLimit, 3, "Waiting (limit of 3 jobs)")]
    [InlineData(JobWait.ConcurrencyLimit, 1, "Waiting (limit of 1 job)")]
    [InlineData(JobWait.ConcurrencyLimit, 0, "Waiting for a free job slot")]
    [InlineData(JobWait.ScanLimit, 0, "Waiting to scan")]
    [InlineData(JobWait.None, 0, "Starting…")]
    public void Queued_jobs_say_what_they_wait_for(JobWait wait, int limit, string expected) =>
        Assert.Equal(expected, JobStateText.For(Job(JobState.Queued) with { Wait = wait }, limit));

    [Fact]
    public void Scanning_shows_what_was_discovered()
    {
        var scanning = Job(JobState.Scanning) with { TotalFiles = 1234, TotalBytes = 1_288_490_189 };
        Assert.Equal("Discovered 1,234 items (1.20 GB)", JobStateText.For(scanning));
        Assert.Equal("Scanning…", JobStateText.For(scanning with { TotalFiles = 0, TotalBytes = 0 }));
        Assert.EndsWith("will pause before copying", JobStateText.For(scanning, pauseLatched: true));
    }

    [Fact]
    public void A_pause_before_running_reads_as_paused_waiting()
    {
        var queued = Job(JobState.Queued) with { Wait = JobWait.OverlappingJob };
        Assert.Equal("Paused (waiting)", JobStateText.For(queued, pauseLatched: true));
        Assert.Equal("PAUSED", JobStateText.Label(queued, pauseLatched: true));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(queued, pauseLatched: true));
        Assert.Equal("Paused", JobStateText.For(Job(JobState.Paused)));
    }

    [Fact]
    public void Canceling_overrides_every_state_until_the_job_ends()
    {
        foreach (var state in new[] { JobState.Queued, JobState.Scanning, JobState.Running, JobState.Paused })
        {
            var job = Job(state) with { CancelRequested = true };
            Assert.Equal("Canceling…", JobStateText.For(job, pauseLatched: true));
            Assert.Equal("CANCELING", JobStateText.Label(job));
        }
        Assert.Equal("Canceled", JobStateText.For(Job(JobState.Canceled) with { CancelRequested = true }));
    }

    [Fact]
    public void Outcomes_are_plain_sentences()
    {
        Assert.Equal("Copying", JobStateText.For(Job(JobState.Running)));
        Assert.Equal("Moving", JobStateText.For(Job(JobState.Running) with { Verb = TransferVerb.Move }));
        Assert.Equal("Done", JobStateText.For(Job(JobState.Done)));
        Assert.StartsWith("Nothing to do", JobStateText.For(Job(JobState.Done) with { NoOp = true }));
        Assert.Equal("Done, 3 items had problems", JobStateText.For(Job(JobState.DoneWithErrors) with { ErrorCount = 2, RefusedCount = 1 }));
        Assert.Equal("Failed: Disk full.", JobStateText.For(Job(JobState.Failed) with { FailureReason = "Disk full." }));
        Assert.Equal("Canceled; 2 files may be incomplete", JobStateText.For(Job(JobState.Canceled) with { DamagedOnCancel = 2 }));
    }

    [Fact]
    public void Every_state_has_a_label_and_a_gridline_tone()
    {
        foreach (var state in Enum.GetValues<JobState>())
        {
            Assert.Matches("^[A-Z]+$", JobStateText.Label(Job(state)));
            Assert.False(string.IsNullOrWhiteSpace(JobStateText.For(Job(state))));
        }
        Assert.Equal(StateTone.Live, JobStateText.Tone(Job(JobState.Running)));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(Job(JobState.Paused)));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(Job(JobState.AwaitingDecision)));
        Assert.Equal(StateTone.Positive, JobStateText.Tone(Job(JobState.Done)));
        Assert.Equal(StateTone.Danger, JobStateText.Tone(Job(JobState.Failed)));
        Assert.Equal(StateTone.Danger, JobStateText.Tone(Job(JobState.DoneWithErrors)));
        Assert.Equal(StateTone.Neutral, JobStateText.Tone(Job(JobState.Canceled)));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(Job(JobState.Canceled) with { DamagedOnCancel = 1 }));
    }

    [Fact]
    public void Titles_name_the_job_except_in_ephemeral_mode()
    {
        Assert.Equal("Robo-Copy: 2 items → secret-dst", JobStateText.Title(Job(JobState.Running)));
        Assert.Equal("Robo-Cut: alpha.txt → secret-dst", JobStateText.Title(Job(JobState.Running) with
        {
            Verb = TransferVerb.Move,
            Sources = [@"C:\secret-src\alpha.txt"],
        }));
        Assert.Equal(@"Robo-Copy: 2 items → D:\", JobStateText.Title(Job(JobState.Running) with { Destination = @"D:\" }));

        var ephemeral = JobStateText.Title(Job(JobState.Running, LoggingMode.Ephemeral));
        Assert.Equal("Robo-Copy job", ephemeral);
    }

    [Fact]
    public void Percent_never_reaches_100_before_done()
    {
        var job = Job(JobState.Running) with { DoneBytes = 999, TotalBytes = 1000 };
        Assert.Equal(99, JobStateText.Percent(job));
        Assert.Equal(99, JobStateText.Percent(job with { DoneBytes = 1000 }));
        Assert.Equal(100, JobStateText.Percent(Job(JobState.Done)));
        Assert.Equal(75, JobStateText.Percent(job with { TotalBytes = 0, DoneFiles = 3, TotalFiles = 4 }));
        Assert.Equal(0, JobStateText.Percent(job with { TotalBytes = 0, TotalFiles = 0 }));
    }

    [Fact]
    public void Amounts_and_rate()
    {
        var job = Job(JobState.Running) with { BytesPerSecond = 1_288_490_189, Remaining = TimeSpan.FromMinutes(4) };
        Assert.Equal("512 bytes of 1.00 KB · 3 of 4 files", JobStateText.Amounts(job));
        Assert.Equal("1.20 GB/s · 4 min left", JobStateText.Rate(job));
        Assert.Equal(string.Empty, JobStateText.Rate(job with { State = JobState.Paused }));
        Assert.Equal(string.Empty, JobStateText.Rate(job with { CancelRequested = true }));
        Assert.Equal(string.Empty, JobStateText.Amounts(job with { TotalBytes = 0, TotalFiles = 0 }));
    }

    [Fact]
    public void Status_cells_and_counts_are_path_free()
    {
        var jobs = new[]
        {
            Job(JobState.Running) with { BytesPerSecond = 1_288_490_189, Remaining = TimeSpan.FromMinutes(4) },
            Job(JobState.Paused),
            Job(JobState.Failed),
            Job(JobState.Done),
        };
        Assert.Equal(["2 JOBS", "1.20 GB/S", "ETA 4 MIN", "1 NEEDS ATTENTION", "EPHEMERAL"], JobStateText.StatusCells(jobs, LoggingMode.Ephemeral));
        Assert.Equal(["NO ACTIVE JOBS"], JobStateText.StatusCells([], LoggingMode.Normal));
        Assert.Equal(new JobCounts(Active: 2, Paused: 1, NeedAttention: 1, Finished: 2), JobStateText.Counts(jobs));
    }
}
