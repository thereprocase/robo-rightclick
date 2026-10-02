using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// Balloon notifications through the tray icon (Windows 10/11 show them as toasts and
/// keep them in the notification center). Text always comes from <see cref="ToastText"/>,
/// which is where ephemeral path-freedom is enforced and tested. UI thread only.
/// </summary>
/// <remarks>
/// NotifyIcon shows one balloon at a time and a new one replaces the current one. Job
/// finishes within about 2 s are coalesced into one toast (Core's batching rule:
/// "3 pastes finished, 1 with errors"), and an error toast is never replaced by a success
/// toast while it is showing. A click opens Jobs filtered to items needing attention,
/// rather than one remembered job, because clicks from the notification center may arrive
/// for older toasts.
/// </remarks>
/// <summary>What a click on a toast opens.</summary>
internal enum ToastTarget
{
    /// <summary>The Jobs window, filtered to jobs needing attention.</summary>
    Jobs,

    /// <summary>The Settings window (settings-problem toast).</summary>
    Settings,

    /// <summary>Nothing (informational toasts such as the tray hint).</summary>
    None,
}

internal sealed class Notifier
{
    public Notifier(NotifyIcon icon)
    {
        Icon = icon;
    }

    public NotifyIcon Icon { get; }

    /// <summary>Raised when the user clicks a balloon; carries what the clicked toast was about.</summary>
    public event EventHandler<ToastTarget>? Clicked;

    /// <summary>Immediate toast (refusals, settings, interrupted jobs, tray notices).</summary>
    public void Show(Toast toast, ToastTarget target = ToastTarget.None) => throw new NotImplementedException();

    /// <summary>A job finished: queued for the ~2 s coalescing window described above.</summary>
    public void JobFinished(JobSnapshot job, bool notifyOnComplete) => throw new NotImplementedException();

    private void OnClicked(ToastTarget target) => Clicked?.Invoke(this, target);
}
