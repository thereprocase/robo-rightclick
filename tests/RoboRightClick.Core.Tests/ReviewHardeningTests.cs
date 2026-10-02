using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

// Rules added after the architecture review. Each safety rule here was broken on purpose
// once and its test seen failing (CLAUDE.md, verification honesty).

public class PathPolicyTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\a b\日本語 📁.txt")]
    [InlineData(@"d:\dst\")]
    [InlineData(@"\\srv\share")]
    [InlineData(@"\\srv\share\dir\file.txt")]
    [InlineData(@"C:\.gitignore")]
    [InlineData(@"C:\CONFIG.SYS")]
    [InlineData(@"C:\console\nul-ish.txt")]
    public void Plain_drive_and_network_paths_are_accepted(string path) =>
        Assert.Null(PathPolicy.Problem(path));

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\x")]
    [InlineData(@"C:relative")]
    [InlineData(@"\rooted\no\drive")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\?\UNC\srv\share")]
    [InlineData(@"\\?\GLOBALROOT\Device\x")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"\\srv")]
    [InlineData(@"\\srv\")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\.\b")]
    [InlineData(@"C:\a\\b")]
    [InlineData(@"C:\a\file.txt:stream")]
    [InlineData(@"C:\a\*")]
    [InlineData(@"C:\a\?.txt")]
    [InlineData(@"C:\a""b")]
    [InlineData(@"C:\x"" ""D:\y"" /MIR """)]
    [InlineData(@"C:\a<b")]
    [InlineData(@"C:\a|b")]
    [InlineData(@"C:/a/b")]
    [InlineData("C:\\a\tb")]
    [InlineData(@"C:\NUL")]
    [InlineData(@"C:\a\con.txt")]
    [InlineData(@"C:\a\COM1")]
    [InlineData(@"C:\a\lpt9.log")]
    [InlineData(@"C:\a\NUL .txt")]
    [InlineData(@"C:\a\trailing.")]
    [InlineData(@"C:\a\trailing ")]
    public void Anything_robocopy_could_misread_is_refused(string path) =>
        Assert.NotNull(PathPolicy.Problem(path));

    [Fact]
    public void Overlong_paths_are_refused()
    {
        var path = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 200), 170));
        Assert.True(path.Length > PathPolicy.MaxPathLength);
        Assert.NotNull(PathPolicy.Problem(path));
    }

    [Fact]
    public void Problems_never_repeat_the_path()
    {
        const string secret = @"C:\secret-folder\x*y";
        Assert.DoesNotContain("secret", PathPolicy.Problem(secret)!, StringComparison.OrdinalIgnoreCase);
    }
}

public class PasteOrderTests
{
    /// <summary>A small file system: kinds by path, plus aliases that FinalPath resolves.</summary>
    private sealed class Disk : IPlanningFacts
    {
        public Dictionary<string, ItemKind> Kinds { get; } = new(WinPath.Comparer);

        public Dictionary<string, string> Aliases { get; } = new(WinPath.Comparer);

        public bool Exists(string path) => Kinds.ContainsKey(WinPath.TrimTrailingSeparators(path));

        public bool SameVolume(string a, string b) => WinPath.Comparer.Equals(WinPath.GetRoot(a), WinPath.GetRoot(b));

        public ItemKind KindOf(string path) =>
            Kinds.TryGetValue(WinPath.TrimTrailingSeparators(path), out var kind) ? kind : ItemKind.Missing;

        public string FinalPath(string path)
        {
            var p = WinPath.TrimTrailingSeparators(path);
            foreach (var (alias, target) in Aliases)
            {
                if (WinPath.AreSame(p, alias))
                {
                    return target;
                }
                if (WinPath.IsStrictlyUnder(p, alias))
                {
                    return target + p[alias.Length..];
                }
            }
            return p;
        }
    }

    private static Disk WithFolders(params string[] folders)
    {
        var disk = new Disk();
        foreach (var f in folders)
        {
            disk.Kinds[f] = ItemKind.Directory;
        }
        return disk;
    }

    [Fact]
    public void Unsafe_and_missing_sources_are_refused_and_the_rest_still_plan()
    {
        var disk = WithFolders(@"D:\dst", @"C:\src");
        disk.Kinds[@"C:\src\ok.txt"] = ItemKind.File;

        var plan = PastePlanner.Plan(
            new PasteOrder([@"C:\src\ok.txt", @"C:\src\*", @"C:\src\gone.txt"], @"D:\dst", TransferVerb.Copy),
            disk);

        var step = Assert.IsType<RobocopyStep>(Assert.Single(plan.Steps));
        Assert.Equal(["ok.txt"], step.FileNames);
        Assert.Equal(
            [PathPolicy.UnsupportedPathReason, PastePlanner.MissingReason],
            plan.Rejected.Select(r => r.Reason));
    }

    [Fact]
    public void A_missing_or_unsafe_destination_refuses_everything()
    {
        var disk = WithFolders(@"C:\src");
        disk.Kinds[@"C:\src\a.txt"] = ItemKind.File;

        var missing = PastePlanner.Plan(new PasteOrder([@"C:\src\a.txt"], @"D:\nowhere", TransferVerb.Copy), disk);
        Assert.Empty(missing.Steps);
        Assert.Equal(PastePlanner.DestinationReason, Assert.Single(missing.Rejected).Reason);

        var device = PastePlanner.Plan(new PasteOrder([@"C:\src\a.txt"], @"\\.\pipe\x", TransferVerb.Copy), disk);
        Assert.Empty(device.Steps);
        Assert.Equal(PathPolicy.UnsupportedPathReason, Assert.Single(device.Rejected).Reason);
    }

    [Fact]
    public void A_destination_inside_the_source_through_a_junction_is_refused()
    {
        // D:\link is a junction to C:\src: pasting C:\src into D:\link\sub would copy the
        // tree into itself.
        var disk = WithFolders(@"C:\src", @"C:\src\sub", @"D:\link\sub");
        disk.Aliases[@"D:\link"] = @"C:\src";

        var plan = PastePlanner.Plan(new PasteOrder([@"C:\src"], @"D:\link\sub", TransferVerb.Copy), disk);

        Assert.Empty(plan.Steps);
        Assert.Equal(PastePlanner.SubfolderReason, Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void A_short_name_spelling_of_the_same_folder_is_treated_as_the_same_folder()
    {
        // Pasting a cut into the folder it came from, spelled with an 8.3 name, is a no-op;
        // planning it as a move would hand robocopy one folder as both source and destination.
        var disk = WithFolders(@"C:\Long Folder", @"C:\LONGFO~1");
        disk.Kinds[@"C:\Long Folder\a.txt"] = ItemKind.File;
        disk.Aliases[@"C:\LONGFO~1"] = @"C:\Long Folder";

        var plan = PastePlanner.Plan(new PasteOrder([@"C:\Long Folder\a.txt"], @"C:\LONGFO~1", TransferVerb.Move), disk);

        Assert.Empty(plan.Steps);
        Assert.Single(plan.NoOps);
    }

    [Fact]
    public void A_selected_folder_link_is_only_ever_renamed_never_given_to_robocopy()
    {
        var disk = WithFolders(@"C:\dst", @"D:\dst", @"C:\src");
        disk.Kinds[@"C:\src\link"] = ItemKind.DirectoryLink;

        var sameVolumeCut = PastePlanner.Plan(new PasteOrder([@"C:\src\link"], @"C:\dst", TransferVerb.Move), disk);
        Assert.Equal([new RenameStep(@"C:\src\link", @"C:\dst\link")], sameVolumeCut.Steps);

        foreach (var (dest, verb) in new[] { (@"D:\dst", TransferVerb.Move), (@"D:\dst", TransferVerb.Copy), (@"C:\dst", TransferVerb.Copy) })
        {
            var plan = PastePlanner.Plan(new PasteOrder([@"C:\src\link"], dest, verb), disk);
            Assert.Empty(plan.Steps);
            Assert.Equal(PastePlanner.LinkReason, Assert.Single(plan.Rejected).Reason);
        }
    }

    [Fact]
    public void A_folder_link_onto_an_existing_name_is_refused_not_merged()
    {
        var disk = WithFolders(@"C:\dst", @"C:\dst\link", @"C:\src");
        disk.Kinds[@"C:\src\link"] = ItemKind.DirectoryLink;

        var plan = PastePlanner.Plan(new PasteOrder([@"C:\src\link"], @"C:\dst", TransferVerb.Move), disk);

        Assert.DoesNotContain(plan.Steps, s => s is RobocopyStep);
        Assert.Equal(PastePlanner.LinkReason, Assert.Single(plan.Rejected).Reason);
    }
}

public class StateTableTests
{
    [Fact]
    public void Every_non_terminal_state_can_fail()
    {
        foreach (var state in Enum.GetValues<JobState>().Where(s => !JobStates.IsTerminal(s)))
        {
            Assert.True(JobStates.CanTransition(state, JobState.Failed), state.ToString());
        }
    }

    [Fact]
    public void Nothing_to_run_goes_from_scanning_to_finalizing_and_never_skips_it()
    {
        Assert.True(JobStates.CanTransition(JobState.Scanning, JobState.Finalizing));
        foreach (var terminal in new[] { JobState.Done, JobState.DoneWithErrors })
        {
            Assert.False(JobStates.CanTransition(JobState.Scanning, terminal));
        }
    }

    [Fact]
    public void Paused_must_resume_before_finalizing()
    {
        Assert.False(JobStates.CanTransition(JobState.Paused, JobState.Finalizing));
    }
}

public class ParserKillTests
{
    [Fact]
    public void A_held_file_line_is_dropped_when_robocopy_was_killed()
    {
        // The line for a file is held until the next line shows it did not fail. If the
        // process is killed in between, the file may be a preallocated partial copy.
        var parser = new RobocopyOutputParser();
        Assert.Empty(parser.Feed("\t  \t\t       5\tC:\\src\\partial.bin"));
        Assert.Empty(parser.Complete(processEndedNormally: false));
    }

    [Fact]
    public void A_held_file_line_is_reported_when_robocopy_ended_normally()
    {
        var parser = new RobocopyOutputParser();
        parser.Feed("\t  \t\t       5\tC:\\src\\done.bin");
        Assert.Equal(new FileReported(5, @"C:\src\done.bin"), Assert.Single(parser.Complete(processEndedNormally: true)));
    }
}

public class CancelClaimTests
{
    private static readonly FileFacts Facts = new(10, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_file_another_job_wrote_is_never_deleted_by_this_jobs_cancel()
    {
        // Jobs A and B both planned D:\dst\f.bin; B (a cut) finished it and its source is
        // gone. Canceling A must not delete the only copy.
        var plan = CancelCleanup.Select(
            [new PlannedFile(@"C:\a\f.bin", @"D:\dst\f.bin", Facts), new PlannedFile(@"C:\a\g.bin", @"D:\dst\g.bin", Facts)],
            completedSources: [],
            presentBeforeStep: [],
            move: false,
            sourceStillExists: _ => true,
            claimedByOtherJob: p => p.EndsWith("f.bin", StringComparison.Ordinal));

        Assert.Equal([@"D:\dst\g.bin"], plan.Delete);
    }

    [Fact]
    public void Completed_and_present_paths_match_robocopys_spelling()
    {
        // A root source is passed to robocopy as "C:\." and may come back as "C:\.\name".
        var plan = CancelCleanup.Select(
            [new PlannedFile(@"C:\f.bin", @"D:\dst\f.bin", Facts)],
            completedSources: [@"C:\.\f.bin"],
            presentBeforeStep: [],
            move: false,
            sourceStillExists: _ => true,
            claimedByOtherJob: _ => false);

        Assert.Empty(plan.Delete);
    }
}

public class QueuePolicyTests
{
    private static JobFootprint Copy(string dest, params string[] sources) => new(dest, sources, Move: false);

    private static JobFootprint Cut(string dest, params string[] sources) => new(dest, sources, Move: true);

    [Fact]
    public void Pastes_into_overlapping_destinations_wait()
    {
        Assert.True(JobQueuePolicy.Conflicts(Copy(@"E:\Archive", @"F:\Photos"), Cut(@"E:\Archive", @"D:\Photos")));
        Assert.True(JobQueuePolicy.Conflicts(Copy(@"E:\Archive\2026", @"F:\x"), Copy(@"E:\Archive", @"G:\y")));
    }

    [Fact]
    public void A_cut_conflicts_with_anything_reading_or_writing_its_sources()
    {
        Assert.True(JobQueuePolicy.Conflicts(Cut(@"E:\x", @"D:\Photos"), Copy(@"F:\y", @"D:\Photos\2026")));
        Assert.True(JobQueuePolicy.Conflicts(Cut(@"E:\x", @"D:\Photos"), Cut(@"F:\y", @"D:\Photos")));
        Assert.True(JobQueuePolicy.Conflicts(Copy(@"D:\Photos\in", @"F:\y"), Cut(@"E:\x", @"D:\Photos")));
    }

    [Fact]
    public void Copies_that_only_read_the_same_sources_run_together()
    {
        Assert.False(JobQueuePolicy.Conflicts(Copy(@"E:\a", @"D:\Photos"), Copy(@"F:\b", @"D:\Photos")));
        Assert.False(JobQueuePolicy.Conflicts(Copy(@"E:\a", @"D:\x"), Cut(@"F:\b", @"G:\y")));
    }

    [Fact]
    public void A_copy_writing_into_another_jobs_source_waits()
    {
        Assert.True(JobQueuePolicy.Conflicts(Copy(@"D:\Photos\new", @"F:\x"), Copy(@"E:\backup", @"D:\Photos")));
    }

    [Fact]
    public void Wait_reasons_in_priority_order()
    {
        var candidate = Copy(@"E:\a", @"D:\x");
        Assert.Equal(JobWait.OverlappingJob, JobQueuePolicy.WaitReason(candidate, [Copy(@"E:\a", @"G:\y")], 0, 0, 0));
        Assert.Equal(JobWait.ConcurrencyLimit, JobQueuePolicy.WaitReason(candidate, [], 2, 2, 0));
        Assert.Equal(JobWait.ScanLimit, JobQueuePolicy.WaitReason(candidate, [], 9, 0, JobQueuePolicy.MaxConcurrentScans));
        Assert.Equal(JobWait.None, JobQueuePolicy.WaitReason(candidate, [Copy(@"F:\b", @"D:\x")], 1, 0, 0));
    }
}

public class PrivacyTests
{
    [Theory]
    [InlineData(LoggingMode.Ephemeral, LoggingMode.Normal, LoggingMode.Ephemeral)]
    [InlineData(LoggingMode.Normal, LoggingMode.Ephemeral, LoggingMode.Ephemeral)]
    [InlineData(LoggingMode.Ephemeral, LoggingMode.Ephemeral, LoggingMode.Ephemeral)]
    [InlineData(LoggingMode.Normal, LoggingMode.Normal, LoggingMode.Normal)]
    public void A_derived_job_is_ephemeral_if_either_side_is(LoggingMode parent, LoggingMode current, LoggingMode expected) =>
        Assert.Equal(expected, JobSinks.ForDerivedJob(parent, current));
}

public class SnapshotAndToastTests
{
    private static JobSnapshot Job(JobState state, LoggingMode mode = LoggingMode.Normal) =>
        DisplayAndTrayTests.Job(state, mode);

    [Fact]
    public void A_cancel_that_damaged_replaced_files_needs_attention_and_toasts()
    {
        var damaged = Job(JobState.Canceled) with { DamagedOnCancel = 2 };
        Assert.True(damaged.NeedsAttention);
        Assert.False((damaged with { Acknowledged = true }).NeedsAttention);
        Assert.False(Job(JobState.Canceled).NeedsAttention);

        var toast = ToastText.ForFinished(damaged, notifyOnComplete: false);
        Assert.NotNull(toast);
        Assert.Contains("2 files were being replaced", toast.Body);
        Assert.Null(ToastText.ForFinished(Job(JobState.Canceled), notifyOnComplete: true));
    }

    [Fact]
    public void Refusals_only_show_the_reason_and_do_not_offer_try_again()
    {
        var job = Job(JobState.DoneWithErrors) with { ErrorCount = 0, RefusedCount = 1, RefusalReason = PastePlanner.SubfolderReason };
        var toast = ToastText.ForFinished(job, notifyOnComplete: true)!;
        Assert.Contains(PastePlanner.SubfolderReason, toast.Body);
        Assert.DoesNotContain("try again", toast.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Done_counts_top_level_items_and_a_no_op_is_silent()
    {
        var done = Job(JobState.Done) with { DoneFiles = 0 };
        Assert.StartsWith("2 items", ToastText.ForFinished(done, notifyOnComplete: true)!.Body);
        Assert.Null(ToastText.ForFinished(done with { NoOp = true }, notifyOnComplete: true));
    }

    [Fact]
    public void A_failure_reason_is_shown_in_normal_mode()
    {
        var failed = Job(JobState.Failed) with { FailureReason = "Access to the destination folder was denied (error 5)." };
        Assert.Contains("denied (error 5)", ToastText.ForFinished(failed, notifyOnComplete: true)!.Body);
    }

    [Theory]
    [InlineData(JobState.Canceled)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.DoneWithErrors)]
    public void Every_ephemeral_toast_is_generic(JobState state)
    {
        var job = Job(state, LoggingMode.Ephemeral) with
        {
            DamagedOnCancel = 3,
            RefusedCount = 1,
            RefusalReason = PastePlanner.SubfolderReason,
            FailureReason = "Access to the destination folder was denied (error 5).",
        };
        var toast = ToastText.ForFinished(job, notifyOnComplete: true)!;
        Assert.Equal(ToastText.EphemeralBody, toast.Body);
        Assert.DoesNotContain("secret", toast.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refusal_toasts_are_path_free_and_defined_for_every_reason()
    {
        foreach (var refusal in Enum.GetValues<VerbRefusal>())
        {
            var toast = ToastText.ForRefusal(refusal);
            Assert.False(string.IsNullOrWhiteSpace(toast.Body));
            Assert.DoesNotContain(@":\", toast.Title + toast.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Settings_toast_names_the_first_problem()
    {
        var toast = ToastText.ForSettingsProblems(["'threads' must be an integer from 1 to 128; using 32", "x", "y"]);
        Assert.Contains("'threads' must be", toast.Body);
        Assert.Contains("and 2 more", toast.Body);
    }

    [Fact]
    public void A_job_being_canceled_adds_no_speed_to_the_tooltip()
    {
        var canceling = Job(JobState.Running) with { BytesPerSecond = 1_000_000, Remaining = TimeSpan.FromMinutes(9), CancelRequested = true };
        Assert.DoesNotContain("/s", TrayStatus.Derive([canceling], LoggingMode.Normal).Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Resetting_the_rate_forgets_samples_from_before_a_pause()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var progress = new JobProgress(1000, 1);
        progress.SetObservedBytes(100, t0);
        progress.SetObservedBytes(200, t0.AddSeconds(1));
        Assert.NotNull(progress.BytesPerSecond);
        progress.ResetRate();
        Assert.Null(progress.BytesPerSecond);
    }
}

public class InstallFootprintTests
{
    private const string Exe = @"C:\Users\u\AppData\Local\Programs\RoboRightClick\RoboRightClick.exe";
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static IReadOnlyList<RegistryValue> Values() =>
        Registration.InstallValues(new InstallTarget(Exe, true, Sid, "1.0.0-beta.1"));

    [Fact]
    public void The_app_id_restricts_launch_and_calls_to_the_user_at_medium_integrity()
    {
        var access = Values().Single(v => v.Key == Registration.AppIdKey && v.Name == "AccessPermission");
        var launch = Values().Single(v => v.Key == Registration.AppIdKey && v.Name == "LaunchPermission");
        Assert.Equal(RegistryDataKind.SecurityDescriptor, access.Kind);
        Assert.Equal($"O:{Sid}G:{Sid}D:(A;;0x3;;;{Sid})(A;;0x3;;;SY)S:(ML;;NX;;;ME)", access.Data);
        Assert.Equal($"O:{Sid}G:{Sid}D:(A;;0xb;;;{Sid})(A;;0xb;;;SY)S:(ML;;NX;;;ME)", launch.Data);
    }

    [Theory]
    [InlineData("S-1-5-21-1)(A;;0x3;;;WD")]
    [InlineData("WD")]
    [InlineData("")]
    public void Only_a_canonical_sid_can_reach_the_sddl(string sid) =>
        Assert.Throws<ArgumentException>(() => ComSecurity.AccessPermissionSddl(sid));

    [Fact]
    public void Installed_apps_entry_uninstalls_through_the_exe()
    {
        var values = Values().Where(v => v.Key == Registration.UninstallKey).ToList();
        Assert.Contains(new RegistryValue(Registration.UninstallKey, "UninstallString", "\"" + Exe + "\" --uninstall"), values);
        Assert.Contains(new RegistryValue(Registration.UninstallKey, "DisplayVersion", "1.0.0-beta.1"), values);
        Assert.Equal(1, Registration.DWordValue(values.Single(v => v.Name == "NoModify")));
        Assert.Contains(new RemoveKeyTree(Registration.UninstallKey), Registration.UninstallRemovals());
    }

    [Fact]
    public void Paste_is_offered_for_a_single_folder_only()
    {
        Assert.Equal("Single", ShellVerbs.RoboPaste.MultiSelectModel);
        Assert.Equal("Player", ShellVerbs.RoboCopy.MultiSelectModel);
        Assert.Equal("Player", ShellVerbs.RoboCut.MultiSelectModel);
    }

    [Fact]
    public void A_reinstall_keeps_the_users_autostart_choice_unless_told_otherwise()
    {
        var off = Settings.Default with { StartWithWindows = false };
        Assert.False(Registration.ResolveStartWithWindows(null, off));
        Assert.True(Registration.ResolveStartWithWindows(true, off));
        Assert.False(Registration.ResolveStartWithWindows(false, Settings.Default));
        Assert.True(Registration.ResolveStartWithWindows(null, null));
    }
}

public class NormalizeForMatchTests
{
    [Theory]
    [InlineData(@"C:\.\f.bin", @"C:\f.bin")]
    [InlineData(@"C:\a\.\b\.\c", @"C:\a\b\c")]
    [InlineData(@"\\?\C:\long\path", @"C:\long\path")]
    [InlineData(@"\\?\UNC\srv\share\x", @"\\srv\share\x")]
    [InlineData(@"C:\.", @"C:\")]
    [InlineData(@"D:\dst\", @"D:\dst")]
    public void Robocopy_spellings_normalize_to_the_planned_form(string printed, string expected) =>
        Assert.Equal(expected, WinPath.NormalizeForMatch(printed));
}
