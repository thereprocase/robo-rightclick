namespace RoboRightClick.Jobs;

/// <summary>
/// A job's pause switch, shared by everything that runs inside the job. Robocopy steps
/// suspend their process when it closes; in-process copies block in their progress
/// callback; the job loop waits on it between steps. Thread-safe.
/// </summary>
internal sealed class PauseGate
{
    public bool IsPaused => throw new NotImplementedException();

    /// <summary>Raised (on the caller's thread) after every change, so a running robocopy step can suspend or resume its process.</summary>
    public event EventHandler<bool>? Changed;

    public void Pause() => throw new NotImplementedException();

    public void Resume() => throw new NotImplementedException();

    /// <summary>Completes immediately when open; otherwise when resumed or canceled.</summary>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    /// <summary>Blocking form for CopyFileEx's progress callback, which runs on the copying thread.</summary>
    public void WaitWhilePaused(CancellationToken cancellationToken) => throw new NotImplementedException();

    private void OnChanged(bool paused) => Changed?.Invoke(this, paused);
}
