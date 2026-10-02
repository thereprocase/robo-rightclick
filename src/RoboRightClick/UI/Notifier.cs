using RoboRightClick.Core;

namespace RoboRightClick.UI;

/// <summary>
/// Balloon notifications through the tray icon (Windows 10/11 show them as toasts and
/// keep them in the notification center). Text always comes from <see cref="ToastText"/>,
/// which is where ephemeral path-freedom is enforced and tested. UI thread only.
/// </summary>
internal sealed class Notifier
{
    public Notifier(NotifyIcon icon)
    {
        Icon = icon;
    }

    public NotifyIcon Icon { get; }

    /// <summary>Raised when the user clicks the most recent balloon; carries the job it was about, if any.</summary>
    public event EventHandler<Guid?>? Clicked;

    /// <summary>ShowBalloonTip with the toast's title, body and icon kind; remembers the job for <see cref="Clicked"/>.</summary>
    public void Show(Toast toast, Guid? jobId = null) => throw new NotImplementedException();

    private void OnClicked(Guid? jobId) => Clicked?.Invoke(this, jobId);
}
