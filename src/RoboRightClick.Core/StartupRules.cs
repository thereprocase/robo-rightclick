using System.Text.Json;

namespace RoboRightClick.Core;

/// <summary>The balloon a tray start shows once its message loop runs.</summary>
public enum StartupToast
{
    None,

    /// <summary>Normal-mode job logs show pastes that never finished (<see cref="ToastText.ForInterrupted"/>).</summary>
    Interrupted,

    /// <summary>The one-time hint about pinning the tray icon (<see cref="ToastText.ForTrayHint"/>).</summary>
    TrayHint,
}

/// <summary>What a plain tray start does.</summary>
public enum StartupAction
{
    RunTray,

    /// <summary>Started from outside the install folder: offer to install instead of running a tray no menu item points at.</summary>
    OfferInstall,
}

/// <summary>What happens when someone asks the tray to exit.</summary>
public enum ExitAction
{
    Exit,

    /// <summary>The user chose Exit while jobs run: ask before canceling them.</summary>
    ConfirmWithUser,

    /// <summary>Another process (install, uninstall) asked while jobs run: stay, and say why.</summary>
    RefuseWithToast,
}

/// <summary>Visibility, check and enabled states of the tray menu items that depend on app state.</summary>
public sealed record TrayMenu(
    bool OpenLogsVisible,
    bool PauseAllChecked,
    bool EphemeralChecked,
    bool ResumeAllEnabled = false,
    bool InterruptedLogVisible = false)
{
    /// <summary>The menu item that opens the interrupted paste's job.json (<see cref="InterruptedLogVisible"/>).</summary>
    public const string InterruptedLogItem = "Interrupted paste: show log";

    /// <summary>
    /// "Open logs" is hidden in ephemeral mode: the mode promises nothing about jobs is on
    /// disk, and a menu item leading to a logs folder would say otherwise. "Resume all" is
    /// enabled only when it would resume something: with Pause all on, or a job that is paused
    /// or will pause when it starts. Otherwise a click on it would visibly do nothing.
    /// "Interrupted paste: show log" stays while the startup notice about an interrupted paste
    /// has been shown and its log not yet opened (<paramref name="interruptedPending"/>): the
    /// notice is shown once per crash, and a later toast takes over what a click on a balloon
    /// opens, so without the item the only pointer to the possibly half-written files could be
    /// gone. Hidden in ephemeral mode, like "Open logs".
    /// </summary>
    public static TrayMenu For(LoggingMode mode, bool pauseAllActive, IReadOnlyList<JobSnapshot>? jobs = null, bool interruptedPending = false) => new(
        OpenLogsVisible: mode == LoggingMode.Normal,
        PauseAllChecked: pauseAllActive,
        EphemeralChecked: mode == LoggingMode.Ephemeral,
        ResumeAllEnabled: pauseAllActive || (jobs ?? []).Any(j =>
            !JobStates.IsTerminal(j.State) && !j.CancelRequested && (j.State == JobState.Paused || j.PauseRequested)),
        InterruptedLogVisible: interruptedPending && mode == LoggingMode.Normal);
}

/// <summary>
/// The tray's start, exit and mode-change decisions, kept out of the composition root so
/// they can be tested on Linux. The host supplies the facts (paths checked, jobs active).
/// </summary>
public static class StartupRules
{
    /// <summary>
    /// COM's -Embedding start always runs the tray: Explorer is waiting on that activation.
    /// A plain start runs the tray only from the install folder; anywhere else it is a
    /// double-clicked download, which offers to install.
    /// </summary>
    public static StartupAction Decide(CliRunTray command, bool runningFromInstallLocation) =>
        command.StartedByCom || runningFromInstallLocation ? StartupAction.RunTray : StartupAction.OfferInstall;

    /// <summary>
    /// With no active jobs and none that still needs attention, exit at once. Otherwise the
    /// user is asked (exit cancels active jobs, and drops finished ones whose files the user has
    /// not reviewed: the Jobs window lives in memory only), and another process's request is
    /// refused: an upgrade or uninstall must never cancel a paste the user did not choose to
    /// cancel, nor silently discard the list of files a paste may have left incomplete.
    /// </summary>
    /// <param name="finishedNeedingAttention">
    /// Finished jobs with <see cref="JobSnapshot.NeedsAttention"/>: errors, a failure or files
    /// that may be incomplete, not yet dealt with (Try again or Skip).
    /// </param>
    /// <param name="forUninstall">
    /// The request came from an uninstall. Finished jobs do not hold it up: uninstall deletes
    /// every job log anyway, so refusing would protect no list of files, and a job closed with X
    /// hours earlier would block the uninstall with nothing on screen to say why. Running jobs
    /// still do: an uninstall never cancels a paste.
    /// </param>
    public static ExitAction ExitDecision(bool hasActiveJobs, int finishedNeedingAttention, bool requestedByOtherProcess, bool forUninstall = false)
    {
        var blocking = requestedByOtherProcess && forUninstall ? 0 : finishedNeedingAttention;
        return !hasActiveJobs && blocking == 0 ? ExitAction.Exit
            : requestedByOtherProcess ? ExitAction.RefuseWithToast
            : ExitAction.ConfirmWithUser;
    }

    /// <summary>Finished jobs that still need the user, for <see cref="ExitDecision"/>.</summary>
    public static int FinishedNeedingAttention(IReadOnlyList<JobSnapshot> jobs) =>
        jobs.Count(j => JobStates.IsTerminal(j.State) && j.NeedsAttention);

    /// <summary>The question Exit asks while jobs run or still need attention. Keep running is the default.</summary>
    public static (string Heading, string Body) ExitConfirmation(int active, int finishedNeedingAttention)
    {
        string Jobs(int n) => n == 1 ? "1 job" : $"{n:N0} jobs";
        var unreviewed = finishedNeedingAttention == 0
            ? string.Empty
            : $"{Jobs(finishedNeedingAttention)} that ended with problems {(finishedNeedingAttention == 1 ? "has" : "have")} not been reviewed: "
                + "the Jobs window starts empty after exiting, and only normal-mode job logs keep the files to check.";
        if (active > 0)
        {
            return (
                $"{Jobs(active)} {(active == 1 ? "is" : "are")} still running",
                ("Exiting cancels them, as Cancel does in the Jobs window. " + unreviewed).TrimEnd());
        }
        return ($"{Jobs(finishedNeedingAttention)} still {(finishedNeedingAttention == 1 ? "needs" : "need")} your attention", unreviewed);
    }

    /// <summary>Turning ephemeral mode on while job logs exist asks whether to delete them too.</summary>
    public static bool ShouldOfferLogDeletion(LoggingMode from, LoggingMode to, int jobFolderCount) =>
        from == LoggingMode.Normal && to == LoggingMode.Ephemeral && jobFolderCount > 0;

    /// <summary>
    /// A mode switch applies to new jobs only, so the tray says so when an active job runs
    /// under the other mode (and would otherwise look like it switched too).
    /// </summary>
    public static bool ModeChangeNeedsNotice(LoggingMode to, IReadOnlyList<JobSnapshot> jobs) =>
        jobs.Any(j => !JobStates.IsTerminal(j.State) && j.Logging != to);

    /// <summary>
    /// The one balloon a tray start shows after the settings-problem toast (a new balloon
    /// replaces the one before, so only the most important survives): possibly damaged files
    /// from interrupted jobs outrank a settings typo, which outranks the first-run hint. The
    /// hint shows only on the start the installer made (<see cref="CliRunTray.AfterInstall"/>),
    /// so it appears once per install in either logging mode and never on an ordinary start.
    /// </summary>
    public static StartupToast PickStartupToast(int interruptedJobs, bool settingsToastShown, bool afterInstall) =>
        interruptedJobs > 0 ? StartupToast.Interrupted
        : afterInstall && !settingsToastShown ? StartupToast.TrayHint
        : StartupToast.None;

    /// <summary>
    /// A click on a balloon goes to the target of the latest toast, which may not be the one
    /// clicked: the notification center keeps older ones. A click meant for the interrupted-paste
    /// notice after a "Copy finished" toast would open Jobs with nothing to show. So while that
    /// notice's log has not been opened, a Jobs-bound click that would find nothing needing
    /// attention opens the interrupted paste's log instead.
    /// </summary>
    public static bool JobsClickOpensInterrupted(IReadOnlyList<JobSnapshot> jobs, bool interruptedPending) =>
        interruptedPending && !jobs.Any(j => j.NeedsAttention);

    /// <summary>
    /// A tray click while a conflict question waits brings that dialog forward instead of
    /// opening Jobs. With several waiting, the one waiting longest. Null when none waits.
    /// </summary>
    public static Guid? ConflictToFront(IReadOnlyList<JobSnapshot> jobs) =>
        jobs.Where(j => j.State == JobState.AwaitingDecision)
            .OrderBy(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefault();

    /// <summary>
    /// True when <paramref name="configText"/> cannot be read as a JSON object at all, as
    /// opposed to one with bad fields (<see cref="SettingsLoadResult.Unreadable"/>).
    /// </summary>
    public static bool IsUnreadableConfig(string configText) => SettingsSerializer.Parse(configText).Unreadable;
}
