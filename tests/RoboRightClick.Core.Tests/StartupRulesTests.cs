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
    public void Exit_without_active_jobs_is_immediate_for_anyone()
    {
        Assert.Equal(ExitAction.Exit, StartupRules.ExitDecision(hasActiveJobs: false, requestedByOtherProcess: false));
        Assert.Equal(ExitAction.Exit, StartupRules.ExitDecision(hasActiveJobs: false, requestedByOtherProcess: true));
    }

    [Fact]
    public void Exit_with_active_jobs_asks_the_user_and_refuses_other_processes()
    {
        // Another process (an upgrade or uninstall) must never cancel a paste the user started.
        Assert.Equal(ExitAction.ConfirmWithUser, StartupRules.ExitDecision(hasActiveJobs: true, requestedByOtherProcess: false));
        Assert.Equal(ExitAction.RefuseWithToast, StartupRules.ExitDecision(hasActiveJobs: true, requestedByOtherProcess: true));
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
