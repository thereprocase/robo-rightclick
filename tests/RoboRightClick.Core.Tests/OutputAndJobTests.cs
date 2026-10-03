using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class RobocopyOutputParserTests
{
    // Edge cases around the line shapes seen in the captured fixtures
    // (CapturedOutputTests); these cover orderings the captures did not hit.
    [Fact]
    public void A_file_line_is_held_until_the_next_line_shows_it_did_not_fail()
    {
        var parser = new RobocopyOutputParser();
        Assert.Empty(parser.Feed("\uFEFF\t  \t\t1048576\tC:\\src\\日本語 📁\\a.bin\r\n"));
        var e = Assert.Single(parser.Feed("\t  \t\t5\tC:\\src\\b.txt"));
        Assert.Equal(new FileReported(1_048_576, @"C:\src\日本語 📁\a.bin"), e);
        Assert.Equal(new FileReported(5, @"C:\src\b.txt"), Assert.Single(parser.Complete()));
    }

    [Fact]
    public void An_error_for_a_different_path_does_not_cancel_the_held_file()
    {
        var parser = new RobocopyOutputParser();
        parser.Feed("\t  \t\t5\tC:\\src\\ok.txt");
        var events = parser.Feed(@"2026/10/02 12:34:56 ERROR 5 (0x00000005) Copying File C:\src\other.txt");
        Assert.Equal(new FileReported(5, @"C:\src\ok.txt"), Assert.Single(events));
    }

    [Fact]
    public void Error_line_and_its_message_become_one_event()
    {
        var parser = new RobocopyOutputParser();
        Assert.Empty(parser.Feed(@"2026/10/02 12:34:56 ERROR 5 (0x00000005) Copying File C:\src\locked.db"));
        var e = Assert.Single(parser.Feed("Access is denied."));
        Assert.Equal(new ErrorReported(5, "Copying File", @"C:\src\locked.db", "Access is denied."), e);
    }

    [Fact]
    public void Error_without_message_is_flushed_by_the_next_event_line()
    {
        var parser = new RobocopyOutputParser();
        parser.Feed(@"2026/10/02 12:34:56 ERROR 32 (0x00000020) Copying File \\srv\share\x.txt");
        var events = parser.Feed("\t\t5\tC:\\src\\y.txt");

        Assert.Equal(new ErrorReported(32, "Copying File", @"\\srv\share\x.txt", ""), Assert.Single(events));
        Assert.Equal(new FileReported(5, @"C:\src\y.txt"), Assert.Single(parser.Complete()));
    }

    /// <summary>
    /// robocopy.exe prints its words from .mui resources. The held file line of a file that
    /// failed must be dropped on a localized Windows too, or the ledger counts a full-length
    /// partial file as copied. The sample lines follow the English shape with the words
    /// translated (LIKELY; no localized capture yet).
    /// </summary>
    [Theory]
    [InlineData(@"2026/10/02 12:34:56 FEHLER 112 (0x00000070) Datei wird kopiert \\nas\share\big.iso", 112, "Datei wird kopiert", "Auf dem Datenträger ist nicht genügend Speicherplatz vorhanden.")]
    [InlineData(@"2026/10/02 12:34:56 ERREUR 5 (0x00000005) Copie du fichier \\nas\share\big.iso", 5, "Copie du fichier", "Accès refusé.")]
    [InlineData(@"2026/10/02 12:34:56 ERROR 53 (0x00000035) Copying File \\nas\share\big.iso", 53, "Copying File", "The network path was not found.")]
    public void A_localized_error_line_still_cancels_the_held_file_line(string errorLine, int code, string operation, string message)
    {
        var parser = new RobocopyOutputParser();
        Assert.Empty(parser.Feed("\t\t1073741824\t\\\\nas\\share\\big.iso"));

        Assert.Empty(parser.Feed(errorLine));
        var events = parser.Feed(message);

        var error = Assert.IsType<ErrorReported>(Assert.Single(events));
        Assert.Equal(new ErrorReported(code, operation, @"\\nas\share\big.iso", message), error);
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void A_line_without_the_error_shape_is_not_an_error()
    {
        var parser = new RobocopyOutputParser();
        Assert.IsType<OtherOutput>(Assert.Single(parser.Feed("FEHLER: WIEDERHOLUNGSLIMIT ÜBERSCHRITTEN.")));
        Assert.IsType<OtherOutput>(Assert.Single(parser.Feed(@"2026/10/02 12:34:56 FEHLER Datei wird kopiert C:\src\a.txt")));
    }

    [Fact]
    public void An_error_code_too_large_for_an_int_is_reported_as_unknown_instead_of_throwing()
    {
        var parser = new RobocopyOutputParser();
        parser.Feed(@"2026/10/02 12:34:56 ERROR 99999999999 (0x00000005) Copying File C:\src\a.txt");
        var e = Assert.IsType<ErrorReported>(Assert.Single(parser.Complete()));
        Assert.Equal(0, e.Code);
        Assert.Equal(@"C:\src\a.txt", e.Path);
    }

    [Fact]
    public void Complete_flushes_a_trailing_error()
    {
        var parser = new RobocopyOutputParser();
        parser.Feed(@"2026/10/02 12:34:56 ERROR 112 (0x00000070) Copying File D:\dst\big.iso");
        Assert.Equal(112, Assert.IsType<ErrorReported>(Assert.Single(parser.Complete())).Code);
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void Blank_lines_are_ignored_and_other_text_is_kept()
    {
        var parser = new RobocopyOutputParser();
        Assert.Empty(parser.Feed("   "));
        Assert.IsType<OtherOutput>(Assert.Single(parser.Feed("Waiting 0 seconds...")));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(16, true)]
    [InlineData(-1, true)]
    public void Exit_code_failure_threshold(int code, bool failure) =>
        Assert.Equal(failure, new RobocopyExitCode(code).IsFailure);

    [Fact]
    public void Exit_code_bits_decode()
    {
        var c = new RobocopyExitCode(1 | 2 | 8);
        Assert.True(c.FilesCopied);
        Assert.True(c.ExtraFilesAtDestination);
        Assert.False(c.MismatchesDetected);
        Assert.True(c.SomeCopiesFailed);
        Assert.False(c.FatalError);
    }
}

public class JobLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Happy_path_records_every_state_with_its_time()
    {
        var job = new JobLifecycle(T0);
        job.MoveTo(JobState.Scanning, T0.AddSeconds(1));
        job.MoveTo(JobState.Running, T0.AddSeconds(2));
        job.MoveTo(JobState.Paused, T0.AddSeconds(3));
        job.MoveTo(JobState.Running, T0.AddSeconds(4));
        job.MoveTo(JobState.Finalizing, T0.AddSeconds(5));
        job.MoveTo(JobState.Done, T0.AddSeconds(6));

        Assert.Equal(
            [JobState.Queued, JobState.Scanning, JobState.Running, JobState.Paused, JobState.Running, JobState.Finalizing, JobState.Done],
            job.History.Select(h => h.State));
        Assert.Equal(T0.AddSeconds(6), job.History[^1].At);
    }

    [Fact]
    public void Conflicts_route_through_awaiting_decision()
    {
        var job = new JobLifecycle(T0);
        job.MoveTo(JobState.Scanning, T0);
        job.MoveTo(JobState.AwaitingDecision, T0);
        Assert.False(job.TryMoveTo(JobState.Paused, T0));
        job.MoveTo(JobState.Running, T0);
    }

    [Fact]
    public void Terminal_states_are_final()
    {
        foreach (var terminal in new[] { JobState.Done, JobState.DoneWithErrors, JobState.Failed, JobState.Canceled })
        {
            Assert.True(JobStates.IsTerminal(terminal));
            foreach (var any in Enum.GetValues<JobState>())
            {
                Assert.False(JobStates.CanTransition(terminal, any));
            }
        }
    }

    [Fact]
    public void Finalizing_cannot_be_canceled()
    {
        // A cut's cleanup is all-or-nothing from the user's point of view;
        // once the copy has finished, cancel no longer applies.
        Assert.False(JobStates.CanTransition(JobState.Finalizing, JobState.Canceled));
    }

    [Fact]
    public void Illegal_move_throws()
    {
        var job = new JobLifecycle(T0);
        Assert.Throws<InvalidOperationException>(() => job.MoveTo(JobState.Done, T0));
    }

    [Fact]
    public void Progress_speed_and_eta_follow_recent_samples()
    {
        var p = new JobProgress(totalBytes: 1_000, totalFiles: 10);
        Assert.Null(p.BytesPerSecond);

        p.FileCompleted(100, T0);
        p.FileCompleted(100, T0.AddSeconds(1));
        p.SetObservedBytes(250, T0.AddSeconds(1.5));

        Assert.Equal(250, p.DoneBytes);
        Assert.Equal(2, p.CompletedFiles);
        Assert.Equal(100.0, p.BytesPerSecond!.Value, precision: 6);
        Assert.Equal(TimeSpan.FromSeconds(7.5), p.EstimatedRemaining);
    }

    [Fact]
    public void Eta_is_null_rather_than_overflowing_when_the_rate_is_tiny()
    {
        var p = new JobProgress(totalBytes: long.MaxValue, totalFiles: 1);
        p.SetObservedBytes(0, T0);
        p.SetObservedBytes(1, T0.AddSeconds(10));
        Assert.NotNull(p.BytesPerSecond);
        Assert.Null(p.EstimatedRemaining);
    }

    [Theory]
    [InlineData(1_000L, 0.0)]
    [InlineData(1_000L, -5.0)]
    [InlineData(1_000L, double.NaN)]
    [InlineData(long.MaxValue, 1e-300)]
    public void Estimate_is_null_without_a_usable_rate_or_beyond_a_year(long remaining, double rate) =>
        Assert.Null(JobProgress.Estimate(remaining, rate));

    [Fact]
    public void Estimate_never_goes_negative() =>
        Assert.Equal(TimeSpan.Zero, JobProgress.Estimate(-10, 5));

    [Fact]
    public void Done_bytes_never_exceed_total()
    {
        var p = new JobProgress(totalBytes: 10, totalFiles: 1);
        p.FileCompleted(10, T0);
        p.SetObservedBytes(25, T0.AddSeconds(1));
        Assert.Equal(10, p.DoneBytes);
    }

    [Fact]
    public void Observed_bytes_never_move_backwards()
    {
        var p = new JobProgress(totalBytes: 100, totalFiles: 1);
        p.SetObservedBytes(60, T0);
        p.SetObservedBytes(40, T0.AddSeconds(1));
        Assert.Equal(60, p.DoneBytes);
    }
}

public class ConflictAndLoggingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_existing_destination_is_a_conflict_even_if_identical()
    {
        var facts = new FileFacts(10, T0);
        var files = new[]
        {
            new PlannedFile(@"C:\s\a", @"D:\d\a", facts),
            new PlannedFile(@"C:\s\b", @"D:\d\b", facts),
        };

        var conflicts = ConflictScan.Find(files, p => p == @"D:\d\a" ? facts : null);

        var c = Assert.Single(conflicts);
        Assert.True(c.LooksIdentical);
        Assert.False(c.SourceIsNewer);
    }

    [Fact]
    public void Resolve_ask_needs_a_choice_only_when_conflicts_exist()
    {
        Assert.Equal(ConflictPolicy.Ask, ConflictScan.Resolve(ConflictPolicy.Ask, 0, null));
        Assert.Equal(ConflictPolicy.Skip, ConflictScan.Resolve(ConflictPolicy.Ask, 3, ConflictPolicy.Skip));
        Assert.Throws<InvalidOperationException>(() => ConflictScan.Resolve(ConflictPolicy.Ask, 3, null));
        Assert.Equal(ConflictPolicy.Replace, ConflictScan.Resolve(ConflictPolicy.Replace, 3, ConflictPolicy.Skip));
    }

    [Fact]
    public void Ephemeral_mode_never_constructs_the_file_sink()
    {
        var constructed = false;
        var sink = JobSinks.For(LoggingMode.Ephemeral, () =>
        {
            constructed = true;
            throw new InvalidOperationException("must not be called");
        });

        Assert.Same(NullJobSink.Instance, sink);
        Assert.False(constructed);
    }

    [Fact]
    public void Normal_mode_uses_the_file_sink()
    {
        var marker = new RecordingSink();
        Assert.Same(marker, JobSinks.For(LoggingMode.Normal, () => marker));
    }

    [Fact]
    public void Job_folder_names_sort_by_time_and_round_trip()
    {
        var id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        var name = JobLogNames.FolderName(T0, id);
        Assert.Equal("20261002-120000-1a2b3c4d", name);
        Assert.True(JobLogNames.IsJobFolderName(name));
    }

    [Fact]
    public void Pruning_keeps_the_newest_and_ignores_foreign_folders()
    {
        var names = new[]
        {
            "20261001-090000-aaaaaaaa",
            "20261002-090000-bbbbbbbb",
            "20260930-090000-cccccccc",
            "my notes",
            "20261003-090000-NOTHEX!!",
        };

        Assert.Equal(["20261001-090000-aaaaaaaa", "20260930-090000-cccccccc"], JobLogNames.SelectForPruning(names, keep: 1));
        Assert.Empty(JobLogNames.SelectForPruning(names, keep: 10));
    }

    [Fact]
    public void Pruning_never_selects_a_running_jobs_folder_and_does_not_count_it()
    {
        var names = new[] { "20260930-090000-cccccccc", "20261001-090000-aaaaaaaa", "20261002-090000-bbbbbbbb" };
        var active = new HashSet<string> { "20260930-090000-cccccccc" };

        // The oldest folder belongs to a job still running: it stays, and keep=1 still
        // keeps one finished job besides it.
        Assert.Equal(["20261001-090000-aaaaaaaa"], JobLogNames.SelectForPruning(names, keep: 1, active));
        Assert.Equal(["20261002-090000-bbbbbbbb", "20261001-090000-aaaaaaaa"], JobLogNames.SelectForPruning(names, keep: 0, active));
    }

    private sealed class RecordingSink : IJobSink
    {
        public void JobCreated(JobDescription job) { }
        public void StateChanged(Guid jobId, StateChange change) { }
        public void CommandStarted(Guid jobId, string arguments) { }
        public void OutputLine(Guid jobId, string line) { }
        public void CommandFinished(Guid jobId, int exitCode) { }
        public void JobFinished(JobSummary summary) { }
    }
}

public class DuplicateNamerTests
{
    [Fact]
    public void Folder_copy_names_have_no_extension_split()
    {
        Assert.Equal("v1.2 - Copy", DuplicateNamer.CopyName("v1.2", isDirectory: true, _ => false));
        Assert.Equal("v1 - Copy.2", DuplicateNamer.CopyName("v1.2", isDirectory: false, _ => false));
    }

    [Fact]
    public void Keep_both_counts_up_from_two()
    {
        var taken = new HashSet<string> { "a (2).txt" };
        Assert.Equal("a (3).txt", DuplicateNamer.KeepBothName("a.txt", taken.Contains));
    }
}
