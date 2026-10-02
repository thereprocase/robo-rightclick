using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class JobSchedulerTests
{
    private static JobFootprint Copy(string destination, params string[] sources) =>
        JobFootprint.Of(new PasteOrder(sources, destination, TransferVerb.Copy));

    private static JobFootprint Cut(string destination, params string[] sources) =>
        JobFootprint.Of(new PasteOrder(sources, destination, TransferVerb.Move));

    private static (Guid Id, JobFootprint Footprint) Queued(JobFootprint footprint) => (Guid.NewGuid(), footprint);

    private static JobWait[] Decide(
        IReadOnlyList<(Guid Id, JobFootprint Footprint)> queued,
        IReadOnlyList<JobFootprint>? holding = null,
        int scanning = 0,
        int maxConcurrentJobs = 0)
    {
        holding ??= [];
        var decisions = JobScheduler.Decide(queued, holding, holding.Count, scanning, maxConcurrentJobs);
        Assert.Equal(queued.Select(q => q.Id), decisions.Select(d => d.Id));
        return decisions.Select(d => d.Wait).ToArray();
    }

    [Fact]
    public void Two_pastes_into_one_folder_run_in_click_order()
    {
        var first = Queued(Copy(@"D:\Archive", @"C:\a.txt"));
        var second = Queued(Copy(@"D:\Archive", @"C:\b.txt"));

        Assert.Equal([JobWait.None, JobWait.OverlappingJob], Decide([first, second]));
    }

    [Fact]
    public void A_paste_into_a_folder_inside_an_earlier_paste_waits_for_it()
    {
        var outer = Queued(Copy(@"D:\Archive", @"C:\a"));
        var inner = Queued(Copy(@"D:\archive\2026", @"C:\b"));

        Assert.Equal([JobWait.None, JobWait.OverlappingJob], Decide([outer, inner]));
    }

    [Fact]
    public void Unrelated_pastes_run_together()
    {
        var a = Queued(Copy(@"D:\One", @"C:\a"));
        var b = Queued(Copy(@"E:\Two", @"C:\b"));
        var c = Queued(Cut(@"F:\Three", @"C:\c"));

        Assert.Equal([JobWait.None, JobWait.None, JobWait.None], Decide([a, b, c]));
    }

    [Fact]
    public void Two_copies_that_only_read_the_same_sources_run_together()
    {
        var a = Queued(Copy(@"D:\One", @"C:\shared"));
        var b = Queued(Copy(@"E:\Two", @"C:\shared"));

        Assert.Equal([JobWait.None, JobWait.None], Decide([a, b]));
    }

    [Fact]
    public void A_running_cut_blocks_a_copy_reading_its_sources()
    {
        var copy = Queued(Copy(@"E:\Backup", @"C:\Projects\report.docx"));

        Assert.Equal([JobWait.OverlappingJob], Decide([copy], holding: [Cut(@"D:\Moved", @"C:\Projects")]));
    }

    [Fact]
    public void A_queued_cut_blocks_a_later_copy_reading_its_sources()
    {
        var cut = Queued(Cut(@"D:\Moved", @"C:\Projects"));
        var copy = Queued(Copy(@"E:\Backup", @"C:\Projects\report.docx"));

        Assert.Equal([JobWait.None, JobWait.OverlappingJob], Decide([cut, copy]));
    }

    [Fact]
    public void A_copy_never_jumps_ahead_of_an_older_conflicting_job_that_is_still_waiting()
    {
        // The older job waits behind the running one; the younger one conflicts with the
        // older and must not overtake it, even though it does not touch the running job.
        var running = Copy(@"D:\Archive", @"C:\a");
        var older = Queued(Copy(@"D:\Archive", @"E:\b"));
        var younger = Queued(Cut(@"F:\Elsewhere", @"E:\b"));

        Assert.Equal(
            [JobWait.OverlappingJob, JobWait.OverlappingJob],
            Decide([older, younger], holding: [running]));
    }

    [Fact]
    public void An_unrelated_job_may_start_while_an_older_one_waits_on_a_conflict()
    {
        var running = Copy(@"D:\Archive", @"C:\a");
        var blocked = Queued(Copy(@"D:\Archive", @"C:\b"));
        var unrelated = Queued(Copy(@"E:\Other", @"C:\c"));

        Assert.Equal([JobWait.OverlappingJob, JobWait.None], Decide([blocked, unrelated], holding: [running]));
    }

    [Fact]
    public void The_concurrency_limit_counts_jobs_started_in_the_same_pass()
    {
        var queued = Enumerable.Range(0, 3).Select(i => Queued(Copy($@"D:\T{i}", $@"C:\s{i}"))).ToList();

        Assert.Equal(
            [JobWait.None, JobWait.ConcurrencyLimit, JobWait.ConcurrencyLimit],
            Decide(queued, holding: [Copy(@"E:\Busy", @"C:\busy")], scanning: 0, maxConcurrentJobs: 2));
    }

    [Fact]
    public void Unlimited_concurrency_starts_everything_that_does_not_conflict()
    {
        var queued = Enumerable.Range(0, 3).Select(i => Queued(Copy($@"D:\T{i}", $@"C:\s{i}"))).ToList();
        var holding = Enumerable.Range(0, 50).Select(i => Copy($@"E:\H{i}", $@"C:\h{i}")).ToList();

        Assert.Equal([JobWait.None, JobWait.None, JobWait.None], Decide(queued, holding, scanning: 0, maxConcurrentJobs: 0));
    }

    [Fact]
    public void The_scan_cap_counts_jobs_started_in_the_same_pass()
    {
        var queued = Enumerable.Range(0, 3).Select(i => Queued(Copy($@"D:\T{i}", $@"C:\s{i}"))).ToList();

        Assert.Equal(
            [JobWait.None, JobWait.ScanLimit, JobWait.ScanLimit],
            Decide(queued, scanning: JobQueuePolicy.MaxConcurrentScans - 1));
    }

    [Fact]
    public void A_full_scan_cap_holds_every_queued_job()
    {
        var queued = Enumerable.Range(0, 2).Select(i => Queued(Copy($@"D:\T{i}", $@"C:\s{i}"))).ToList();

        Assert.Equal([JobWait.ScanLimit, JobWait.ScanLimit], Decide(queued, scanning: JobQueuePolicy.MaxConcurrentScans));
    }

    [Fact]
    public void Started_entries_count_as_ahead_for_later_entries()
    {
        // Nothing holds a slot, so the first entry starts in this pass. The second
        // conflicts only with it and must still wait: had the first one not been counted,
        // both pastes into the same folder would scan at once and race on its names.
        var first = Queued(Cut(@"D:\Inbox", @"C:\mail"));
        var second = Queued(Copy(@"E:\Backup", @"D:\Inbox\today"));

        var waits = Decide([first, second]);

        Assert.Equal(JobWait.None, waits[0]);
        Assert.Equal(JobWait.OverlappingJob, waits[1]);
    }

    [Fact]
    public void An_empty_queue_decides_nothing()
    {
        Assert.Empty(JobScheduler.Decide([], [Copy(@"D:\x", @"C:\y")], 1, 1, 0));
    }
}
