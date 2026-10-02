namespace RoboRightClick.Core;

/// <summary>When queued jobs may start.</summary>
public static class JobQueuePolicy
{
    /// <summary>
    /// States that hold a concurrency slot. A paused job keeps its slot, as does one
    /// waiting on the conflict dialog: both still own a half-finished paste.
    /// </summary>
    public static bool HoldsSlot(JobState state) => state is
        JobState.Scanning or JobState.AwaitingDecision or JobState.Running or JobState.Paused or JobState.Finalizing;

    /// <summary>How many queued jobs may start now. maxConcurrentJobs 0 means unlimited (Explorer parity).</summary>
    public static int FreeSlots(int jobsHoldingSlots, int maxConcurrentJobs) =>
        maxConcurrentJobs <= 0 ? int.MaxValue : Math.Max(0, maxConcurrentJobs - jobsHoldingSlots);
}

public static class PipeNames
{
    /// <summary>
    /// The /UNILOG pipe name for one robocopy run. The nonce comes from a CSPRNG in the
    /// host: an unguessable name means no other process can create the pipe first, and
    /// the client-PID check catches anything that connects to ours.
    /// </summary>
    public static string ForStep(Guid jobId, int stepIndex, ulong nonce) =>
        $"{AppInfo.Name}-{jobId:N}-{stepIndex}-{nonce:x16}";
}
