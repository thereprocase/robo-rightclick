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
                Assert.Contains(new RegistryValue(verbKey, "MUIVerb", verb.MenuLabel), values);
                Assert.Contains(new RegistryValue(verbKey, "Icon", Registration.IconPath(Exe, verb)), values);
            }
            Assert.Contains(new RegistryValue(Registration.ClsidKey(verb.Clsid) + @"\LocalServer32", "", "\"" + Exe + "\""), values);
        }
        Assert.Equal(3, ShellVerbs.All.Select(v => v.Clsid).Distinct().Count());
    }

    [Fact]
    public void Menu_labels_have_exactly_one_access_key_and_the_class_names_none()
    {
        var values = Install(startWithWindows: false);
        foreach (var verb in ShellVerbs.All)
        {
            Assert.Equal(1, verb.MenuLabel.Count(c => c == '&'));
            Assert.Equal(verb.Label, verb.MenuLabel.Replace("&", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain('&', verb.Label);
            Assert.True(char.IsAsciiLetter(verb.AccessKey), verb.MenuLabel);

            var clsidName = values.Single(v => v.Key == Registration.ClsidKey(verb.Clsid) && v.Name.Length == 0);
            Assert.Equal($"{AppInfo.Name} {verb.Label}", clsidName.Data);
            Assert.DoesNotContain('&', clsidName.Data);
            foreach (var muiVerb in values.Where(v => v.Name == "MUIVerb" && v.Key.EndsWith(@"" + verb.KeyName, StringComparison.Ordinal)))
            {
                Assert.Equal(1, muiVerb.Data.Count(c => c == '&'));
            }
        }
    }

    /// <summary>
    /// Provisional. The classic-menu letters Explorer itself uses, per kind of right-click, as
    /// named in the design review. Not measured yet: the release gate measures them on Windows
    /// and replaces this table with what it finds (docs/decisions/0001-paste-hotkey.md). Until
    /// then this test only proves the letters avoid the table. Entries Explorer adds for one
    /// kind of item are left out, and some likely share a letter: "Troubleshoot
    /// compatibility" on programs and shortcuts and "Open AutoPlay" on removable drives
    /// probably use Y, so Robo-Copy's Y may move between two items there (README describes
    /// that behavior); BitLocker entries on drives may use B. Y stays because every other
    /// letter of "Robo-Copy" is taken on the common menu or by Robo-Paste on folders.
    /// </summary>
    private static readonly Dictionary<string, string> ExplorerLettersByMenu = new()
    {
        ["file"] = "OTCAREPD",
        ["folder"] = "OTCAREPD",
        ["drive"] = "OCPRE",
        ["background"] = "PREU",
    };

    private static IEnumerable<ShellVerbInfo> VerbsOnMenu(string menu) => ShellVerbs.All.Where(v => v.Associations.Any(a => menu switch
    {
        "file" => a == "AllFilesystemObjects",
        "folder" => a is "AllFilesystemObjects" or "Directory",
        "drive" => a == "Drive",
        "background" => a == ShellVerbs.BackgroundAssociation,
        _ => false,
    }));

    [Theory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("drive")]
    [InlineData("background")]
    public void Access_keys_are_unique_and_clear_of_explorers_letters_on_each_menu(string menu)
    {
        var letters = VerbsOnMenu(menu).Select(v => v.AccessKey).ToList();
        Assert.NotEmpty(letters);
        Assert.Equal(letters.Count, letters.Distinct().Count());
        foreach (var letter in letters)
        {
            Assert.DoesNotContain(letter, ExplorerLettersByMenu[menu]);
        }
    }

    [Fact]
    public void Access_keys_are_the_documented_letters()
    {
        Assert.Equal('Y', ShellVerbs.RoboCopy.AccessKey);
        Assert.Equal('U', ShellVerbs.RoboCut.AccessKey);
        Assert.Equal('B', ShellVerbs.RoboPaste.AccessKey);
        // U is Undo on the background menu only, where Robo-Cut does not appear.
        Assert.DoesNotContain(ShellVerbs.RoboCut, VerbsOnMenu("background"));
    }

    [Fact]
    public void Menu_icons_are_plain_ico_paths_beside_the_installed_exe()
    {
        var installDir = WinPath.GetParent(Exe);
        var names = ShellVerbs.All.Select(ShellVerbs.IconFileName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var verb in ShellVerbs.All)
        {
            var path = Registration.IconPath(Exe, verb);
            // A bare .ico path, no ",index": the shell then reads every DPI frame in the file.
            Assert.EndsWith(".ico", path, StringComparison.Ordinal);
            Assert.DoesNotContain(",", path);
            Assert.True(WinPath.AreSame(WinPath.GetParent(path), installDir), path);
        }
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
