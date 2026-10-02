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
/// <see cref="ResolveConflictsAsync"/> is called from job worker threads; the dictionary of
/// open dialogs is touched on the UI thread only. Cancellation and <see cref="Shutdown"/>
/// complete the answer directly rather than through the UI thread, so the promise to answer
/// null holds even when the message loop has stopped pumping; the dialog itself is closed
/// when the loop gets to it. If building or showing the dialog throws, the task faults rather
/// than returning null, so the job ends Failed with a reason instead of looking like the user
/// canceled it.
/// </remarks>
internal sealed class UiPrompts : IJobPrompts
{
    private readonly Dictionary<Guid, OpenQuestion> _open = [];
    private readonly Lock _gate = new();
    private readonly HashSet<TaskCompletionSource<ConflictChoice?>> _unanswered = [];
    private bool _shutdown;

    public UiPrompts(SynchronizationContext ui, Func<ProgressWindowHost?> progressWindows)
    {
        Ui = ui;
        ProgressWindows = progressWindows;
    }

    public SynchronizationContext Ui { get; }

    /// <summary>Late-bound: the host is created after the job manager, which needs these prompts.</summary>
    public Func<ProgressWindowHost?> ProgressWindows { get; }

    private sealed record OpenQuestion(ConflictDialog Dialog, TaskCompletionSource<ConflictChoice?> Answer);

    public Task<ConflictChoice?> ResolveConflictsAsync(
        JobSnapshot job,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<ConflictChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Registered under the same lock Shutdown takes, so a question either sees the
        // shutdown here or is in the set Shutdown answers; it can never slip between them.
        lock (_gate)
        {
            if (_shutdown || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult<ConflictChoice?>(null);
            }
            _unanswered.Add(answer);
        }
        var registration = cancellationToken.Register(() =>
        {
            answer.TrySetResult(null);
            Post(() => CloseFor(job.Id, answer), answer);
        });
        _ = answer.Task.ContinueWith(
            _ =>
            {
                registration.Dispose();
                lock (_gate)
                {
                    _unanswered.Remove(answer);
                }
            },
            TaskScheduler.Default);
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
        if (!_open.TryGetValue(jobId, out var question) || question.Dialog.IsDisposed)
        {
            return false;
        }
        var dialog = question.Dialog;
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

    /// <summary>UI thread. Answers every open or pending question with null, closes the dialogs and refuses later questions.</summary>
    public void Shutdown()
    {
        List<TaskCompletionSource<ConflictChoice?>> pending;
        lock (_gate)
        {
            _shutdown = true;
            pending = [.. _unanswered];
        }
        foreach (var answer in pending)
        {
            answer.TrySetResult(null);
        }
        foreach (var question in _open.Values.ToList())
        {
            if (!question.Dialog.IsDisposed)
            {
                question.Dialog.Close();
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
        lock (_gate)
        {
            if (_shutdown)
            {
                answer.TrySetResult(null);
                return;
            }
        }
        ConflictDialog? dialog = null;
        try
        {
            // A second question for the same job replaces the first; the first answers null.
            if (_open.Remove(job.Id, out var earlier) && !earlier.Dialog.IsDisposed)
            {
                earlier.Dialog.Close();
            }

            dialog = new ConflictDialog(job, conflicts);
            var shown = dialog;
            shown.FormClosed += (_, _) =>
            {
                if (_open.TryGetValue(job.Id, out var current) && ReferenceEquals(current.Dialog, shown))
                {
                    _open.Remove(job.Id);
                }
                // A modeless form disposes itself after closing; nothing to release here.
                answer.TrySetResult(shown.Choice);
            };
            _open[job.Id] = new OpenQuestion(shown, answer);

            var owner = ProgressWindows()?.WindowFor(job.Id);
            if (owner is { IsDisposed: false })
            {
                if (owner.WindowState == FormWindowState.Minimized)
                {
                    owner.WindowState = FormWindowState.Normal;
                }
                shown.StartPosition = FormStartPosition.CenterParent;
                shown.Show(owner);
            }
            else
            {
                shown.Show();
            }
            // Foreground is granted only if Explorer's COM call allowed it; otherwise Windows
            // flashes the taskbar button, which still tells the user a question is waiting.
            shown.Activate();
        }
        catch (Exception ex)
        {
            // Fault first: disposing a shown dialog may raise FormClosed, whose null answer
            // would otherwise read as the user canceling.
            answer.TrySetException(ex);
            if (dialog is not null)
            {
                if (_open.TryGetValue(job.Id, out var current) && ReferenceEquals(current.Dialog, dialog))
                {
                    _open.Remove(job.Id);
                }
                dialog.Dispose();
            }
        }
    }

    /// <summary>Closes the dialog of this question only: a later question for the same job keeps its own.</summary>
    private void CloseFor(Guid jobId, TaskCompletionSource<ConflictChoice?> answer)
    {
        if (_open.TryGetValue(jobId, out var question) && ReferenceEquals(question.Answer, answer) && !question.Dialog.IsDisposed)
        {
            question.Dialog.Close();
        }
        answer.TrySetResult(null);
    }
}
