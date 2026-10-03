using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class StartupRulesTests
{
    private static JobSnapshot Job(JobState state, LoggingMode mode = LoggingMode.Normal) =>
        DisplayAndTrayTests.Job(state, mode);

    [Fact]
    public void A_plain_start_from_outside_the_install_folder_offers_to_install()
    {
        Assert.Equal(StartupAction.OfferInstall, StartupRules.Decide(new CliRunTray(StartedByCom: false), runningFromInstallLocation: false));
        Assert.Equal(StartupAction.RunTray, StartupRules.Decide(new CliRunTray(StartedByCom: false), runningFromInstallLocation: true));
    }

    [Fact]
    public void An_embedding_start_always_runs_the_tray()
    {
        // Explorer is waiting on this activation; an install prompt would make the right-click fail.
        Assert.Equal(StartupAction.RunTray, StartupRules.Decide(new CliRunTray(StartedByCom: true), runningFromInstallLocation: false));
        Assert.Equal(StartupAction.RunTray, StartupRules.Decide(new CliRunTray(StartedByCom: true), runningFromInstallLocation: true));
    }

    [Fact]
    public void An_uninstall_is_held_up_by_running_jobs_but_not_by_finished_ones()
    {
        // Uninstall deletes every job log: refusing over a finished job protects nothing.
        Assert.Equal(ExitAction.Exit, StartupRules.ExitDecision(hasActiveJobs: false, finishedNeedingAttention: 3, requestedByOtherProcess: true, forUninstall: true));
        Assert.Equal(ExitAction.RefuseWithToast, StartupRules.ExitDecision(hasActiveJobs: true, finishedNeedingAttention: 0, requestedByOtherProcess: true, forUninstall: true));
        // An update keeps the logs, so it still waits for finished jobs to be reviewed.
        Assert.Equal(ExitAction.RefuseWithToast, StartupRules.ExitDecision(hasActiveJobs: false, finishedNeedingAttention: 3, requestedByOtherProcess: true, forUninstall: false));
        // The user's own Exit still asks.
        Assert.Equal(ExitAction.ConfirmWithUser, StartupRules.ExitDecision(hasActiveJobs: false, finishedNeedingAttention: 3, requestedByOtherProcess: false, forUninstall: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_refusal_never_claims_jobs_are_running_and_names_every_way_out(bool forUninstall)
    {
        var (heading, body) = InstallText.TrayDidNotClose(forUninstall);

        Assert.DoesNotContain("Jobs are still running", heading);
        Assert.Contains("Exit on the tray icon", body);
        Assert.Contains("Jobs window", body);
        if (!forUninstall)
        {
            Assert.Contains("Try again, Skip or OK", body);
        }
        var toast = ToastText.ForExitRefused(active: 0, finishedNeedingAttention: 2);
        Assert.Contains("Skip or OK", toast.Body);
        Assert.Contains("Exit on this icon", toast.Body);
        Assert.True(toast.Body.Length <= ToastText.MaxBalloonText);
    }

    [Fact]
    public void The_interrupted_paste_log_stays_in_the_menu_until_opened()
    {
        Assert.True(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, jobs: [], interruptedPending: true).InterruptedLogVisible);
        Assert.False(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, jobs: [], interruptedPending: false).InterruptedLogVisible);
        Assert.False(TrayMenu.For(LoggingMode.Ephemeral, pauseAllActive: false, jobs: [], interruptedPending: true).InterruptedLogVisible);
    }

    [Fact]
    public void A_jobs_click_with_nothing_to_show_opens_the_pending_interrupted_log()
    {
        // The interrupted notice went to the notification center; a "Copy finished" toast came
        // later. A click on the old notice arrives with the latest toast's target, Jobs.
        var clean = DisplayAndTrayTests.Job(JobState.Done);
        var failed = DisplayAndTrayTests.Job(JobState.DoneWithErrors);

        Assert.True(StartupRules.JobsClickOpensInterrupted([clean], interruptedPending: true));
        Assert.True(StartupRules.JobsClickOpensInterrupted([], interruptedPending: true));
        Assert.False(StartupRules.JobsClickOpensInterrupted([clean, failed], interruptedPending: true));
        Assert.False(StartupRules.JobsClickOpensInterrupted([clean], interruptedPending: false));
    }

    [Fact]
    public void Open_logs_is_hidden_in_ephemeral_mode()
    {
        Assert.False(TrayMenu.For(LoggingMode.Ephemeral, pauseAllActive: false).OpenLogsVisible);
        Assert.False(TrayMenu.For(LoggingMode.Ephemeral, pauseAllActive: true).OpenLogsVisible);
        Assert.True(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false).OpenLogsVisible);
    }

    [Fact]
    public void Pause_all_and_ephemeral_are_checked_while_on()
    {
        var on = TrayMenu.For(LoggingMode.Ephemeral, pauseAllActive: true);
        Assert.True(on.PauseAllChecked);
        Assert.True(on.EphemeralChecked);

        var off = TrayMenu.For(LoggingMode.Normal, pauseAllActive: false);
        Assert.False(off.PauseAllChecked);
        Assert.False(off.EphemeralChecked);
    }

    [Fact]
    public void Resume_all_is_enabled_only_when_it_would_resume_something()
    {
        static JobSnapshot Job(JobState state) => DisplayAndTrayTests.Job(state);

        Assert.False(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false).ResumeAllEnabled);
        Assert.False(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, [Job(JobState.Running), Job(JobState.Done)]).ResumeAllEnabled);
        Assert.True(TrayMenu.For(LoggingMode.Normal, pauseAllActive: true).ResumeAllEnabled);
        Assert.True(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, [Job(JobState.Paused)]).ResumeAllEnabled);
        // A pause latched before the job runs is resumable too.
        Assert.True(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, [Job(JobState.Queued) with { PauseRequested = true }]).ResumeAllEnabled);
        // A job being canceled is not coming back.
        Assert.False(TrayMenu.For(LoggingMode.Normal, pauseAllActive: false, [Job(JobState.Paused) with { CancelRequested = true }]).ResumeAllEnabled);
    }

    [Fact]
    public void Exit_with_nothing_running_or_to_review_is_immediate_for_anyone()
    {
        Assert.Equal(ExitAction.Exit, StartupRules.ExitDecision(hasActiveJobs: false, finishedNeedingAttention: 0, requestedByOtherProcess: false));
        Assert.Equal(ExitAction.Exit, StartupRules.ExitDecision(hasActiveJobs: false, finishedNeedingAttention: 0, requestedByOtherProcess: true));
    }

    [Fact]
    public void Exit_with_active_jobs_asks_the_user_and_refuses_other_processes()
    {
        // Another process (an upgrade or uninstall) must never cancel a paste the user started.
        Assert.Equal(ExitAction.ConfirmWithUser, StartupRules.ExitDecision(hasActiveJobs: true, finishedNeedingAttention: 0, requestedByOtherProcess: false));
        Assert.Equal(ExitAction.RefuseWithToast, StartupRules.ExitDecision(hasActiveJobs: true, finishedNeedingAttention: 0, requestedByOtherProcess: true));
    }

    [Fact]
    public void Exit_with_finished_jobs_still_needing_attention_asks_the_user_and_refuses_other_processes()
    {
        // A canceled copy left 3 files that may be incomplete, not yet reviewed. The Jobs
        // window is in memory only: exiting without a word would lose which files they were.
        var jobs = new[]
        {
            Job(JobState.Canceled) with { DamagedOnCancel = 3 },
            Job(JobState.DoneWithErrors) with { Acknowledged = true },
            Job(JobState.Done),
        };
        var unreviewed = StartupRules.FinishedNeedingAttention(jobs);

        Assert.Equal(1, unreviewed);
        Assert.Equal(ExitAction.ConfirmWithUser, StartupRules.ExitDecision(hasActiveJobs: false, unreviewed, requestedByOtherProcess: false));
        Assert.Equal(ExitAction.RefuseWithToast, StartupRules.ExitDecision(hasActiveJobs: false, unreviewed, requestedByOtherProcess: true));
        Assert.Equal(0, StartupRules.FinishedNeedingAttention([Job(JobState.AwaitingDecision)]));
    }

    [Fact]
    public void The_exit_question_and_refusal_say_why()
    {
        var (heading, body) = StartupRules.ExitConfirmation(active: 0, finishedNeedingAttention: 2);
        Assert.Equal("2 jobs still need your attention", heading);
        Assert.Contains("Jobs window starts empty", body);

        var (runningHeading, runningBody) = StartupRules.ExitConfirmation(active: 1, finishedNeedingAttention: 1);
        Assert.Equal("1 job is still running", runningHeading);
        Assert.StartsWith("Exiting cancels them", runningBody);
        Assert.Contains("1 job that ended with problems has not been reviewed", runningBody);

        var refused = ToastText.ForExitRefused(active: 0, finishedNeedingAttention: 1);
        Assert.Contains("Try again, Skip or OK", refused.Body);
        Assert.DoesNotContain(@":\", refused.Title + refused.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_deletion_is_offered_only_when_turning_ephemeral_on_with_logs_present()
    {
        Assert.True(StartupRules.ShouldOfferLogDeletion(LoggingMode.Normal, LoggingMode.Ephemeral, jobFolderCount: 3));
        Assert.False(StartupRules.ShouldOfferLogDeletion(LoggingMode.Normal, LoggingMode.Ephemeral, jobFolderCount: 0));
        Assert.False(StartupRules.ShouldOfferLogDeletion(LoggingMode.Ephemeral, LoggingMode.Normal, jobFolderCount: 3));
        Assert.False(StartupRules.ShouldOfferLogDeletion(LoggingMode.Normal, LoggingMode.Normal, jobFolderCount: 3));
        Assert.False(StartupRules.ShouldOfferLogDeletion(LoggingMode.Ephemeral, LoggingMode.Ephemeral, jobFolderCount: 3));
    }

    [Fact]
    public void Mode_change_notice_only_when_an_active_job_keeps_the_other_mode()
    {
        Assert.True(StartupRules.ModeChangeNeedsNotice(LoggingMode.Ephemeral, [Job(JobState.Running, LoggingMode.Normal)]));
        Assert.True(StartupRules.ModeChangeNeedsNotice(LoggingMode.Normal, [Job(JobState.Queued, LoggingMode.Ephemeral)]));
        Assert.False(StartupRules.ModeChangeNeedsNotice(LoggingMode.Ephemeral, [Job(JobState.Running, LoggingMode.Ephemeral)]));
        Assert.False(StartupRules.ModeChangeNeedsNotice(LoggingMode.Ephemeral, [Job(JobState.Done, LoggingMode.Normal)]));
        Assert.False(StartupRules.ModeChangeNeedsNotice(LoggingMode.Ephemeral, []));
    }

    [Fact]
    public void The_tray_hint_shows_only_on_the_start_the_installer_made()
    {
        Assert.Equal(StartupToast.TrayHint, StartupRules.PickStartupToast(0, settingsToastShown: false, afterInstall: true));
        // An ordinary start never repeats it, however empty the log folder is.
        Assert.Equal(StartupToast.None, StartupRules.PickStartupToast(0, settingsToastShown: false, afterInstall: false));
    }

    [Fact]
    public void Interrupted_jobs_outrank_the_settings_toast_which_outranks_the_hint()
    {
        Assert.Equal(StartupToast.Interrupted, StartupRules.PickStartupToast(2, settingsToastShown: true, afterInstall: true));
        Assert.Equal(StartupToast.Interrupted, StartupRules.PickStartupToast(1, settingsToastShown: false, afterInstall: false));
        Assert.Equal(StartupToast.None, StartupRules.PickStartupToast(0, settingsToastShown: true, afterInstall: true));
    }

    [Fact]
    public void Tray_click_brings_the_longest_waiting_conflict_forward()
    {
        Assert.Null(StartupRules.ConflictToFront([]));
        Assert.Null(StartupRules.ConflictToFront([Job(JobState.Running), Job(JobState.DoneWithErrors)]));

        var older = Job(JobState.AwaitingDecision) with { CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) };
        var newer = Job(JobState.AwaitingDecision) with { CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(5) };

        // Snapshots arrive newest first.
        Assert.Equal(older.Id, StartupRules.ConflictToFront([newer, Job(JobState.Running), older]));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("{ \"threads\": 8", true)]
    [InlineData("[1, 2]", true)]
    [InlineData("\"text\"", true)]
    [InlineData("null", true)]
    [InlineData("{}", false)]
    [InlineData("{ \"threads\": \"many\" }", false)]
    [InlineData("{\n  // note\n  \"threads\": 8,\n}", false)]
    public void Unreadable_config_means_no_json_object_at_all(string text, bool unreadable)
    {
        Assert.Equal(unreadable, StartupRules.IsUnreadableConfig(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"threads\": 8")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"threads\": \"many\", \"bogus\": 1 }")]
    [InlineData("{\n  // note\n  \"threads\": 8,\n}")]
    public void Unreadable_config_agrees_with_the_serializer(string text)
    {
        // The .bad copy decision must match what Parse treated as a whole-file failure: a
        // file Parse could read field by field is never "unreadable", and vice versa.
        var result = SettingsSerializer.Parse(text);
        var parseGaveUp = result.Problems.Any(p => p.StartsWith("config is not", StringComparison.Ordinal));
        Assert.Equal(parseGaveUp, result.Unreadable);
        Assert.Equal(parseGaveUp, StartupRules.IsUnreadableConfig(text));
    }
}
