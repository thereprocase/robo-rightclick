using RoboRightClick.Core;

namespace RoboRightClick.UI;

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

/// <summary>
/// Balloon notifications through the tray icon (Windows 10/11 show them as toasts and
/// keep them in the notification center). Text always comes from <see cref="ToastText"/>
/// and <see cref="ToastBatch"/>, which is where ephemeral path-freedom is enforced and
/// tested. UI thread only.
/// </summary>
/// <remarks>
/// NotifyIcon shows one balloon at a time and a new one replaces the current one. Job
/// finishes within <see cref="ToastBatch.Window"/> are coalesced into one toast
/// ("3 pastes finished, 1 with errors"), and a warning or error toast is never replaced by
/// an info toast while it is showing (<see cref="ToastBatch.ShouldReplace"/>). Immediate
/// toasts (<see cref="Show"/>) always show: they answer a click the user just made, and
/// staying silent would look like the click did nothing. A click raises
/// <see cref="Clicked"/> with the target of the last toast shown, rather than one
/// remembered job, because clicks from the notification center may arrive for older toasts.
/// </remarks>
internal sealed class Notifier : IDisposable
{
    /// <summary>Ignored by Windows 10 and later, which use the system's notification duration.</summary>
    private const int BalloonTimeoutMs = 10_000;

    private readonly List<JobSnapshot> _pending = [];
    private readonly System.Windows.Forms.Timer _batchTimer;
    private ToastKind? _showing;
    private ToastTarget _lastTarget = ToastTarget.None;

    public Notifier(NotifyIcon icon)
    {
        Icon = icon;
        _batchTimer = new System.Windows.Forms.Timer { Interval = (int)ToastBatch.Window.TotalMilliseconds };
        _batchTimer.Tick += (_, _) => FlushBatch();
        Icon.BalloonTipClicked += OnBalloonClicked;
        Icon.BalloonTipClosed += OnBalloonClosed;
    }

    public NotifyIcon Icon { get; }

    /// <summary>Raised when the user clicks a balloon; carries what the clicked toast was about.</summary>
    public event EventHandler<ToastTarget>? Clicked;

    /// <summary>Immediate toast (refusals, settings, interrupted jobs, tray notices).</summary>
    public void Show(Toast toast, ToastTarget target = ToastTarget.None) => Display(toast, target);

    /// <summary>A job finished: queued for the ~2 s coalescing window described above.</summary>
    public void JobFinished(JobSnapshot job, bool notifyOnComplete)
    {
        // The setting is applied now, per job, so a job that finished while
        // notifyOnComplete was off stays silent even if the batch flushes after it changed.
        if (ToastText.ForFinished(job, notifyOnComplete) is null)
        {
            return;
        }
        _pending.Add(job);
        if (!_batchTimer.Enabled)
        {
            _batchTimer.Start();
        }
    }

    public void Dispose()
    {
        _batchTimer.Stop();
        _batchTimer.Dispose();
        Icon.BalloonTipClicked -= OnBalloonClicked;
        Icon.BalloonTipClosed -= OnBalloonClosed;
        _pending.Clear();
    }

    private void FlushBatch()
    {
        _batchTimer.Stop();
        var batch = _pending.ToList();
        _pending.Clear();
        // notifyOnComplete was already applied per job in JobFinished.
        if (ToastBatch.Combine(batch, notifyOnComplete: true) is not { } toast)
        {
            return;
        }
        if (_showing is { } showing && !ToastBatch.ShouldReplace(showing, toast.Kind))
        {
            return;
        }
        Display(toast, ToastTarget.Jobs);
    }

    private void Display(Toast toast, ToastTarget target)
    {
        var fitted = ToastBatch.Fit(toast);
        _showing = fitted.Kind;
        _lastTarget = target;
        Icon.ShowBalloonTip(BalloonTimeoutMs, fitted.Title, fitted.Body, IconFor(fitted.Kind));
    }

    private static ToolTipIcon IconFor(ToastKind kind) => kind switch
    {
        ToastKind.Error => ToolTipIcon.Error,
        ToastKind.Warning => ToolTipIcon.Warning,
        _ => ToolTipIcon.Info,
    };

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        _showing = null;
        OnClicked(_lastTarget);
    }

    private void OnBalloonClosed(object? sender, EventArgs e) => _showing = null;

    private void OnClicked(ToastTarget target) => Clicked?.Invoke(this, target);
}
