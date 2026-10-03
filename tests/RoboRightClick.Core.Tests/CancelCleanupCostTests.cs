using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// Cancel cleanup's cost and the memory a cancel and a finished job keep: evidence first, so
/// absent and not-open files cost nothing more; absent paths and finished claims as hashes.
/// </summary>
public class CancelCleanupCostTests
{
    private static readonly FileFacts Facts = new(10, DateTimeOffset.UnixEpoch);

    private static KilledRunFile Killed(int i) => new(new PlannedFile($@"C:\src\f{i}.bin", $@"D:\dst\f{i}.bin", Facts), ConflictPolicy.Ask);

    [Fact]
    public void Absent_and_not_open_evidence_never_asks_the_other_jobs_or_the_source_volume()
    {
        var killed = Enumerable.Range(0, 1_000).Select(Killed).ToList();
        var atKill = new KillObservations();
        for (var i = 0; i < killed.Count; i++)
        {
            atKill.Record(
                killed[i].File.DestinationPath,
                i % 2 == 0 ? new KillObservation(KillEvidence.Absent, default) : new KillObservation(KillEvidence.NotOpenByRobocopy, new FileIdentity(1, 0, (ulong)i + 1, 1)));
        }
        var claimedAsked = 0;
        var sourceAsked = 0;
        var destinationAsked = 0;

        var plan = CancelCleanup.Select(
            killed,
            completedSources: [],
            presentBeforeStep: [],
            atKill.Of,
            move: true,
            destinationExists: _ => { destinationAsked++; return true; },
            sourceStillExists: _ => { sourceAsked++; return true; },
            claimedByOtherJob: _ => { claimedAsked++; return false; });

        Assert.Empty(plan.Delete);
        Assert.Empty(plan.LeftInPlace);
        Assert.Equal(0, claimedAsked);
        Assert.Equal(0, sourceAsked);
        Assert.Equal(0, destinationAsked);
    }

    [Fact]
    public void Open_and_unknown_evidence_still_asks_both()
    {
        var killed = new[] { Killed(1), Killed(2) };
        var atKill = new KillObservations();
        atKill.Record(killed[0].File.DestinationPath, new KillObservation(KillEvidence.OpenByRobocopy, new FileIdentity(1, 0, 9, 1)));
        var claimedAsked = 0;
        var sourceAsked = 0;

        var plan = CancelCleanup.Select(
            killed, [], [], atKill.Of, move: true,
            destinationExists: _ => true,
            sourceStillExists: _ => { sourceAsked++; return true; },
            claimedByOtherJob: _ => { claimedAsked++; return false; });

        Assert.Equal(2, claimedAsked);
        Assert.Equal(2, sourceAsked);
        Assert.Equal([killed[0].File.DestinationPath], plan.Delete.Select(d => d.Path));
        Assert.Equal([killed[1].File.DestinationPath], plan.LeftInPlace);
    }

    [Fact]
    public void Kill_observations_keep_absent_paths_as_hashes_and_everything_else_whole()
    {
        var atKill = new KillObservations();
        var held = new KillObservation(KillEvidence.OpenByRobocopy, new FileIdentity(1, 0, 5, 1));
        atKill.Record(@"D:\dst\gone.bin", new KillObservation(KillEvidence.Absent, default));
        atKill.Record(@"D:\dst\held.bin", held);

        Assert.Equal(KillEvidence.Absent, atKill.Of(@"d:\DST\gone.bin").Evidence);
        Assert.Equal(held, atKill.Of(@"\\?\D:\dst\held.bin"));
        Assert.Equal(KillEvidence.Unknown, atKill.Of(@"D:\dst\never-looked.bin").Evidence);
        Assert.Equal(1, atKill.Count(KillEvidence.Absent));
        Assert.Equal(1, atKill.Count(KillEvidence.OpenByRobocopy));
        Assert.Equal(2, atKill.Total);

        atKill.Clear();
        Assert.Equal(KillEvidence.Unknown, atKill.Of(@"D:\dst\held.bin").Evidence);
        Assert.Equal(0, atKill.Total);
    }

    [Fact]
    public void A_finished_jobs_claims_shrink_to_hashes_and_still_answer()
    {
        var claims = new ClaimSet();
        for (var i = 0; i < 10_000; i++)
        {
            claims.AddFile($@"D:\dst\f{i}.bin");
        }
        claims.AddRoot(@"D:\dst\renamed");

        claims.Compact();

        Assert.True(claims.IsCompact);
        Assert.True(claims.Contains(@"d:\DST\f42.bin"));
        Assert.True(claims.Contains(@"D:\dst\f9999.bin"));
        Assert.True(claims.Contains(@"D:\dst\renamed\inside\x.txt"));
        Assert.False(claims.Contains(@"D:\dst\f10000.bin"));
        Assert.False(claims.Contains(@"D:\other\f42.bin"));

        // A job still adding after it compacted (it never does) would not lose the claim.
        claims.AddFile(@"D:\dst\late.bin");
        Assert.True(claims.Contains(@"D:\dst\late.bin"));
    }

    [Fact]
    public void Path_hashes_follow_the_path_comparer()
    {
        Assert.Equal(PathHash.Of(@"D:\DST\ÄBC.txt"), PathHash.Of(@"d:\dst\äbc.txt"));
        Assert.NotEqual(PathHash.Of(@"D:\dst\a.txt"), PathHash.Of(@"D:\dst\b.txt"));
    }
}
