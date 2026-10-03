using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void Prerelease_labels_order_as_semver_says()
    {
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0",
        ];
        for (var i = 0; i < ascending.Length - 1; i++)
        {
            var lower = AppVersion.TryParse(ascending[i])!;
            var higher = AppVersion.TryParse(ascending[i + 1])!;
            Assert.True(lower.CompareTo(higher) < 0, $"{lower} should be older than {higher}");
            Assert.True(higher.CompareTo(lower) > 0, $"{higher} should be newer than {lower}");
        }
    }

    [Fact]
    public void The_task_ordering_holds()
    {
        Assert.True(V("1.0.0-beta.1").CompareTo(V("1.0.0-beta.2")) < 0);
        Assert.True(V("1.0.0-beta.2").CompareTo(V("1.0.0-rc.1")) < 0);
        Assert.True(V("1.0.0-rc.1").CompareTo(V("1.0.0")) < 0);
    }

    [Fact]
    public void Build_metadata_is_dropped_and_never_orders()
    {
        Assert.Equal(0, V("1.0.0-beta.1+abc").CompareTo(V("1.0.0-beta.1+zzz")));
        Assert.Equal(0, V("1.0.0+1").CompareTo(V("1.0.0")));
        Assert.Equal("1.0.0-beta.1", V("1.0.0-beta.1+abc.def").Text);
    }

    [Fact]
    public void Numbers_compare_numerically_not_as_text()
    {
        Assert.True(V("1.9.0").CompareTo(V("1.10.0")) < 0);
        Assert.True(V("1.0.0-beta.9").CompareTo(V("1.0.0-beta.10")) < 0);
        Assert.Equal(0, V("1.02.0").CompareTo(V("1.2.0")));
    }

    [Fact]
    public void A_fourth_part_counts_and_a_missing_one_is_zero()
    {
        Assert.Equal(0, V("1.0.0.0").CompareTo(V("1.0.0")));
        Assert.True(V("1.0.0.1").CompareTo(V("1.0.0")) > 0);
    }

    [Fact]
    public void Huge_numbers_do_not_overflow()
    {
        var big = V("99999999999999999999999999.0.0");
        Assert.True(big.CompareTo(V("1.0.0")) > 0);
        Assert.True(V("1.0.0-99999999999999999999999999").CompareTo(V("1.0.0-9")) > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("1.0.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-beta 1")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0\0")]
    public void Garbled_versions_do_not_parse(string? text) => Assert.Null(AppVersion.TryParse(text));

    [Fact]
    public void An_overlong_version_does_not_parse() =>
        Assert.Null(AppVersion.TryParse("1.0.0-" + new string('a', 200)));

    private static AppVersion V(string text) => AppVersion.TryParse(text) ?? throw new InvalidOperationException(text);
}

public class InstallDecisionTests
{
    private const string This = "1.0.0-beta.2";

    private static InstalledFacts Both(string? exe, string? registry) => new(true, exe, registry);

    private static InstallDecision Decide(InstalledFacts facts, string thisVersion = This, bool force = false) =>
        InstallDecision.Decide(facts, thisVersion, force);

    [Fact]
    public void Nothing_installed_is_a_fresh_install() =>
        Assert.Equal(new InstallDecision.FreshInstall(This), Decide(InstalledFacts.None));

    [Fact]
    public void An_older_install_is_updated() =>
        Assert.Equal(new InstallDecision.Update("1.0.0-beta.1", This), Decide(Both("1.0.0-beta.1", "1.0.0-beta.1")));

    [Fact]
    public void The_same_version_is_a_repair() =>
        Assert.Equal(new InstallDecision.Repair(This), Decide(Both(This, This)));

    [Fact]
    public void Build_metadata_alone_does_not_make_an_update() =>
        Assert.Equal(new InstallDecision.Repair(This), Decide(Both("1.0.0-beta.2+abc", This)));

    [Fact]
    public void A_newer_install_is_refused_without_force() =>
        Assert.Equal(new InstallDecision.RefuseDowngrade("1.0.0", This), Decide(Both("1.0.0", "1.0.0")));

    [Fact]
    public void A_stable_release_is_newer_than_its_own_beta() =>
        Assert.IsType<InstallDecision.RefuseDowngrade>(Decide(Both("1.0.0", "1.0.0"), "1.0.0-rc.1"));

    [Fact]
    public void Force_turns_a_refusal_into_a_marked_downgrade() =>
        Assert.Equal(new InstallDecision.Update("1.0.0", This, IsDowngrade: true), Decide(Both("1.0.0", "1.0.0"), force: true));

    [Fact]
    public void Force_does_not_mark_an_ordinary_update_or_repair()
    {
        Assert.Equal(new InstallDecision.Update("1.0.0-beta.1", This), Decide(Both("1.0.0-beta.1", null), force: true));
        Assert.Equal(new InstallDecision.Repair(This), Decide(Both(This, This), force: true));
    }

    [Fact]
    public void The_exe_wins_when_it_and_the_registry_disagree()
    {
        // Registry claims newer, exe is older: the exe is what runs, so this is an update.
        Assert.Equal(new InstallDecision.Update("1.0.0-beta.1", This), Decide(Both("1.0.0-beta.1", "9.0.0")));
        // Registry claims older, exe is newer: refuse, on the exe's word.
        Assert.Equal(new InstallDecision.RefuseDowngrade("1.0.0", This), Decide(Both("1.0.0", "0.1.0")));
    }

    [Fact]
    public void The_registry_is_used_when_the_exe_has_no_readable_version()
    {
        Assert.Equal(new InstallDecision.Repair(This), Decide(Both(null, This)));
        Assert.Equal(new InstallDecision.RefuseDowngrade("1.0.0", This), Decide(Both("garbage", "1.0.0")));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("garbage", "also garbage")]
    [InlineData("1.0", null)]
    public void A_missing_or_garbled_version_counts_as_older(string? exe, string? registry) =>
        Assert.Equal(new InstallDecision.Update(null, This), Decide(Both(exe, registry)));

    [Fact]
    public void A_build_with_no_readable_version_is_never_called_a_downgrade()
    {
        Assert.Equal(new InstallDecision.Update("1.0.0", "unknown"), Decide(Both("1.0.0", "1.0.0"), "unknown"));
        Assert.Equal(new InstallDecision.FreshInstall("unknown"), Decide(InstalledFacts.None, "unknown"));
    }

    [Fact]
    public void A_stale_registry_label_is_found_only_when_it_disagrees_with_the_exe()
    {
        Assert.Equal("1.0.0", InstallDecision.StaleRegistryVersion(Both("1.0.0", "0.9.0")));
        Assert.Equal("1.0.0", InstallDecision.StaleRegistryVersion(Both("1.0.0", "junk")));
        Assert.Null(InstallDecision.StaleRegistryVersion(Both("1.0.0", "1.0.0+x")));
        // No registry label means no key to repair; no readable exe version means nothing to trust.
        Assert.Null(InstallDecision.StaleRegistryVersion(Both("1.0.0", null)));
        Assert.Null(InstallDecision.StaleRegistryVersion(Both(null, "1.0.0")));
    }

    [Fact]
    public void The_dialogs_say_what_the_task_specifies()
    {
        var fresh = InstallText.Offer(Decide(InstalledFacts.None));
        Assert.Equal("Install RoboRightClick 1.0.0-beta.2?", fresh.Heading);
        Assert.Equal("Install", fresh.Action);
        Assert.False(fresh.CanOpen);

        var update = InstallText.Offer(Decide(Both("1.0.0-beta.1", "1.0.0-beta.1")));
        Assert.Equal("Update RoboRightClick 1.0.0-beta.1 \u2192 1.0.0-beta.2?", update.Heading);

        var repair = InstallText.Offer(Decide(Both(This, This)));
        Assert.Equal("RoboRightClick 1.0.0-beta.2 is installed. Repair it?", repair.Heading);
        Assert.True(repair.CanOpen);
    }

    [Fact]
    public void A_newer_install_offers_nothing_that_changes_it()
    {
        var offer = InstallText.Offer(Decide(Both("1.0.0", "1.0.0")));
        Assert.Null(offer.Action);
        Assert.True(offer.CanOpen);
        Assert.Contains("1.0.0", offer.Body);
        Assert.Contains("--force", offer.Body);
    }

    [Fact]
    public void Every_decision_has_a_result_heading()
    {
        Assert.Equal("RoboRightClick was updated from 1.0.0-beta.1 to 1.0.0-beta.2.",
            InstallText.Done(Decide(Both("1.0.0-beta.1", null))));
        Assert.Equal("RoboRightClick 1.0.0-beta.2 was repaired.", InstallText.Done(Decide(Both(This, This))));
        Assert.Equal("RoboRightClick is installed.", InstallText.Done(Decide(InstalledFacts.None)));
    }

    [Fact]
    public void Force_and_the_install_backup_are_parsed_and_listed()
    {
        Assert.Equal(new CliInstall(null, Force: true), CommandLine.Parse(["--install", "--force"]));
        Assert.Equal(new CliInstall(true, Quiet: true, Force: true), CommandLine.Parse(["--install", "--quiet", "--FORCE", "--autostart"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--force", "--force"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--uninstall", "--force"]));
    }

    [Fact]
    public void Uninstall_removes_a_backup_exe_an_interrupted_update_left()
    {
        var paths = AppPaths.From(@"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming");
        var plan = UninstallPlan.For(paths, [], runningFromInstallDir: false);
        Assert.Contains(paths.InstalledExe + AppPaths.BackupExeSuffix, plan.Files);
    }
}
