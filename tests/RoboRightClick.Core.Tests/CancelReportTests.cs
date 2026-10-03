using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// A cancel does not undo what went wrong before it: files robocopy had already failed on
/// stay in the report, keep the job in the attention state and keep "Try again".
/// </summary>
public class CancelReportTests
{
    private static PlannedFile File(string name, long size = 10) =>
        new($@"C:\src\{name}", $@"\\nas\share\{name}", new FileFacts(size, FakeDisk.BaseTime));

    private static readonly PlannedFile Video = File("video.mp4", 2_000);
    private static readonly PlannedFile Photo = File("photo.jpg");
    private static readonly PlannedFile Big = File("big.iso", 4_000);

    private static JobSnapshot Canceled() =>
        DisplayAndTrayTests.Job(JobState.Canceled);

    [Fact]
    public void A_file_robocopy_failed_on_before_the_cancel_may_be_incomplete_and_is_retryable_but_the_interrupted_one_is_cleanups()
    {
        var plan = new ExecutionPlan(
            [new ExecutionStep(new RobocopyStep(@"C:\src", @"\\nas\share", ["video.mp4", "photo.jpg", "big.iso"], Recursive: false, Move: false), ConflictPolicy.Ask, [Video, Photo, Big])],
            [], [], [], []);
        var ledger = new StepLedger(plan);
        // A Wi-Fi drop: "ERROR 64 ... Copying File ...\video.mp4" (retries 0); the copy goes on.
        ledger.Apply(0, new ErrorReported(64, "Copying File", Video.SourcePath, "The specified network name is no longer available."));
        ledger.Apply(0, new FileReported(10, Photo.SourcePath));
        // The user cancels while robocopy writes big.iso.
        ledger.StepFinished(0, exitCode: null, killedByCancel: true);

        Assert.Equal([Video], ledger.MayBeIncomplete);
        Assert.Equal([(0, Video)], ledger.Retryable);
        Assert.Equal([Video, Big], ledger.KilledRunFiles.Select(k => k.File));
    }

    [Fact]
    public void A_cancel_after_files_failed_shows_the_summary_and_needs_attention()
    {
        var job = Canceled() with { MayBeIncomplete = 1, RetryCount = 1 };

        Assert.True(job.OutcomeNeedsUser);
        Assert.True(job.NeedsAttention);
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(job));
        Assert.Equal(StateTone.Attention, JobStateText.Tone(job));
        Assert.Equal("Canceled; 1 file may be incomplete", JobStateText.For(job));
        Assert.Equal("Try again (1)", JobStateText.TryAgainLabel(job));
        Assert.EndsWith("Files at the destination may be incomplete.", JobStateText.SummaryHeading(job));
        Assert.StartsWith("1 file was not finished", JobStateText.MayBeIncompleteText(job));

        var toast = ToastText.ForFinished(job, notifyOnComplete: false);
        Assert.NotNull(toast);
        Assert.Contains("may be incomplete", toast.Body);
    }

    [Fact]
    public void A_cancel_after_errors_alone_still_reports_them()
    {
        var job = Canceled() with { ErrorCount = 2 };

        Assert.True(job.NeedsAttention);
        Assert.Equal(ProgressWindowAction.ShowSummary, ProgressWindowPolicy.OnTerminal(job));
        Assert.Equal("Canceled; 2 items had problems", JobStateText.For(job));
        Assert.NotNull(ToastText.ForFinished(job, notifyOnComplete: false));
    }

    [Fact]
    public void A_plain_cancel_still_closes_quietly()
    {
        var job = Canceled();

        Assert.False(job.NeedsAttention);
        Assert.Equal(ProgressWindowAction.Close, ProgressWindowPolicy.OnTerminal(job));
        Assert.Null(ToastText.ForFinished(job, notifyOnComplete: true));
        Assert.Null(JobStateText.TryAgainLabel(job));
        Assert.Equal("Canceled", JobStateText.For(job));
    }

    [Fact]
    public void An_ephemeral_cancel_toast_names_nothing()
    {
        var job = DisplayAndTrayTests.Job(JobState.Canceled, LoggingMode.Ephemeral) with { MayBeIncomplete = 3 };

        var toast = ToastText.ForFinished(job, notifyOnComplete: false)!;

        Assert.Equal(ToastText.EphemeralBody, toast.Body);
        Assert.DoesNotContain("secret", toast.Title + toast.Body, StringComparison.OrdinalIgnoreCase);
    }
}
