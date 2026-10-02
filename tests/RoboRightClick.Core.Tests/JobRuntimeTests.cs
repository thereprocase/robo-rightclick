using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class CancelCleanupTests
{
    private static readonly FileFacts Facts = new(10, DateTimeOffset.UnixEpoch);

    private static PlannedFile File(string name) => new($@"C:\src\{name}", $@"D:\dst\{name}", Facts);

    [Fact]
    public void Unreported_new_files_are_deleted_and_reported_ones_kept()
    {
        var plan = CancelCleanup.Select(
            [File("done.bin"), File("partial.bin")],
            completedSources: [@"C:\SRC\done.bin"],
            presentBeforeStep: [],
            move: false,
            sourceStillExists: _ => true,
            claimedByOtherJob: _ => false);

        Assert.Equal([@"D:\dst\partial.bin"], plan.Delete);
        Assert.Empty(plan.LeftInPlace);
    }

    [Fact]
    public void A_destination_that_existed_before_the_job_is_never_deleted()
    {
        var plan = CancelCleanup.Select(
            [File("existing.bin")],
            completedSources: [],
            presentBeforeStep: [@"d:\DST\existing.bin"],
            move: false,
            sourceStillExists: _ => true,
            claimedByOtherJob: _ => false);

        Assert.Empty(plan.Delete);
        Assert.Equal([@"D:\dst\existing.bin"], plan.LeftInPlace);
    }

    [Fact]
    public void A_cut_keeps_an_unreported_destination_whose_source_is_already_gone()
    {
        var plan = CancelCleanup.Select(
            [File("moved.bin"), File("inflight.bin")],
            completedSources: [],
            presentBeforeStep: [],
            move: true,
            sourceStillExists: p => !p.EndsWith("moved.bin", StringComparison.Ordinal),
            claimedByOtherJob: _ => false);

        Assert.Equal([@"D:\dst\inflight.bin"], plan.Delete);
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
