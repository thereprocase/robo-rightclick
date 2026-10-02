using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>Install refuses exactly the locations uninstall would refuse, so no install is ever stuck.</summary>
public class InstallPathSafetyTests
{
    private const string Local = @"C:\Users\Test User\AppData\Local";
    private const string Roaming = @"C:\Users\Test User\AppData\Roaming";
    private const string System32 = @"C:\Windows\System32";

    [Fact]
    public void An_ordinary_profile_installs()
    {
        Assert.Null(UninstallPlan.InstallRefusal(AppPaths.From(Local, Roaming), System32));
    }

    [Theory]
    [InlineData(@"C:\Users\Zoë Ådahl\AppData\Local")]
    [InlineData(@"C:\Users\田中\AppData\Local")]
    [InlineData(@"D:\Profiles\o'brien\AppData\Local")]
    [InlineData(@"\\fileserver\profiles\ann\AppData\Local")]
    public void Unusual_but_safe_profiles_install(string local)
    {
        Assert.Null(UninstallPlan.InstallRefusal(AppPaths.From(local, Roaming), System32));
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
    [InlineData('\t')]
    public void A_local_profile_path_uninstall_would_refuse_is_refused_at_install(char c)
    {
        var paths = AppPaths.From(Local.Replace("Test User", "R" + c + "D"), Roaming);
        Assert.Equal(UninstallPlan.UnsafePathsRefusal, UninstallPlan.InstallRefusal(paths, System32));
    }

    [Theory]
    [InlineData('&')]
    [InlineData('!')]
    [InlineData('%')]
    public void A_roaming_profile_path_uninstall_would_refuse_is_refused_at_install(char c)
    {
        // Only the config folder is under %APPDATA%; uninstall from the install folder still
        // deletes it, and would refuse.
        var paths = AppPaths.From(Local, Roaming.Replace("Test User", "R" + c + "D"));
        Assert.NotNull(UninstallPlan.InstallRefusal(paths, System32));
    }

    [Theory]
    [InlineData(@"System32")]
    [InlineData(@"\\srv\share\System32")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Win&dows\System32")]
    public void A_system_folder_the_self_delete_would_refuse_is_refused_at_install(string systemDirectory)
    {
        Assert.NotNull(UninstallPlan.InstallRefusal(AppPaths.From(Local, Roaming), systemDirectory));
    }

    [Fact]
    public void A_hand_assembled_layout_is_refused()
    {
        var paths = AppPaths.From(Local, Roaming) with { DataDirectory = @"C:\Users\Test User\Documents" };
        Assert.NotNull(UninstallPlan.InstallRefusal(paths, System32));
    }

    [Theory]
    [InlineData(@"C:\Users\Test User\AppData\Local", @"C:\Users\Test User\AppData\Roaming")]
    [InlineData(@"C:\Users\A&B\AppData\Local", @"C:\Users\A&B\AppData\Roaming")]
    [InlineData(@"C:\Users\ok\AppData\Local", @"C:\Users\50%\AppData\Roaming")]
    [InlineData(@"C:\Users\x!y\AppData\Local", @"C:\Users\ok\AppData\Roaming")]
    public void Install_is_accepted_exactly_when_both_uninstall_routes_accept(string local, string roaming)
    {
        var paths = AppPaths.From(local, roaming);
        var uninstallWorks = Accepts(() => UninstallPlan.For(paths, ["20260101-120000-0123abcd"], runningFromInstallDir: true))
            && Accepts(() => UninstallPlan.For(paths, ["20260101-120000-0123abcd"], runningFromInstallDir: false))
            && Accepts(() => UninstallPlan.SelfDeleteArguments(paths.InstalledExe, paths.InstallDirectory, System32));

        Assert.Equal(uninstallWorks, UninstallPlan.InstallRefusal(paths, System32) is null);
    }

    [Fact]
    public void The_refusal_names_no_path()
    {
        var text = UninstallPlan.UnsafePathsRefusal;
        Assert.DoesNotContain(@":\", text);
        Assert.Contains("Nothing was changed", text);
    }

    private static bool Accepts(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
