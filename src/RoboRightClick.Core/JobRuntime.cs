namespace RoboRightClick.Core;

/// <summary>Why a job is still Queued; shown in the Jobs and progress windows.</summary>
public enum JobWait
{
    None,

    /// <summary>maxConcurrentJobs is reached.</summary>
    ConcurrencyLimit,

    /// <summary>Another job writes where this one reads or writes (or the reverse).</summary>
    OverlappingJob,

    /// <summary><see cref="JobQueuePolicy.MaxConcurrentScans"/> jobs are already scanning.</summary>
    ScanLimit,
}

/// <summary>
/// Where a job reads and writes. Computed from the raw <see cref="PasteOrder"/> before
/// anything touches the disk, so the queue can decide on the UI thread's terms.
/// </summary>
public sealed record JobFootprint(string Destination, IReadOnlyList<string> Sources, bool Move)
{
    public static JobFootprint Of(PasteOrder order) => new(order.Destination, order.Sources, order.Verb == TransferVerb.Move);

    /// <summary>Locations this job changes: its destination, and for a cut its sources too.</summary>
    public IEnumerable<string> Writes => Move ? [Destination, .. Sources] : [Destination];

    public IEnumerable<string> Touches => [Destination, .. Sources];
}

/// <summary>When queued jobs may start.</summary>
public static class JobQueuePolicy
{
    /// <summary>
    /// Scanning walks whole trees, often over the network, on a dedicated thread per job.
    /// More than a few at once only makes each slower and starves the thread pool.
    /// </summary>
    public const int MaxConcurrentScans = 4;

    /// <summary>
    /// States that hold a concurrency slot. A paused job keeps its slot, as does one
    /// waiting on the conflict dialog: both still own a half-finished paste.
    /// </summary>
    public static bool HoldsSlot(JobState state) => state is
        JobState.Scanning or JobState.AwaitingDecision or JobState.Running or JobState.Paused or JobState.Finalizing;

    /// <summary>How many queued jobs may start now. maxConcurrentJobs 0 means unlimited (Explorer parity).</summary>
    public static int FreeSlots(int jobsHoldingSlots, int maxConcurrentJobs) =>
        maxConcurrentJobs <= 0 ? int.MaxValue : Math.Max(0, maxConcurrentJobs - jobsHoldingSlots);

    /// <summary>
    /// Two jobs must not run at the same time when either one writes where the other reads
    /// or writes. Explorer asks about each conflict at the moment it writes; this app asks
    /// once, after its scan, so a second paste into the same tree has to wait until the
    /// first is finished for its scan (and its cancel cleanup) to be accurate. Without this,
    /// a copy could overwrite files a concurrent cut has just moved, whose sources are
    /// already gone. Two copies that only read the same sources still run together.
    /// </summary>
    public static bool Conflicts(JobFootprint a, JobFootprint b) =>
        a.Writes.Any(w => b.Touches.Any(t => WinPath.Overlap(w, t)))
        || b.Writes.Any(w => a.Touches.Any(t => WinPath.Overlap(w, t)));

    /// <summary>
    /// Why <paramref name="candidate"/> cannot start yet, or <see cref="JobWait.None"/>.
    /// Queued jobs start in creation order, and a queued job also waits for older queued
    /// jobs it conflicts with, so two pastes into one folder run in click order.
    /// </summary>
    /// <param name="ahead">Footprints of jobs that hold a slot, plus older queued jobs.</param>
    public static JobWait WaitReason(
        JobFootprint candidate,
        IEnumerable<JobFootprint> ahead,
        int jobsHoldingSlots,
        int maxConcurrentJobs,
        int jobsScanning)
    {
        if (ahead.Any(a => Conflicts(candidate, a)))
        {
            return JobWait.OverlappingJob;
        }
        if (FreeSlots(jobsHoldingSlots, maxConcurrentJobs) == 0)
        {
            return JobWait.ConcurrencyLimit;
        }
        if (jobsScanning >= MaxConcurrentScans)
        {
            return JobWait.ScanLimit;
        }
        return JobWait.None;
    }
}

public static class PipeNames
{
    /// <summary>
    /// The /UNILOG pipe name for one robocopy run. The name is not a secret: it is on
    /// robocopy's command line, which any process of the same user can read. Uniqueness
    /// keeps runs apart; trust comes from creating the pipe first (FirstPipeInstance, one
    /// instance, current-user DACL) and from checking the client's PID before reading.
    /// </summary>
    public static string ForStep(Guid jobId, int stepIndex, ulong nonce) =>
        $"{AppInfo.Name}-{jobId:N}-{stepIndex}-{nonce:x16}";
}
