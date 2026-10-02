using System.Globalization;

namespace RoboRightClick.Core;

/// <summary>
/// Coalescing of job-finished toasts. A notification icon shows one balloon at a time and
/// each new one replaces the last, so several pastes finishing together would otherwise
/// leave only the final one visible.
/// </summary>
public static class ToastBatch
{
    /// <summary>Finishes this close together are reported as one toast.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>NOTIFYICONDATA.szInfoTitle holds 64 characters including the terminator.</summary>
    public const int MaxTitleLength = 63;

    /// <summary>NOTIFYICONDATA.szInfo holds 256 characters including the terminator.</summary>
    public const int MaxBodyLength = 255;

    public const string AttentionBody = "Open Jobs from the tray icon to try again or see what went wrong.";

    /// <summary>
    /// One toast for the jobs that finished within <see cref="Window"/>, or null when none
    /// of them would toast on its own (<see cref="ToastText.ForFinished"/>). A single job keeps
    /// its own text. Several get a count-only sentence ("3 pastes finished, 1 with errors")
    /// that names no file or folder in any mode: one batch can mix normal and ephemeral jobs,
    /// and Windows keeps toast text in the notification center. The kind is the worst in the
    /// batch, so an error is never reported as an Info.
    /// </summary>
    public static Toast? Combine(IReadOnlyList<JobSnapshot> finished, bool notifyOnComplete)
    {
        var toasting = new List<(JobSnapshot Job, Toast Toast)>();
        foreach (var job in finished)
        {
            if (ToastText.ForFinished(job, notifyOnComplete) is { } toast)
            {
                toasting.Add((job, toast));
            }
        }
        if (toasting.Count == 0)
        {
            return null;
        }
        if (toasting.Count == 1)
        {
            return toasting[0].Toast;
        }

        var withErrors = toasting.Count(t => t.Job.State == JobState.DoneWithErrors);
        var failed = toasting.Count(t => t.Job.State == JobState.Failed);
        var damaged = toasting.Count(t => t.Job.State == JobState.Canceled);
        var kind = toasting.Max(t => t.Toast.Kind);

        var title = string.Create(CultureInfo.InvariantCulture, $"{toasting.Count} pastes finished");
        if (withErrors > 0)
        {
            title += string.Create(CultureInfo.InvariantCulture, $", {withErrors} with errors");
        }
        if (failed > 0)
        {
            title += string.Create(CultureInfo.InvariantCulture, $", {failed} failed");
        }
        if (damaged > 0)
        {
            title += string.Create(CultureInfo.InvariantCulture, $", {damaged} canceled");
        }
        var body = kind == ToastKind.Info ? ToastText.EphemeralBody : AttentionBody;
        return Fit(new Toast(title, body, kind));
    }

    /// <summary>
    /// How long a shown toast is assumed to stay on screen when Windows never reports that it
    /// closed. The close report (NIN_BALLOONTIMEOUT / NIN_BALLOONHIDE) is not guaranteed, for
    /// example when Do Not Disturb files the toast away unseen; without a bound, one warning
    /// would silence every later info toast for the rest of the session.
    /// </summary>
    public static readonly TimeSpan AssumedOnScreen = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether an incoming toast may replace the one showing. A warning or error is never
    /// replaced by an info: a later success must not hide a failure the user has not seen.
    /// </summary>
    public static bool ShouldReplace(ToastKind showing, ToastKind incoming) =>
        !(incoming == ToastKind.Info && showing != ToastKind.Info);

    /// <summary>
    /// <see cref="ShouldReplace(ToastKind, ToastKind)"/> for a toast shown
    /// <paramref name="shownFor"/> ago: once that is <see cref="AssumedOnScreen"/> or more,
    /// the toast is taken to be gone and anything may show.
    /// </summary>
    public static bool ShouldReplace(ToastKind showing, TimeSpan shownFor, ToastKind incoming) =>
        shownFor >= AssumedOnScreen || ShouldReplace(showing, incoming);

    /// <summary>Trims title and body to what a balloon can hold, marking a cut with an ellipsis.</summary>
    public static Toast Fit(Toast toast) => toast with
    {
        Title = Truncate(toast.Title, MaxTitleLength),
        Body = Truncate(toast.Body, MaxBodyLength),
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
