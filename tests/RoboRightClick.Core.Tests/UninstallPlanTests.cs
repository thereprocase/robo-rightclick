using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class UninstallPlanTests
{
    private const string Local = @"C:\Users\Test User\AppData\Local";
    private const string Roaming = @"C:\Users\Test User\AppData\Roaming";
    private const string JobA = "20260101-120000-0123abcd";
    private const string JobB = "20260102-130000-fedcba98";

    private static readonly AppPaths Paths = AppPaths.From(Local, Roaming);

    private static string[] Roots => [Paths.InstallDirectory, Paths.ConfigDirectory, Paths.DataDirectory];

    private static bool UnderAppPaths(string path) =>
        Roots.Any(r => WinPath.AreSame(path, r) || WinPath.IsStrictlyUnder(path, r));

    [Fact]
    public void Lists_only_files_and_directories_under_AppPaths()
    {
        var plan = UninstallPlan.For(Paths, [JobA, JobB], runningFromInstallDir: false);

        Assert.All(plan.Files, f => Assert.True(UnderAppPaths(f), f));
        Assert.All(plan.Directories, d => Assert.True(UnderAppPaths(d), d));
        Assert.Contains(Paths.ConfigFile, plan.Files);
        Assert.Contains(Paths.ConfigFile + ".bad", plan.Files);
        Assert.Contains(Paths.HistoryFile, plan.Files);
        Assert.Contains(WinPath.Combine(Paths.DataDirectory, "history.1.jsonl"), plan.Files);
        Assert.Contains(WinPath.Combine(WinPath.Combine(Paths.JobsDirectory, JobA), "job.json"), plan.Files);
        Assert.Contains(WinPath.Combine(WinPath.Combine(Paths.JobsDirectory, JobB), "robocopy.log"), plan.Files);
    }

    [Fact]
    public void Lists_the_temp_files_a_crash_between_write_and_replace_leaves_behind()
    {
        // Without these, RemoveDirectory fails on the job folder or the config folder and
        // uninstall leaves them behind.
        var plan = UninstallPlan.For(Paths, [JobA], runningFromInstallDir: false);
        Assert.Contains(Paths.ConfigFile + ".tmp", plan.Files);
        Assert.Contains(WinPath.Combine(WinPath.Combine(Paths.JobsDirectory, JobA), "job.json.tmp"), plan.Files);
    }

    [Fact]
    public void Never_lists_a_drive_root_or_a_shared_parent()
    {
        var plan = UninstallPlan.For(Paths, [JobA], runningFromInstallDir: false);
        var shared = new[] { Local, Roaming, WinPath.GetParent(Paths.InstallDirectory), WinPath.GetRoot(Local) };
        Assert.DoesNotContain(plan.Directories, d => shared.Any(s => WinPath.AreSame(d, s)));
    }

    [Fact]
    public void Ignores_folder_names_the_app_did_not_create()
    {
        var plan = UninstallPlan.For(
            Paths, [JobA, "My Documents", "..", @"..\..\Windows", "20260101-120000-0123abcdX", "", "notes"],
            runningFromInstallDir: false);

        Assert.Equal(1, plan.Directories.Count(d => WinPath.IsStrictlyUnder(d, Paths.JobsDirectory)));
        Assert.DoesNotContain(plan.Files, f => f.Contains("My Documents") || f.Contains("..") || f.Contains("notes"));
    }

    [Fact]
    public void Lists_each_job_folder_once()
    {
        var plan = UninstallPlan.For(Paths, [JobA, JobA], runningFromInstallDir: true);
        Assert.Equal(1, plan.Directories.Count(d => WinPath.IsStrictlyUnder(d, Paths.JobsDirectory)));
    }

    [Fact]
    public void Removes_directories_deepest_first()
    {
        var plan = UninstallPlan.For(Paths, [JobA, JobB], runningFromInstallDir: false);

        for (var i = 0; i < plan.Directories.Count; i++)
        {
            for (var j = i + 1; j < plan.Directories.Count; j++)
            {
                // RemoveDirectory fails on a non-empty folder, so a folder must come
                // before the folder that contains it.
                Assert.False(
                    WinPath.IsStrictlyUnder(plan.Directories[j], plan.Directories[i]),
                    $"{plan.Directories[i]} is listed before {plan.Directories[j]}, which is inside it");
            }
        }
        Assert.Equal(Paths.JobsDirectory, plan.Directories[2]);
        Assert.Equal(Paths.DataDirectory, plan.Directories[3]);
    }

    [Fact]
    public void Leaves_the_install_folder_to_the_self_delete_command_when_running_from_it()
    {
        var running = UninstallPlan.For(Paths, [], runningFromInstallDir: true);
        Assert.DoesNotContain(running.Directories, d => WinPath.AreSame(d, Paths.InstallDirectory));
        Assert.DoesNotContain(Paths.InstalledExe, running.Files);

        var elsewhere = UninstallPlan.For(Paths, [], runningFromInstallDir: false);
        Assert.Equal(Paths.InstallDirectory, elsewhere.Directories[^1]);
        Assert.Contains(Paths.InstalledExe, elsewhere.Files);
    }

    [Fact]
    public void Every_directory_has_its_expected_leaf_name()
    {
        var plan = UninstallPlan.For(Paths, [JobA], runningFromInstallDir: false);

        Assert.Equal(plan.Directories.Count, plan.ExpectedLeafNames.Count);
        foreach (var directory in plan.Directories)
        {
            Assert.Equal(WinPath.GetFileName(directory), plan.ExpectedLeafNames[directory], ignoreCase: true);
        }
        Assert.Equal("jobs", plan.ExpectedLeafNames[Paths.JobsDirectory]);
        Assert.Equal(JobA, plan.ExpectedLeafNames[WinPath.Combine(Paths.JobsDirectory, JobA)]);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('&')]
    [InlineData('|')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('^')]
    [InlineData('%')]
    [InlineData('!')]
    public void Rejects_command_injection_characters_in_any_path(char c)
    {
        var bad = AppPaths.From(Local.Replace("Test User", "Test" + c + "User"), Roaming);

        Assert.Throws<ArgumentException>(() => UninstallPlan.For(bad, [], runningFromInstallDir: false));
        Assert.Throws<ArgumentException>(() => UninstallPlan.For(bad, [], runningFromInstallDir: true));
        Assert.Throws<ArgumentException>(() => UninstallPlan.SelfDeleteArguments(bad.InstalledExe, bad.InstallDirectory));
    }

    [Fact]
    public void Rejects_a_config_directory_with_an_injection_character_even_when_running_from_install()
    {
        var bad = AppPaths.From(Local, Roaming + "&calc");
        Assert.Throws<ArgumentException>(() => UninstallPlan.For(bad, [], runningFromInstallDir: true));
    }

    [Fact]
    public void Rejects_an_AppPaths_that_reaches_outside_the_app_folders()
    {
        var wrongLeaf = Paths with { DataDirectory = @"C:\Users\Test User\AppData\Local\Documents" };
        Assert.Throws<ArgumentException>(() => UninstallPlan.For(wrongLeaf, [], runningFromInstallDir: true));

        var strayJobs = Paths with { JobsDirectory = @"C:\Windows\jobs" };
        Assert.Throws<ArgumentException>(() => UninstallPlan.For(strayJobs, [], runningFromInstallDir: true));

        var rootInstall = Paths with { InstallDirectory = @"C:\", InstalledExe = @"C:\RoboRightClick.exe" };
        Assert.Throws<ArgumentException>(() => UninstallPlan.For(rootInstall, [], runningFromInstallDir: false));
    }

    [Fact]
    public void SelfDelete_command_uses_a_non_recursive_remove_and_a_bounded_retry()
    {
        var args = UninstallPlan.SelfDeleteArguments(Paths.InstalledExe, Paths.InstallDirectory);

        Assert.StartsWith("/d /s /c \"", args);
        var command = args["/d /s /c ".Length..];
        Assert.Contains($"rd \"{Paths.InstallDirectory}\"", command);
        Assert.DoesNotContain("rmdir", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/s", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(1,1,30)", command);
        Assert.Contains($"del /f /q \"{Paths.InstalledExe}\"", command);
    }

    [Fact]
    public void SelfDelete_refuses_an_exe_that_is_not_the_installed_one()
    {
        Assert.Throws<ArgumentException>(() => UninstallPlan.SelfDeleteArguments(@"C:\Windows\System32\cmd.exe", Paths.InstallDirectory));
        Assert.Throws<ArgumentException>(() => UninstallPlan.SelfDeleteArguments(Paths.InstalledExe, @"C:\Users\Test User\Documents"));
        Assert.Throws<ArgumentException>(() => UninstallPlan.SelfDeleteArguments(
            WinPath.Combine(Paths.InstallDirectory, "other.exe"), Paths.InstallDirectory));
    }
}
