using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class CancelCleanupTests
{
    private static readonly FileFacts Facts = new(10, DateTimeOffset.UnixEpoch);

    private static PlannedFile File(string name) => new($@"C:\src\{name}", $@"D:\dst\{name}", Facts);

    private static KilledRunFile Killed(string name, ConflictPolicy policy = ConflictPolicy.Ask) => new(File(name), policy);

    private static FileIdentity Id(ulong n) => new(0xABCD, 0, n, 1_000 + (long)n);

    private static KillObservation Open(ulong n) => new(KillEvidence.OpenByRobocopy, Id(n));

    private static KillObservation NotOpen(ulong n) => new(KillEvidence.NotOpenByRobocopy, Id(n));

    private static readonly KillObservation Absent = new(KillEvidence.Absent, default);

    private static Dictionary<string, KillObservation> Seen(params (string Name, KillObservation Observation)[] seen) =>
        seen.ToDictionary(s => $@"D:\dst\{s.Name}", s => s.Observation, WinPath.Comparer);

    private static CleanupPlan Select(
        IEnumerable<KilledRunFile> killed,
        IReadOnlyDictionary<string, KillObservation> atKill,
        IEnumerable<string>? completedSources = null,
        IEnumerable<string>? presentBeforeStep = null,
        bool move = false,
        Func<string, bool>? destinationExists = null,
        Func<string, bool>? sourceStillExists = null) =>
        CancelCleanup.Select(
            killed,
            completedSources ?? [],
            presentBeforeStep ?? [],
            atKill,
            move,
            destinationExists ?? (_ => true),
            sourceStillExists ?? (_ => true),
            claimedByOtherJob: _ => false);

    [Fact]
    public void Only_a_file_robocopy_held_open_at_the_kill_is_deleted_and_only_as_that_file()
    {
        var plan = Select(
            [Killed("partial.bin"), Killed("absent.bin"), Killed("done.bin")],
            Seen(("partial.bin", Open(7)), ("absent.bin", Absent), ("done.bin", Open(8))),
            completedSources: [@"C:\SRC\done.bin"]);

        Assert.Equal([new CleanupTarget(@"D:\dst\partial.bin", Id(7))], plan.Delete);
        Assert.Empty(plan.LeftInPlace);
    }

    [Fact]
    public void A_late_arrival_robocopy_skipped_is_never_deleted()
    {
        // The file appeared after the step's presence check; robocopy saw it, skipped it under
        // its Skip flags and printed nothing. It is not a partial copy: robocopy did not have it
        // open while suspended for the kill.
        var plan = Select([Killed("late.bin")], Seen(("late.bin", NotOpen(9))));

        Assert.Empty(plan.Delete);
        Assert.Empty(plan.LeftInPlace);
    }

    [Fact]
    public void Without_evidence_nothing_is_deleted_and_an_existing_file_is_reported()
    {
        // No observation at all: robocopy could not be suspended, or the look failed.
        var plan = Select(
            [Killed("unknown.bin"), Killed("never-created.bin"), Killed("failed-look.bin")],
            Seen(("failed-look.bin", new KillObservation(KillEvidence.Unknown, default))),
            destinationExists: p => !p.EndsWith("never-created.bin", StringComparison.Ordinal));

        Assert.Empty(plan.Delete);
        Assert.Equal([@"D:\dst\unknown.bin", @"D:\dst\failed-look.bin"], plan.LeftInPlace);
    }

    [Fact]
    public void Held_open_without_a_usable_identity_is_reported_not_deleted()
    {
        var plan = Select([Killed("noid.bin")], Seen(("noid.bin", new KillObservation(KillEvidence.OpenByRobocopy, default))));

        Assert.Empty(plan.Delete);
        Assert.Equal([@"D:\dst\noid.bin"], plan.LeftInPlace);
    }

    [Fact]
    public void A_destination_that_existed_before_the_step_is_never_deleted()
    {
        var plan = Select(
            [Killed("replaced.bin", ConflictPolicy.Replace), Killed("newer.bin", ConflictPolicy.KeepNewer), Killed("skipped.bin", ConflictPolicy.Skip)],
            Seen(("replaced.bin", Open(1)), ("newer.bin", Open(2)), ("skipped.bin", Open(3))),
            presentBeforeStep: [@"d:\DST\replaced.bin", @"D:\dst\newer.bin", @"D:\dst\skipped.bin"]);

        Assert.Empty(plan.Delete);
        // Only a policy that overwrites can have left it half written.
        Assert.Equal([@"D:\dst\replaced.bin", @"D:\dst\newer.bin"], plan.LeftInPlace);
    }

    [Fact]
    public void A_cut_keeps_an_unreported_destination_whose_source_is_already_gone()
    {
        var plan = Select(
            [Killed("moved.bin"), Killed("inflight.bin")],
            Seen(("moved.bin", Open(1)), ("inflight.bin", Open(2))),
            move: true,
            sourceStillExists: p => !p.EndsWith("moved.bin", StringComparison.Ordinal));

        Assert.Equal([new CleanupTarget(@"D:\dst\inflight.bin", Id(2))], plan.Delete);
    }

    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Replace)]
    [InlineData(ConflictPolicy.KeepNewer)]
    public void Overwriting_policies_are_exactly_those_without_all_three_skip_flags(ConflictPolicy policy)
    {
        var flags = RobocopyArgs.ConflictFlags(policy).Split(' ');
        var skipsEveryExistingFile = flags.Contains("/XC") && flags.Contains("/XN") && flags.Contains("/XO") && !flags.Contains("/IS");

        Assert.Equal(!skipsEveryExistingFile, RobocopyArgs.MayOverwriteExisting(policy));
    }

    [Fact]
    public void An_identity_of_zeros_is_not_an_identity()
    {
        Assert.False(new FileIdentity(5, 0, 0, 123).IsKnown);
        Assert.True(new FileIdentity(5, 0, 1, 0).IsKnown);
        Assert.NotEqual(new FileIdentity(5, 0, 1, 100), new FileIdentity(5, 0, 1, 101));
    }
}

public class JobOutcomeTests
{
    private static StepOutcome Step(int completed = 0, int errors = 0, int? exit = 1, string? failure = null) => new(
        Enumerable.Range(0, completed).Select(i => $@"C:\s\{i}").ToList(),
        Enumerable.Range(0, errors).Select(i => new ErrorReported(5, "Copying File", $@"C:\s\e{i}", "Access is denied.")).ToList(),
        exit is { } e ? new RobocopyExitCode(e) : null,
        failure);

    [Fact]
    public void Clean_steps_are_done()
    {
        Assert.Equal(JobState.Done, JobOutcome.FinalState([Step(completed: 3)], canceled: false, planIssues: 0));
        Assert.Equal(JobState.Done, JobOutcome.FinalState([], canceled: false, planIssues: 0));
    }

    [Fact]
    public void Per_file_errors_and_plan_issues_are_done_with_errors_so_they_can_be_retried()
    {
        Assert.Equal(JobState.DoneWithErrors, JobOutcome.FinalState([Step(errors: 2, exit: 8)], false, 0));
        Assert.Equal(JobState.DoneWithErrors, JobOutcome.FinalState([Step(exit: 9)], false, 0));
        Assert.Equal(JobState.DoneWithErrors, JobOutcome.FinalState([Step(completed: 1)], false, planIssues: 1));
    }

    [Fact]
    public void Nothing_running_at_all_is_failed_and_cancel_wins()
    {
        Assert.Equal(JobState.Failed, JobOutcome.FinalState([Step(exit: 16)], false, 0));
        Assert.Equal(JobState.Failed, JobOutcome.FinalState([Step(exit: null, failure: "launch")], false, 0));
        Assert.Equal(JobState.DoneWithErrors, JobOutcome.FinalState([Step(completed: 1), Step(exit: 16)], false, 0));
        Assert.Equal(JobState.Canceled, JobOutcome.FinalState([Step(completed: 1)], canceled: true, planIssues: 0));
    }

    [Fact]
    public void Concurrency_zero_means_unlimited_and_paused_jobs_hold_their_slot()
    {
        Assert.Equal(int.MaxValue, JobQueuePolicy.FreeSlots(50, 0));
        Assert.Equal(1, JobQueuePolicy.FreeSlots(1, 2));
        Assert.Equal(0, JobQueuePolicy.FreeSlots(3, 2));
        Assert.True(JobQueuePolicy.HoldsSlot(JobState.Paused));
        Assert.False(JobQueuePolicy.HoldsSlot(JobState.Queued));
        Assert.False(JobQueuePolicy.HoldsSlot(JobState.Done));
    }

    [Fact]
    public void Pipe_names_are_valid_unilog_targets_and_differ_by_nonce()
    {
        var id = Guid.NewGuid();
        var a = PipeNames.ForStep(id, 0, 1);
        var b = PipeNames.ForStep(id, 0, 2);
        Assert.NotEqual(a, b);
        Assert.Equal(@"/UNILOG:\\.\pipe\" + a, RobocopyArgs.LogPipeArgument(a));
    }
}

public class DisplayAndTrayTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(2047, "1.99 KB")]
    [InlineData(1_023_999, "999 KB")]
    [InlineData(1_048_576, "1.00 MB")]
    [InlineData(1_288_490_189, "1.20 GB")]
    [InlineData(1_288_490_188, "1.19 GB")]
    [InlineData(10_485_760, "10.0 MB")]
    public void Bytes_use_three_truncated_significant_digits(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayText.Bytes(bytes));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(39.2, "40 s")]
    [InlineData(61, "2 min")]
    [InlineData(3600, "1 h")]
    [InlineData(4801, "1 h 21 min")]
    public void Durations_round_up(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayText.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Source_summary_names_the_first_item()
    {
        Assert.Equal("a.txt", DisplayText.SourcesSummary([@"C:\x\a.txt"]));
        Assert.Equal("a.txt and 2 more", DisplayText.SourcesSummary([@"C:\x\a.txt", @"C:\x\b", @"C:\x\c"]));
        Assert.Equal(@"D:\", DisplayText.SourcesSummary([@"D:\"]));
    }

    internal static JobSnapshot Job(JobState state, LoggingMode mode = LoggingMode.Normal, bool acknowledged = false, double? speed = null) => new(
        Guid.NewGuid(), null, TransferVerb.Copy, [@"C:\secret-src\alpha.txt", @"C:\secret-src\beta"], @"D:\secret-dst",
        state, mode, DateTimeOffset.UnixEpoch, 512, 1024, 3, 4, speed, speed is null ? null : TimeSpan.FromMinutes(4), 2, acknowledged);

    [Fact]
    public void Tray_state_priority_is_attention_then_running_then_paused()
    {
        Assert.Equal(TrayIconState.Idle, TrayStatus.Derive([], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Idle, TrayStatus.Derive([Job(JobState.Done)], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Paused, TrayStatus.Derive([Job(JobState.Paused)], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Running, TrayStatus.Derive([Job(JobState.Paused), Job(JobState.Running)], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Attention, TrayStatus.Derive([Job(JobState.Running), Job(JobState.DoneWithErrors)], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Idle, TrayStatus.Derive([Job(JobState.DoneWithErrors, acknowledged: true)], LoggingMode.Normal).Icon);
        Assert.Equal(TrayIconState.Attention, TrayStatus.Derive([Job(JobState.AwaitingDecision, acknowledged: true)], LoggingMode.Normal).Icon);
    }

    [Fact]
    public void Tooltip_summarizes_and_fits_notify_icon_limit()
    {
        var status = TrayStatus.Derive([Job(JobState.Running, speed: 1_288_490_189), Job(JobState.Paused)], LoggingMode.Ephemeral);
        Assert.True(status.Ephemeral);
        Assert.Equal("RoboRightClick (ephemeral)\n2 jobs (1 paused) · 1.20 GB/s · 4 min", status.Tooltip);

        var many = Enumerable.Range(0, 200).Select(_ => Job(JobState.Failed)).ToList();
        Assert.True(TrayStatus.Derive(many, LoggingMode.Normal).Tooltip.Length <= TrayStatus.MaxTooltipLength);
    }

    [Theory]
    [InlineData(JobState.Done)]
    [InlineData(JobState.DoneWithErrors)]
    [InlineData(JobState.Failed)]
    public void Ephemeral_toasts_name_no_file_or_folder(JobState state)
    {
        var toast = ToastText.ForFinished(Job(state, LoggingMode.Ephemeral), notifyOnComplete: true);
        Assert.NotNull(toast);
        foreach (var fragment in new[] { "secret", "alpha", "beta", @"D:\", "dst" })
        {
            Assert.DoesNotContain(fragment, toast.Title + toast.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Clean_finishes_respect_notify_setting_but_errors_always_notify()
    {
        Assert.Null(ToastText.ForFinished(Job(JobState.Done), notifyOnComplete: false));
        Assert.NotNull(ToastText.ForFinished(Job(JobState.DoneWithErrors), notifyOnComplete: false));
        Assert.Null(ToastText.ForFinished(Job(JobState.Canceled), notifyOnComplete: true));
        Assert.Contains("secret-dst", ToastText.ForFinished(Job(JobState.Done), notifyOnComplete: true)!.Body);
    }
}
