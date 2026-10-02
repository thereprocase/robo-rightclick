using RoboRightClick.Core;
using RoboRightClick.Jobs;

namespace RoboRightClick.UI;

/// <summary>
/// <see cref="IJobPrompts"/> for the real UI: posts to the UI thread, shows a modeless
/// <see cref="ConflictDialog"/> (several jobs may ask at once, as several Explorer copy
/// dialogs can), owned by the job's progress window when one is open, and completes the
/// task when it closes. Cancellation closes the dialog and returns null.
/// <see cref="Shutdown"/> closes every open dialog and makes later requests return null at
/// once, so exit never waits on a question nobody will answer.
/// </summary>
/// <remarks>
/// <see cref="ResolveConflictsAsync"/> is called from job worker threads; everything else,
/// including the dictionary of open dialogs, runs on the UI thread only. If building or
/// showing the dialog throws, the task faults rather than returning null, so the job ends
/// Failed with a reason instead of looking like the user canceled it.
/// </remarks>
internal sealed class UiPrompts : IJobPrompts
{
    private readonly Dictionary<Guid, ConflictDialog> _open = [];
    private volatile bool _shutdown;

    public UiPrompts(SynchronizationContext ui, Func<ProgressWindowHost?> progressWindows)
    {
        Ui = ui;
        ProgressWindows = progressWindows;
    }

    public SynchronizationContext Ui { get; }

    /// <summary>Late-bound: the host is created after the job manager, which needs these prompts.</summary>
    public Func<ProgressWindowHost?> ProgressWindows { get; }

    public Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        if (_shutdown || cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<ConflictChoice?>(null);
        }
        var answer = new TaskCompletionSource<ConflictChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => Post(() => CloseFor(job.Id, answer), answer));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        Post(() => Ask(job, conflicts, answer), answer);
        return answer.Task;
    }

    /// <summary>
    /// Posts to the UI thread. Once the message loop has ended a post can throw; the question
    /// then answers null at once, exactly as after <see cref="Shutdown"/>.
    /// </summary>
    private void Post(Action action, TaskCompletionSource<ConflictChoice?> answer)
    {
        try
        {
            Ui.Post(_ => action(), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            answer.TrySetResult(null);
        }
    }

    /// <summary>Brings the job's open conflict dialog to the front; false when it has none.</summary>
    public bool Activate(Guid jobId)
    {
        if (!_open.TryGetValue(jobId, out var dialog) || dialog.IsDisposed)
        {
            return false;
        }
        if (dialog.Owner is { WindowState: FormWindowState.Minimized } owner)
        {
            owner.WindowState = FormWindowState.Normal;
        }
        if (dialog.WindowState == FormWindowState.Minimized)
        {
            dialog.WindowState = FormWindowState.Normal;
        }
        dialog.Activate();
        dialog.BringToFront();
        return true;
    }

    /// <summary>UI thread. Closes every open dialog (each answers null) and refuses later questions.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        foreach (var dialog in _open.Values.ToList())
        {
            if (!dialog.IsDisposed)
            {
                dialog.Close();
            }
        }
        _open.Clear();
    }

    private void Ask(JobSnapshot job, IReadOnlyList<FileConflict> conflicts, TaskCompletionSource<ConflictChoice?> answer)
    {
        if (answer.Task.IsCompleted)
        {
            return;
        }
        if (_shutdown)
        {
            answer.TrySetResult(null);
            return;
        }
        try
        {
            // A second question for the same job replaces the first; the first answers null.
            if (_open.Remove(job.Id, out var earlier) && !earlier.IsDisposed)
            {
                earlier.Close();
            }

            var dialog = new ConflictDialog(job, conflicts);
            dialog.FormClosed += (_, _) =>
            {
                if (_open.TryGetValue(job.Id, out var current) && ReferenceEquals(current, dialog))
                {
                    _open.Remove(job.Id);
                }
                // A modeless form disposes itself after closing; nothing to release here.
                answer.TrySetResult(dialog.Choice);
            };
            _open[job.Id] = dialog;

            var owner = ProgressWindows()?.WindowFor(job.Id);
            if (owner is { IsDisposed: false })
            {
                if (owner.WindowState == FormWindowState.Minimized)
                {
                    owner.WindowState = FormWindowState.Normal;
                }
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Show(owner);
            }
            else
            {
                dialog.Show();
            }
            // Foreground is granted only if Explorer's COM call allowed it; otherwise Windows
            // flashes the taskbar button, which still tells the user a question is waiting.
            dialog.Activate();
        }
        catch (Exception ex)
        {
            _open.Remove(job.Id);
            answer.TrySetException(ex);
        }
    }

    private void CloseFor(Guid jobId, TaskCompletionSource<ConflictChoice?> answer)
    {
        if (_open.TryGetValue(jobId, out var dialog) && !dialog.IsDisposed)
        {
            dialog.Close();
        }
        answer.TrySetResult(null);
    }
}
