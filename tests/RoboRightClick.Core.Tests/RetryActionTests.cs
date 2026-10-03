using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// One rule decides what "Try again" does (<see cref="RetryRules.ActionFor"/>); the label, the
/// summary's sentences and the toast are built from it, so the button never promises a few
/// files while re-running the whole paste.
/// </summary>
public class RetryActionTests
{
    private static JobSnapshot Job(JobState state, TransferVerb verb = TransferVerb.Move) =>
        DisplayAndTrayTests.Job(state) with { Verb = verb };

    [Fact]
    public void A_cancel_with_unreliable_paths_finishes_only_the_files_it_left()
    {
        // A sync client wrote into the source during the cut, so the ledger lost track of
        // robocopy's paths; the user canceled, and one file was left possibly incomplete. The
        // files a cancel leaves are known by their own paths: "Finish moving them" repeats
        // those, and never the move the user canceled.
        var job = Job(JobState.Canceled) with { PathsUnreliable = true, DamagedOnCancel = 1, RetryCount = 1 };

        Assert.Equal(RetryAction.RepeatFiles, RetryRules.ActionFor(job));
        Assert.False(job.RetriesWholePaste);
        Assert.Equal("Finish moving them", JobStateText.TryAgainLabel(job));
        Assert.Contains("Open Jobs to finish them.", ToastText.ForFinished(job, notifyOnComplete: true)!.Body);
    }

    [Fact]
    public void A_whole_rerun_of_a_canceled_paste_is_labeled_and_explained_as_one()
    {
        // Nothing per file to repeat, but robocopy had failed on a file before the cancel.
        var job = Job(JobState.Canceled) with { PathsUnreliable = true, MayBeIncomplete = 1, RetryCount = 0 };

        Assert.Equal(RetryAction.WholePaste, RetryRules.ActionFor(job));
        Assert.Equal(JobStateText.WholePasteLabel, JobStateText.TryAgainLabel(job));
        Assert.Contains("including what you canceled", JobStateText.SummaryHeading(job));
    }

    [Fact]
    public void Damaged_files_with_nothing_to_finish_never_mention_finishing()
    {
        // A cut whose interrupted files' sources are gone: the moves had finished.
        var job = Job(JobState.Canceled) with { DamagedOnCancel = 2, RetryCount = 0 };

        Assert.Null(JobStateText.TryAgainLabel(job));
        Assert.DoesNotContain("Finish", JobStateText.DamagedText(job));
        Assert.DoesNotContain("finish them", ToastText.ForFinished(job, notifyOnComplete: true)!.Body);
        Assert.Contains("Finish moving them", JobStateText.DamagedText(job with { RetryCount = 2 }));
    }

    public static TheoryData<JobState, bool, int, int, int, int> Snapshots()
    {
        var data = new TheoryData<JobState, bool, int, int, int, int>();
        foreach (var state in new[] { JobState.Done, JobState.DoneWithErrors, JobState.Failed, JobState.Canceled })
        {
            foreach (var unreliable in new[] { false, true })
            {
                foreach (var damaged in new[] { 0, 2 })
                {
                    foreach (var retryCount in new[] { 0, 3 })
                    {
                        foreach (var mayBeIncomplete in new[] { 0, 1 })
                        {
                            foreach (var errors in new[] { 0, 1 })
                            {
                                data.Add(state, unreliable, damaged, retryCount, mayBeIncomplete, errors);
                            }
                        }
                    }
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void The_label_always_says_what_the_button_does(JobState state, bool unreliable, int damaged, int retryCount, int mayBeIncomplete, int errors)
    {
        var job = Job(state) with
        {
            PathsUnreliable = unreliable,
            DamagedOnCancel = state == JobState.Canceled ? damaged : 0,
            RetryCount = retryCount,
            MayBeIncomplete = mayBeIncomplete,
            ErrorCount = errors,
        };
        var action = RetryRules.ActionFor(job);
        var label = JobStateText.TryAgainLabel(job);

        Assert.Equal(action == RetryAction.None, label is null);
        if (label == JobStateText.FinishLabel(job) || label?.StartsWith("Try again (", StringComparison.Ordinal) == true)
        {
            // A label that names some files is never on a button that repeats the whole paste.
            Assert.Equal(RetryAction.RepeatFiles, action);
        }
        if (action == RetryAction.WholePaste && state == JobState.Canceled)
        {
            Assert.Equal(JobStateText.WholePasteLabel, label);
        }
        Assert.Equal(action == RetryAction.WholePaste, job.RetriesWholePaste);
    }

    // ---------------------------------------------------------------- kept suspected files

    [Fact]
    public void A_done_child_that_kept_suspected_files_does_not_point_at_a_missing_button()
    {
        var job = Job(JobState.Done, TransferVerb.Copy) with { MayBeIncomplete = 2, KeptIncomplete = 2 };

        Assert.Null(JobStateText.TryAgainLabel(job));
        var text = JobStateText.MayBeIncompleteText(job);
        Assert.DoesNotContain("Try again", text);
        Assert.Contains("kept, as you chose", text);
        Assert.Contains("Robo-Copy the source again and choose Replace", text);
    }

    [Theory]
    [InlineData(JobState.DoneWithErrors)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Canceled)]
    public void Kept_files_are_never_described_as_stopped_mid_write_or_failed_by_robocopy(JobState state)
    {
        var job = Job(state) with { MayBeIncomplete = 1, KeptIncomplete = 1, RetryCount = 3 };

        var text = JobStateText.MayBeIncompleteText(job);

        Assert.DoesNotContain("not finished", text);
        Assert.DoesNotContain("being written", text);
        Assert.DoesNotContain(JobStateText.TryAgainLabel(job) ?? "\0", text);
        Assert.Contains("Robo-Cut the source again and choose Replace", text);
    }

    [Fact]
    public void Files_robocopy_left_and_files_the_user_kept_are_told_apart()
    {
        var job = Job(JobState.DoneWithErrors, TransferVerb.Copy) with { MayBeIncomplete = 3, KeptIncomplete = 1, RetryCount = 2 };

        var text = JobStateText.MayBeIncompleteText(job);

        Assert.StartsWith("2 files were not finished: robocopy reported an error on them", text);
        Assert.Contains("1 file the earlier paste may have left incomplete was kept", text);
        Assert.Contains("Try again (2), or check them before you use them.", text);
    }

    [Fact]
    public void A_cancel_with_unreliable_paths_does_not_offer_its_button_for_files_it_will_not_repeat()
    {
        // "Finish moving them" repeats only the files the cancel left; robocopy's earlier
        // failures are not in that plan when the ledger lost track of its paths.
        var job = Job(JobState.Canceled) with { PathsUnreliable = true, DamagedOnCancel = 1, RetryCount = 1, MayBeIncomplete = 1 };

        Assert.EndsWith("Check them before you use them.", JobStateText.MayBeIncompleteText(job));
        Assert.Contains("Finish moving them, or check", JobStateText.MayBeIncompleteText(job with { PathsUnreliable = false }));
    }

    // ---------------------------------------------------------------- a re-run of a cut

    [Fact]
    public void A_whole_rerun_of_a_cut_does_not_report_what_the_earlier_paste_moved_as_missing()
    {
        // Folder A was moved whole; B is still at the source; C is missing and not at the destination.
        var disk = new FakeDisk().Dir(@"C:\src").Dir(@"C:\src\B").File(@"C:\src\B\x.txt").Dir(@"D:\dst").Dir(@"D:\dst\A").File(@"D:\dst\A\y.txt");
        var order = new PasteOrder([@"C:\src\A", @"C:\src\B", @"C:\src\C"], @"D:\dst", TransferVerb.Move);
        var plan = PastePlanner.Plan(order, disk);

        var rerun = PastePlanner.WithoutAlreadyMoved(plan, order.Destination, disk);

        Assert.Equal([new PlanIssue(@"C:\src\C", PastePlanner.MissingReason)], rerun.Rejected);
        Assert.Equal([new PlanIssue(@"C:\src\A", PastePlanner.AlreadyMovedReason)], rerun.NoOps);
        Assert.Equal(plan.Steps, rerun.Steps);
    }
}
