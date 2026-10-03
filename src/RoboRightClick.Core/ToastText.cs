namespace RoboRightClick.Core;

public enum ToastKind
{
    Info,
    Warning,
    Error,
}

public sealed record Toast(string Title, string Body, ToastKind Kind);

/// <summary>Why a Robo-Copy, Robo-Cut or Robo-Paste click did nothing. Each has one fixed, path-free sentence.</summary>
public enum VerbRefusal
{
    /// <summary>No files on the clipboard.</summary>
    ClipboardEmpty,

    /// <summary>The clipboard holds items that are not files on a drive (zip contents, mail attachments).</summary>
    ClipboardNotFiles,

    /// <summary>More than <see cref="ClipboardPayload.MaxDropFilesPaths"/> paths, or a block over the byte limit.</summary>
    ClipboardTooLarge,

    /// <summary>Another program kept the clipboard open through every retry.</summary>
    ClipboardBusy,

    /// <summary>The paste target has no file-system path (Libraries, This PC, Home).</summary>
    DestinationNotFileSystem,

    /// <summary>More than one destination folder selected.</summary>
    NotOneDestination,

    /// <summary>Robo-Copy/Robo-Cut on items with no file-system path.</summary>
    SelectionNotFiles,

    /// <summary>The same cut is already being pasted.</summary>
    AlreadyBeingMoved,

    /// <summary>
    /// The selection handed to the verb is over <see cref="SelectionLimits"/>: more items, or
    /// more path text, than one click accepts. Refused whole, never truncated.
    /// </summary>
    SelectionTooLarge,

    /// <summary>
    /// The Robo-Paste hotkey: File Explorer did not say which folder is open within
    /// <see cref="HotkeyDeadline.BudgetMs"/>. Nothing is pasted, and a late answer is dropped.
    /// </summary>
    ExplorerNotResponding,

    /// <summary>
    /// The Robo-Paste hotkey: no single open File Explorer tab matched the one that had the
    /// keyboard focus, so nothing is pasted rather than guessing (never a neighboring tab).
    /// </summary>
    FolderNotIdentified,

    /// <summary>
    /// The Robo-Paste hotkey right after a Ctrl+C or Ctrl+X: the copy or cut had not reached
    /// the clipboard within <see cref="ClipboardGuard.WaitMs"/>, so pasting would have used
    /// the previous clipboard (<see cref="ClipboardGuard"/>).
    /// </summary>
    ClipboardNotReady,

    /// <summary>
    /// Something unexpected stopped the verb before it could hand anything on. Without this,
    /// an exception inside the dispatcher would leave the click with no visible result.
    /// </summary>
    Failed,
}

/// <summary>
/// Notification text. Windows keeps toast text in the notification center database,
/// so a job created in ephemeral mode gets text with no file or folder names at all
/// (product invariant 2). The job's own mode decides, not the current one. Text that is
/// path-free by construction (refusals, settings) is the same in both modes.
/// </summary>
public static class ToastText
{
    public const string EphemeralBody = "Open Jobs from the tray icon for details.";

    /// <summary>
    /// The completion toast, or null for none. notifyOnComplete silences only clean
    /// finishes: errors need the user's "Try again / Skip", so they always notify.
    /// A cancel was the user's own action and gets no toast, unless it left files the
    /// user owned partly overwritten. A paste with nothing to do gets none.
    /// </summary>
    public static Toast? ForFinished(JobSnapshot job, bool notifyOnComplete)
    {
        if (job.NoOp)
        {
            return null;
        }
        if (job.State == JobState.Canceled)
        {
            return job.DamagedOnCancel > 0 ? ForCanceledWithDamage(job) : null;
        }
        if (job.State is not (JobState.Done or JobState.DoneWithErrors or JobState.Failed))
        {
            return null;
        }
        if (job.State == JobState.Done && !notifyOnComplete)
        {
            return null;
        }

        var kind = job.State switch
        {
            JobState.Done => ToastKind.Info,
            JobState.DoneWithErrors => ToastKind.Warning,
            _ => ToastKind.Error,
        };

        if (job.Logging == LoggingMode.Ephemeral)
        {
            var title = job.State switch
            {
                JobState.Done => "Job finished",
                JobState.DoneWithErrors => "Job finished with errors",
                _ => "Job failed",
            };
            return new Toast(title, EphemeralBody, kind);
        }

        var verb = job.Verb == TransferVerb.Move ? "Move" : "Copy";
        var done = job.Verb == TransferVerb.Move ? "moved" : "copied";
        var into = WinPath.GetFileName(job.Destination) is { Length: > 0 } name ? name : job.Destination;
        var pasted = Math.Max(0, job.Sources.Count - job.RefusedCount);
        // A move within one drive is a rename that writes no data, so it counts no bytes;
        // "(0 bytes)" would misstate the size of what moved. No size is shown then.
        var size = job.DoneBytes > 0 ? $" ({DisplayText.Bytes(job.DoneBytes)})" : string.Empty;
        return job.State switch
        {
            // Top-level items, as Explorer counts them: an empty folder is still "1 item".
            JobState.Done => new Toast(
                $"{verb} finished",
                $"{DisplayText.Items(pasted)}{size} to {into}",
                kind),
            JobState.DoneWithErrors when job.ErrorCount == 0 && job.RefusedCount > 0 => new Toast(
                $"{verb} finished with errors",
                $"{DisplayText.Items(job.RefusedCount)} could not be {done} to {into}. {job.RefusalReason}".TrimEnd(),
                kind),
            JobState.DoneWithErrors => new Toast(
                $"{verb} finished with errors",
                $"{DisplayText.Items(job.ErrorCount)} could not be {done} to {into}. Open Jobs to try again.",
                kind),
            _ => new Toast(
                $"{verb} failed",
                job.FailureReason is { Length: > 0 } reason
                    ? $"Nothing was {done} to {into}. {reason}"
                    : $"Nothing was {done} to {into}. Open Jobs for details.",
                kind),
        };
    }

    private static Toast ForCanceledWithDamage(JobSnapshot job)
    {
        if (job.Logging == LoggingMode.Ephemeral)
        {
            return new Toast("Job canceled", EphemeralBody, ToastKind.Warning);
        }
        var files = job.DamagedOnCancel == 1 ? "1 file was" : $"{job.DamagedOnCancel:N0} files were";
        return new Toast(
            job.Verb == TransferVerb.Move ? "Move canceled" : "Copy canceled",
            $"{files} being written when you canceled and may be incomplete. Open Jobs to finish them.",
            ToastKind.Warning);
    }

    /// <summary>Why a click did nothing. Path-free in every mode, and says what to do instead.</summary>
    public static Toast ForRefusal(VerbRefusal refusal) => refusal switch
    {
        VerbRefusal.ClipboardEmpty => new("Nothing to paste", "The clipboard has no files. Use Robo-Copy or Ctrl+C first.", ToastKind.Info),
        VerbRefusal.ClipboardNotFiles => new(
            "Can't Robo-Paste these items",
            "They aren't files on a drive (for example items inside a zip file or an email). Use Paste instead.",
            ToastKind.Warning),
        VerbRefusal.ClipboardTooLarge => new(
            "Too many items",
            $"Robo-Paste accepts up to {ClipboardPayload.MaxDropFilesPaths:N0} items at once. Copy their folder instead.",
            ToastKind.Warning),
        VerbRefusal.ClipboardBusy => new(
            "Clipboard is busy",
            "Another app is using the clipboard. Try again in a moment.",
            ToastKind.Warning),
        VerbRefusal.DestinationNotFileSystem => new(
            "Can't Robo-Paste here",
            "Robo-Paste works in folders on a drive or network share, not in libraries, zip files or other virtual folders.",
            ToastKind.Warning),
        VerbRefusal.NotOneDestination => new("Can't Robo-Paste here", "Select one destination folder.", ToastKind.Warning),
        VerbRefusal.SelectionNotFiles => new(
            "Can't Robo-Copy these items",
            "Only files and folders on a drive or network share can be Robo-copied or Robo-cut.",
            ToastKind.Warning),
        VerbRefusal.AlreadyBeingMoved => new("Already moving", "These items are already being moved.", ToastKind.Info),
        VerbRefusal.SelectionTooLarge => new(
            "Too many items selected",
            $"Robo-Copy, Robo-Cut and Robo-Paste take up to {SelectionLimits.MaxItems:N0} selected items at once, "
                + "fewer when their paths are very long. Select the folder that holds them instead.",
            ToastKind.Warning),
        VerbRefusal.ExplorerNotResponding => new(
            "File Explorer isn't responding",
            "Robo-Paste couldn't find out which folder is open, so nothing was pasted. Try again in a moment.",
            ToastKind.Warning),
        VerbRefusal.FolderNotIdentified => new(
            "Can't tell which folder is open",
            "Nothing was pasted. Click in the folder's file list and try again, or right-click its background and use Robo-Paste.",
            ToastKind.Warning),
        // The guard cannot tell a slow copy from a Ctrl+C that copied nothing (no selection),
        // so the text blames neither; after RecentMs the guard no longer applies.
        VerbRefusal.ClipboardNotReady => new(
            "Clipboard not ready",
            "The clipboard hasn't changed since the Ctrl+C or Ctrl+X just before, so nothing was pasted. "
                + $"Wait {ClipboardGuard.RecentMs / 1000} seconds and try again to paste what the clipboard holds then.",
            ToastKind.Warning),
        VerbRefusal.Failed => new("Something went wrong", "Robo-Copy, Robo-Cut or Robo-Paste could not finish. Try again.", ToastKind.Warning),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };

    /// <summary>At startup (normal mode): job logs show pastes that never finished because the app ended.</summary>
    public static Toast ForInterrupted(int count) => new(
        count == 1 ? "A paste was interrupted" : $"{count} pastes were interrupted",
        "RoboRightClick ended while copying. Some files at the destination may be incomplete. Open Jobs for details.",
        ToastKind.Warning);

    /// <summary>Ephemeral mode was switched while normal-mode jobs run.</summary>
    public static Toast ForModeAppliesToNewJobs(LoggingMode newMode) => new(
        newMode == LoggingMode.Ephemeral ? "Ephemeral mode on" : "Ephemeral mode off",
        "The change applies to new jobs. Jobs already running keep their mode.",
        ToastKind.Info);

    /// <summary>Another process (install, uninstall) asked the tray to exit while jobs run.</summary>
    public static Toast ForExitRefused() => new(
        "RoboRightClick is busy",
        "It was asked to close, but jobs are still running. Try again when they finish.",
        ToastKind.Warning);

    /// <summary>Windows cuts a balloon's text at 255 characters (NOTIFYICONDATA.szInfo).</summary>
    public const int MaxBalloonText = 255;

    /// <summary>
    /// First tray start after an install or upgrade: Windows 11 puts new tray icons in the
    /// hidden overflow. With the hotkey on, one sentence names it, so an upgrade that turns
    /// it on (a config without "pasteHotkey" means the default) says so.
    /// </summary>
    public static Toast ForTrayHint(HotkeySpec? pasteHotkey = null) => new(
        "RoboRightClick is running",
        "Right-click files, then Show more options, for Robo-Copy, Robo-Cut and Robo-Paste. "
            + (pasteHotkey is null ? string.Empty : $"{pasteHotkey.Format()} in a folder runs Robo-Paste (Settings changes it). ")
            + "Drag this icon out of the overflow to keep it visible.",
        ToastKind.Info);

    /// <summary>The tray menu's hotkey line; it opens Settings, hence the ellipsis.</summary>
    public static string HotkeyTrayLine(HotkeyStatus status, HotkeySpec? pasteHotkey) => status switch
    {
        HotkeyStatus.Active when pasteHotkey is not null => $"Robo-Paste hotkey: {pasteHotkey.Format()}…",
        HotkeyStatus.Invalid => "Robo-Paste hotkey: off (setting invalid)…",
        HotkeyStatus.Failed => "Robo-Paste hotkey: not active…",
        _ => "Robo-Paste hotkey: off…",
    };

    /// <summary>
    /// Path-free in every mode: setting names only. Shows the first problem so the user
    /// knows what to fix; Settings lists the rest. When this version may not save over the
    /// file (<paramref name="savesRefused"/>, <see cref="SettingsLoadResult.SavesRefused"/>),
    /// Settings cannot fix it, so the toast names the steps that can instead.
    /// </summary>
    public static Toast ForSettingsProblems(IReadOnlyList<string> problems, bool savesRefused = false)
    {
        var first = problems.Count > 0 ? problems[0] : "a setting was invalid";
        var more = problems.Count switch
        {
            <= 1 => string.Empty,
            2 => " (and 1 more)",
            _ => $" (and {problems.Count - 1} more)",
        };
        var next = savesRefused
            ? "Settings cannot save over this file; install the newer version, or delete config.json to start from the defaults."
            : "Open Settings to fix.";
        return new Toast("Settings problem", $"config.json: {first}{more}. {next}", ToastKind.Warning);
    }
}
