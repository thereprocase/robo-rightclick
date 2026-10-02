using System.Text.Json;

namespace RoboRightClick.Core;

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

/// <summary>Visibility and check states of the tray menu items that depend on app state.</summary>
public sealed record TrayMenu(bool OpenLogsVisible, bool PauseAllChecked, bool EphemeralChecked)
{
    /// <summary>
    /// "Open logs" is hidden in ephemeral mode: the mode promises nothing about jobs is on
    /// disk, and a menu item leading to a logs folder would say otherwise.
    /// </summary>
    public static TrayMenu For(LoggingMode mode, bool pauseAllActive) => new(
        OpenLogsVisible: mode == LoggingMode.Normal,
        PauseAllChecked: pauseAllActive,
        EphemeralChecked: mode == LoggingMode.Ephemeral);
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
    /// With no active jobs, exit at once. With active jobs, the user is asked (exit cancels
    /// them), and another process's request is refused: an upgrade or uninstall must never
    /// cancel a paste the user did not choose to cancel.
    /// </summary>
    public static ExitAction ExitDecision(bool hasActiveJobs, bool requestedByOtherProcess) =>
        !hasActiveJobs ? ExitAction.Exit
        : requestedByOtherProcess ? ExitAction.RefuseWithToast
        : ExitAction.ConfirmWithUser;

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
    /// First tray start after install: no history file and no jobs folder yet, so Windows 11
    /// has only just put the icon in the hidden overflow. Ephemeral mode never creates
    /// either, so it never counts as a first run; otherwise the hint would show on every start.
    /// </summary>
    public static bool IsFirstRun(LoggingMode mode, bool historyFileExists, bool jobsDirectoryExists) =>
        mode == LoggingMode.Normal && !historyFileExists && !jobsDirectoryExists;

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
    /// opposed to one with bad fields. Only then does the next save keep a config.json.bad
    /// copy: per-field fallback already preserves everything else in a readable file, but an
    /// unreadable one would otherwise be overwritten by defaults and lost.
    /// </summary>
    /// <remarks>
    /// Mirrors the document options of <see cref="SettingsSerializer.Parse"/> (comments,
    /// trailing commas); a test pins the two together.
    /// </remarks>
    public static bool IsUnreadableConfig(string configText)
    {
        try
        {
            using var document = JsonDocument.Parse(configText, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return document.RootElement.ValueKind != JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
