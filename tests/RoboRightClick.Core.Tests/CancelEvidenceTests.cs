using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// Cancel cleanup when a check cannot answer: "could not check" must never pass for "not
/// there", and a file robocopy created after the look at the kill is reported, never deleted.
/// </summary>
public class CancelEvidenceTests
{
    private static readonly FileFacts Facts = new(10, DateTimeOffset.UnixEpoch);

    private static KilledRunFile Killed(string name) =>
        new(new PlannedFile($@"\\nas\share\{name}", $@"D:\dst\{name}", Facts), ConflictPolicy.Ask);

    private static readonly FileIdentity Held = new(0xABCD, 0, 42, 7);

    private static CleanupPlan Select(
        KilledRunFile file,
        KillObservation seen,
        bool move,
        PathPresence destination,
        PathPresence source,
        Func<string, IReadOnlySet<string>?>? namesAfterExit = null) =>
        CancelCleanup.Select(
            [file],
            completedSources: [],
            presentBeforeStep: [],
            atKill: _ => seen,
            move,
            destinationPresence: _ => destination,
            sourcePresence: _ => source,
            claimedByOtherJob: _ => false,
            namesAfterExit: namesAfterExit);

    [Fact]
    public void A_cut_whose_source_cannot_be_checked_is_reported_and_not_deleted()
    {
        // The share dropped: the source cannot be stat'ed. That is not "the move finished", and
        // since the source may in fact be gone, the destination is not deleted either.
        var file = Killed("big.iso");
        var plan = Select(file, new KillObservation(KillEvidence.OpenByRobocopy, Held), move: true, PathPresence.Present, PathPresence.Unknown);

        Assert.Empty(plan.Delete);
        Assert.Equal([file.File.DestinationPath], plan.LeftInPlace);
    }

    [Fact]
    public void A_cut_whose_source_is_gone_is_left_alone_as_finished()
    {
        var plan = Select(Killed("done.iso"), new KillObservation(KillEvidence.OpenByRobocopy, Held), move: true, PathPresence.Present, PathPresence.Absent);

        Assert.Empty(plan.Delete);
        Assert.Empty(plan.LeftInPlace);
    }

    [Fact]
    public void An_unproven_file_whose_destination_cannot_be_checked_is_reported()
    {
        var file = Killed("unknown.bin");
        var plan = Select(file, default, move: false, PathPresence.Unknown, PathPresence.Present);

        Assert.Equal([file.File.DestinationPath], plan.LeftInPlace);
        Assert.Empty(Select(file, default, move: false, PathPresence.Absent, PathPresence.Present).LeftInPlace);
    }

    [Fact]
    public void A_file_absent_at_the_kill_that_is_there_after_the_exit_is_reported_never_deleted()
    {
        // A thread was still inside its create call when the look ran; the file appeared after.
        var file = Killed("late-create.bin");
        var absent = new KillObservation(KillEvidence.Absent, default);

        var appeared = Select(file, absent, move: false, PathPresence.Present, PathPresence.Present,
            namesAfterExit: _ => new HashSet<string>(["LATE-CREATE.BIN"], WinPath.Comparer));
        var stillAbsent = Select(file, absent, move: false, PathPresence.Present, PathPresence.Present,
            namesAfterExit: _ => new HashSet<string>(WinPath.Comparer));
        var unlisted = Select(file, absent, move: false, PathPresence.Present, PathPresence.Present,
            namesAfterExit: _ => null);

        Assert.Empty(appeared.Delete);
        Assert.Equal([file.File.DestinationPath], appeared.LeftInPlace);
        Assert.Empty(stillAbsent.LeftInPlace);
        Assert.Equal([file.File.DestinationPath], unlisted.LeftInPlace);
    }

    [Fact]
    public void The_second_look_lists_each_folder_once()
    {
        var killed = Enumerable.Range(0, 50).Select(i => Killed($@"sub{i % 2}\f{i}.bin")).ToList();
        var listings = new List<string>();

        var plan = CancelCleanup.Select(
            killed, [], [], _ => new KillObservation(KillEvidence.Absent, default), move: false,
            destinationPresence: _ => PathPresence.Present,
            sourcePresence: _ => PathPresence.Present,
            claimedByOtherJob: _ => false,
            namesAfterExit: folder =>
            {
                listings.Add(folder);
                return new HashSet<string>(WinPath.Comparer);
            });

        Assert.Empty(plan.LeftInPlace);
        Assert.Equal([@"D:\dst\sub0", @"D:\dst\sub1"], listings.Order(StringComparer.Ordinal));
    }
}
