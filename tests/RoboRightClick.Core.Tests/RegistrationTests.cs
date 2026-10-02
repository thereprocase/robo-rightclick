using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class RegistrationTests
{
    private const string Exe = @"C:\Users\Test User\AppData\Local\Programs\RoboRightClick\RoboRightClick.exe";
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    private static IReadOnlyList<RegistryValue> Install(bool startWithWindows) =>
        Registration.InstallValues(new InstallTarget(Exe, startWithWindows, Sid, "1.0.0-beta.1"));

    [Fact]
    public void Uninstall_removes_every_key_and_value_install_writes()
    {
        var removals = Registration.UninstallRemovals();
        var trees = removals.OfType<RemoveKeyTree>().Select(r => r.Key).ToList();
        var values = removals.OfType<RemoveValue>().ToList();

        foreach (var written in Install(startWithWindows: true))
        {
            var coveredByTree = trees.Any(t => WinPath.AreSame(written.Key, t) || WinPath.IsStrictlyUnder(written.Key, t));
            var coveredByValue = values.Any(v => WinPath.AreSame(v.Key, written.Key) && v.Name == written.Name);
            Assert.True(coveredByTree || coveredByValue, $"{written.Key} [{written.Name}] would survive uninstall");
        }
    }

    [Fact]
    public void Uninstall_deletes_only_trees_that_install_itself_created()
    {
        var writtenKeys = Install(startWithWindows: true).Select(v => v.Key).ToList();
        foreach (var tree in Registration.UninstallRemovals().OfType<RemoveKeyTree>())
        {
            // A tree removal must be a key install writes a value into, never a shared
            // parent such as ...\shell or ...\CLSID that other software also uses.
            Assert.Contains(writtenKeys, k => WinPath.AreSame(k, tree.Key));
            Assert.False(tree.Key.EndsWith(@"\shell", StringComparison.OrdinalIgnoreCase), tree.Key);
        }
    }

    [Fact]
    public void Nothing_touches_explorer_settings_or_leaves_hkcu_classes_and_run()
    {
        var keys = Install(startWithWindows: true).Select(v => v.Key)
            .Concat(Registration.UninstallRemovals().Select(r => r.Key));
        foreach (var key in keys)
        {
            Assert.DoesNotContain(@"\Explorer", key, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                key.StartsWith(Registration.ClassesRoot + @"\", StringComparison.Ordinal)
                    || key == Registration.RunKey
                    || key == Registration.UninstallKey,
                key);
        }
    }

    [Fact]
    public void Each_verb_delegates_to_its_own_class_with_player_selection()
    {
        var values = Install(startWithWindows: false);
        foreach (var verb in ShellVerbs.All)
        {
            foreach (var association in verb.Associations)
            {
                var verbKey = Registration.VerbKey(association, verb);
                Assert.Contains(new RegistryValue(verbKey + @"\command", "DelegateExecute", Registration.FormatGuid(verb.Clsid)), values);
                if (association == ShellVerbs.BackgroundAssociation)
                {
                    Assert.DoesNotContain(values, v => v.Key == verbKey && v.Name == "MultiSelectModel");
                }
                else
                {
                    Assert.Contains(new RegistryValue(verbKey, "MultiSelectModel", verb.MultiSelectModel), values);
                }
                Assert.Contains(new RegistryValue(verbKey, "MUIVerb", verb.Label), values);
            }
            Assert.Contains(new RegistryValue(Registration.ClsidKey(verb.Clsid) + @"\LocalServer32", "", "\"" + Exe + "\""), values);
        }
        Assert.Equal(3, ShellVerbs.All.Select(v => v.Clsid).Distinct().Count());
    }

    [Fact]
    public void Run_value_is_written_only_when_asked_but_always_removed()
    {
        Assert.DoesNotContain(Install(startWithWindows: false), v => v.Key == Registration.RunKey);
        Assert.Contains(Registration.RunValue(Exe), Install(startWithWindows: true));
        Assert.Contains(new RemoveValue(Registration.RunKey, Registration.RunValueName), Registration.UninstallRemovals());
    }

    [Fact]
    public void Verb_table_matches_the_documented_menu_locations()
    {
        var keys = ShellVerbs.All.SelectMany(v => v.Associations.Select(a => Registration.VerbKey(a, v))).ToList();
        Assert.Equal(
            [
                @"Software\Classes\AllFilesystemObjects\shell\RoboCopy",
                @"Software\Classes\AllFilesystemObjects\shell\RoboCut",
                @"Software\Classes\Directory\Background\shell\RoboPaste",
                @"Software\Classes\Directory\shell\RoboPaste",
                @"Software\Classes\Drive\shell\RoboPaste",
            ],
            keys);
    }

    [Fact]
    public void Paste_needs_exactly_one_destination()
    {
        Assert.Equal((@"D:\dst", null), ShellVerbs.PasteDestination([@"D:\dst\"]));
        Assert.Equal((@"D:\", null), ShellVerbs.PasteDestination([@"D:\"]));
        Assert.Equal(ShellVerbs.NotOneFolderReason, ShellVerbs.PasteDestination([@"D:\a", @"D:\b"]).Problem);
        Assert.Equal(ShellVerbs.NothingSelectedReason, ShellVerbs.PasteDestination([]).Problem);
    }

    [Fact]
    public void App_paths_follow_the_documented_layout()
    {
        var p = AppPaths.From(@"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming");
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\RoboRightClick\RoboRightClick.exe", p.InstalledExe);
        Assert.Equal(@"C:\Users\u\AppData\Roaming\RoboRightClick\config.json", p.ConfigFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\RoboRightClick\history.jsonl", p.HistoryFile);
        Assert.Equal(
            @"C:\Users\u\AppData\Local\RoboRightClick\jobs\20261002-153000-1a2b3c4d",
            p.JobFolder(new DateTimeOffset(2026, 10, 2, 15, 30, 0, TimeSpan.Zero), Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000")));
    }
}

public class CommandLineTests
{
    [Fact]
    public void No_arguments_and_embedding_run_the_tray()
    {
        Assert.Equal(new CliRunTray(false), CommandLine.Parse([]));
        Assert.Equal(new CliRunTray(true), CommandLine.Parse(["-Embedding"]));
        Assert.Equal(new CliRunTray(true), CommandLine.Parse(["/embedding"]));
        Assert.IsType<CliError>(CommandLine.Parse(["-Embedding", "x"]));
    }

    [Fact]
    public void Install_and_uninstall()
    {
        Assert.Equal(new CliInstall(null), CommandLine.Parse(["--install"]));
        Assert.Equal(new CliInstall(true), CommandLine.Parse(["--install", "--autostart"]));
        Assert.Equal(new CliInstall(false), CommandLine.Parse(["--install", "--no-autostart"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--autostart", "--no-autostart"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--bogus"]));
        Assert.IsType<CliUninstall>(CommandLine.Parse(["--UNINSTALL"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--uninstall", "now"]));
    }

    [Fact]
    public void Quiet_install_and_uninstall_in_any_order()
    {
        Assert.Equal(new CliInstall(null, Quiet: true), CommandLine.Parse(["--install", "--quiet"]));
        Assert.Equal(new CliInstall(true, Quiet: true), CommandLine.Parse(["--install", "--quiet", "--autostart"]));
        Assert.Equal(new CliInstall(false, Quiet: true), CommandLine.Parse(["--install", "--no-autostart", "--QUIET"]));
        Assert.Equal(new CliUninstall(Quiet: true), CommandLine.Parse(["--uninstall", "--quiet"]));
        Assert.Equal(new CliUninstall(Quiet: false), CommandLine.Parse(["--uninstall"]));
    }

    [Fact]
    public void Repeated_or_stray_install_options_are_refused()
    {
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--quiet", "--quiet"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--autostart", "--autostart"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--install", "--no-autostart", "--quiet", "--autostart"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--uninstall", "--quiet", "now"]));
        Assert.IsType<CliError>(CommandLine.Parse(["--uninstall", "--quiet", "--quiet"]));
    }

    [Fact]
    public void After_install_runs_the_tray_with_the_first_run_signal()
    {
        Assert.Equal(new CliRunTray(StartedByCom: false, AfterInstall: true), CommandLine.Parse(["--after-install"]));
        Assert.False(((CliRunTray)CommandLine.Parse([])).AfterInstall);
        Assert.False(((CliRunTray)CommandLine.Parse(["-Embedding"])).AfterInstall);
        Assert.IsType<CliError>(CommandLine.Parse(["--after-install", "x"]));
    }

    [Fact]
    public void Verbs_take_paths_and_paste_takes_one_folder()
    {
        var copy = Assert.IsType<CliInvokeVerb>(CommandLine.Parse(["copy", @"C:\a b.txt", @"C:\日本"]));
        Assert.Equal(ShellVerb.RoboCopy, copy.Verb);
        Assert.Equal([@"C:\a b.txt", @"C:\日本"], copy.Paths);
        Assert.Equal(ShellVerb.RoboCut, Assert.IsType<CliInvokeVerb>(CommandLine.Parse(["Cut", "x"])).Verb);
        Assert.Equal(ShellVerb.RoboPaste, Assert.IsType<CliInvokeVerb>(CommandLine.Parse(["paste", @"D:\"])).Verb);

        Assert.IsType<CliError>(CommandLine.Parse(["copy"]));
        Assert.IsType<CliError>(CommandLine.Parse(["paste", "a", "b"]));
        Assert.IsType<CliError>(CommandLine.Parse(["copy", " "]));
        Assert.IsType<CliError>(CommandLine.Parse(["move", "x"]));
        Assert.IsType<CliHelp>(CommandLine.Parse(["/?"]));
    }
}
