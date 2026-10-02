namespace RoboRightClick.Core;

/// <summary>
/// One pass of the start queue: which queued jobs may start now, and why the others wait.
/// The host runs it on every job state change and on every enqueue, and starts each job
/// that comes back <see cref="JobWait.None"/>.
/// </summary>
public static class JobScheduler
{
    /// <summary>
    /// Decides every queued job, walking the queue in creation order. Each entry is judged
    /// by <see cref="JobQueuePolicy.WaitReason"/> with "ahead" = the jobs holding a slot plus
    /// every earlier queued entry, started in this pass or not: an older paste that is still
    /// waiting keeps a younger, conflicting one behind it, so two pastes into one folder run
    /// in click order. An entry started in this pass moves into Scanning, so it counts
    /// towards the slots and the scan cap of the entries after it.
    /// </summary>
    /// <param name="queued">Queued jobs in creation order.</param>
    /// <param name="holding">Footprints of jobs in states that <see cref="JobQueuePolicy.HoldsSlot"/>.</param>
    /// <param name="holdingCount">Jobs holding a slot (normally <c>holding.Count</c>).</param>
    /// <param name="scanningCount">Jobs in Scanning.</param>
    /// <param name="maxConcurrentJobs">The setting; 0 means unlimited.</param>
    /// <returns>One entry per queued job, in the same order.</returns>
    public static IReadOnlyList<(Guid Id, JobWait Wait)> Decide(
        IReadOnlyList<(Guid Id, JobFootprint Footprint)> queued,
        IReadOnlyList<JobFootprint> holding,
        int holdingCount,
        int scanningCount,
        int maxConcurrentJobs)
    {
        var ahead = new List<JobFootprint>(holding);
        var decisions = new List<(Guid, JobWait)>(queued.Count);
        foreach (var (id, footprint) in queued)
        {
            var wait = JobQueuePolicy.WaitReason(footprint, ahead, holdingCount, maxConcurrentJobs, scanningCount);
            if (wait == JobWait.None)
            {
                holdingCount++;
                scanningCount++;
            }
            ahead.Add(footprint);
            decisions.Add((id, wait));
        }
        return decisions;
    }
}
