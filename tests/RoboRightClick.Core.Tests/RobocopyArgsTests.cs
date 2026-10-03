using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class RobocopyArgsTests
{
    private static readonly RobocopyStep FileStep = new(@"C:\src", @"D:\dst", ["a b.txt", "c.txt"], Recursive: false, Move: false);
    private const string Pipe = "rrc-test-1";
    private static readonly RobocopyStep DirStep = new(@"C:\src\photos", @"D:\dst\photos", [], Recursive: true, Move: false);

    [Fact]
    public void Default_settings_produce_explorer_semantics_with_32_threads()
    {
        var args = RobocopyArgs.Build(FileStep, Settings.Default, ConflictPolicy.Ask, Pipe);

        Assert.Equal(
            "\"C:\\src\" \"D:\\dst\" \"a b.txt\" \"c.txt\" /MT:32 /R:0 /W:0 /COPY:DAT /DCOPY:DA /A+:A /XJD " +
            "/NP /NDL /NC /NJH /NJS /BYTES /FP /XX /UNILOG:\\\\.\\pipe\\rrc-test-1 /XC /XN /XO",
            args);
    }

    [Theory]
    [InlineData("-E")]
    [InlineData("-MOV")]
    [InlineData("/MIR")]
    public void A_file_name_robocopy_would_read_as_a_switch_is_never_put_on_the_command_line(string name)
    {
        var step = FileStep with { FileNames = ["ok.txt", name] };

        Assert.Throws<ArgumentException>(() => RobocopyArgs.Build(step, Settings.Default, ConflictPolicy.Ask, Pipe));
    }

    [Theory]
    [InlineData("a-b.txt", false)]
    [InlineData("x-", false)]
    [InlineData(" -E", false)]
    [InlineData("-E", true)]
    [InlineData("/E", true)]
    [InlineData("", false)]
    public void Only_a_leading_dash_or_slash_makes_a_name_switch_like(string name, bool expected) =>
        Assert.Equal(expected, RobocopyArgs.IsSwitchLikeName(name));

    [Fact]
    public void Folder_copy_is_recursive()
    {
        var args = RobocopyArgs.Build(DirStep, Settings.Default, ConflictPolicy.Ask, Pipe);
        Assert.Contains(" /E ", args);
        Assert.DoesNotContain("/MOV", args);
    }

    [Fact]
    public void Destination_only_files_are_kept_out_of_the_output_for_every_run()
    {
        // With /NC an "extra" file's line looks like a copied file with a destination path;
        // the ledger would match nothing and mark the whole job's paths unreliable.
        foreach (var policy in Enum.GetValues<ConflictPolicy>())
        {
            foreach (var step in new[] { FileStep, DirStep, DirStep with { Move = true } })
            {
                var switches = RobocopyArgs.Build(step, Settings.Default, policy, Pipe).Split(' ');
                Assert.Contains("/XX", switches);
                Assert.DoesNotContain("/X", switches);
            }
        }
    }

    [Fact]
    public void Move_uses_MOVE_for_trees_and_MOV_for_files()
    {
        Assert.Contains(" /MOVE ", RobocopyArgs.Build(DirStep with { Move = true }, Settings.Default, ConflictPolicy.Ask, Pipe));
        var fileMove = RobocopyArgs.Build(FileStep with { Move = true }, Settings.Default, ConflictPolicy.Ask, Pipe);
        Assert.Contains(" /MOV ", fileMove);
        Assert.DoesNotContain("/MOVE", fileMove);
    }

    [Fact]
    public void The_only_log_target_is_the_app_owned_pipe()
    {
        var hostile = Settings.Default with { ExtraArgs = new ExtraArgs("/LOG:C:\\x.log /TEE", "/UNILOG+:C:\\y.log") };
        foreach (var settings in new[] { Settings.Default, hostile })
        {
            foreach (var policy in Enum.GetValues<ConflictPolicy>())
            {
                foreach (var step in new[] { FileStep, DirStep with { Move = true } })
                {
                    var args = RobocopyArgs.Build(step, settings, policy, Pipe);
                    var logSwitches = args.Split(' ').Where(a => a.StartsWith("/LOG", StringComparison.OrdinalIgnoreCase)
                        || a.StartsWith("/UNILOG", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("/TEE", StringComparison.OrdinalIgnoreCase)).ToList();
                    Assert.Equal([@"/UNILOG:\\.\pipe\rrc-test-1"], logSwitches);
                }
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("..\\x")]
    [InlineData("C:\\temp\\log")]
    public void Pipe_names_cannot_smuggle_a_file_path(string name) =>
        Assert.Throws<ArgumentException>(() => RobocopyArgs.LogPipeArgument(name));

    [Theory]
    [InlineData(ConflictPolicy.Replace, "/IS /IT /IM")]
    [InlineData(ConflictPolicy.Skip, "/XC /XN /XO")]
    [InlineData(ConflictPolicy.KeepNewer, "/XC /XO")]
    [InlineData(ConflictPolicy.Ask, "/XC /XN /XO")]
    public void Conflict_policy_flags_are_appended(ConflictPolicy policy, string flags) =>
        Assert.EndsWith(" " + flags, RobocopyArgs.Build(FileStep, Settings.Default, policy, Pipe));

    [Fact]
    public void Thread_and_retry_settings_flow_through()
    {
        var s = Settings.Default with { Threads = 8, Retries = 2, RetryWaitSeconds = 5 };
        Assert.Contains(" /MT:8 /R:2 /W:5 ", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
    }

    [Fact]
    public void Extra_args_are_appended_per_verb()
    {
        var s = Settings.Default with { ExtraArgs = new ExtraArgs("/J", "/Z") };
        Assert.EndsWith(" /J", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        Assert.EndsWith(" /Z", RobocopyArgs.Build(FileStep with { Move = true }, s, ConflictPolicy.Ask, Pipe));
    }

    [Fact]
    public void Forbidden_extra_args_are_never_emitted_even_if_settings_were_built_in_code()
    {
        var s = Settings.Default with { ExtraArgs = new ExtraArgs("/MIR", "/LOG:C:\\x.log") };
        Assert.DoesNotContain("/MIR", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        Assert.DoesNotContain("/LOG", RobocopyArgs.Build(FileStep with { Move = true }, s, ConflictPolicy.Ask, Pipe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/J /Z /SL")]
    [InlineData("/compress /NoOffload /FFT /DST")]
    [InlineData("/IORATE:50M /IOMAXSIZE:1048576 /THRESHOLD:2g")]
    public void Allowed_extra_args_have_no_problems(string extra) =>
        Assert.Empty(RobocopyArgs.ExtraArgProblems(extra));

    [Theory]
    // Destructive or log-writing switches.
    [InlineData("/MIR")]
    [InlineData("/j /purge")]
    [InlineData("/LOG+:C:\\x.log")]
    [InlineData("/unilog:x")]
    [InlineData("/TEE")]
    [InlineData("/MT:64")]
    [InlineData("/L")]
    // Robocopy strips quotes, so a quoted switch is still the switch.
    [InlineData("\"/MIR\"")]
    [InlineData("\"/LOG:C:\\x.txt\"")]
    [InlineData("/MI\"R\"")]
    [InlineData("/MI^R")]
    // Selection changers: they would make the app's planned-file accounting wrong.
    [InlineData("/S")]
    [InlineData("/E")]
    [InlineData("/LEV:1")]
    [InlineData("/IF *.txt")]
    [InlineData("/XF *.tmp")]
    [InlineData("/XD x")]
    [InlineData("/XO")]
    [InlineData("/MAX:10")]
    // Never-ending or output-breaking runs, privilege tricks.
    [InlineData("/MON:1")]
    [InlineData("/MOT:1")]
    [InlineData("/RH:0100-0200")]
    [InlineData("/NFL")]
    [InlineData("/ZB")]
    [InlineData("/B")]
    // Positional tokens become extra file filters; dash switches are not examined by name.
    [InlineData("*.txt")]
    [InlineData("-MIR")]
    // Malformed values.
    [InlineData("/IORATE:")]
    [InlineData("/IORATE:5X")]
    [InlineData("/IORATE:5\"M")]
    [InlineData("/IORATE:1^0")]
    [InlineData("/IORATE:1\"/MIR")]
    [InlineData("/J:1")]
    public void Unsafe_extra_args_are_refused(string extra) =>
        Assert.NotEmpty(RobocopyArgs.ExtraArgProblems(extra));

    [Theory]
    // A non-breaking space pasted from a web page, and other separators the C runtime does not
    // split on: robocopy would get "/J<NBSP>/Z" as one argument and fail every run.
    [InlineData("/J\u00A0/Z")]
    [InlineData("/J\u2028/Z")]
    [InlineData("/J\u0085/Z")]
    [InlineData("/J\n/Z")]
    [InlineData("/J\r\n/Z")]
    [InlineData("/J\u00A0")]
    public void Separators_robocopy_does_not_split_on_are_refused(string extra)
    {
        var problems = RobocopyArgs.ExtraArgProblems(extra);

        Assert.NotEmpty(problems);
        Assert.Contains("\\u", Assert.Single(problems));
        Assert.DoesNotContain(" /J", RobocopyArgs.Build(FileStep, Settings.Default with { ExtraArgs = new ExtraArgs(extra, extra) }, ConflictPolicy.Ask, Pipe));
    }

    [Fact]
    public void Extra_args_reach_robocopy_joined_by_single_plain_spaces()
    {
        const string extra = " /J \t\t /Z  /IORATE:50M ";
        var s = Settings.Default with { ExtraArgs = new ExtraArgs(extra, extra) };

        Assert.Empty(RobocopyArgs.ExtraArgProblems(extra));
        Assert.EndsWith(" /J /Z /IORATE:50M", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        var read = SettingsSerializer.Parse("{\"version\": 2, \"extraArgs\": {\"copy\": \"/J\\t/Z\", \"move\": \"/J\\u00A0/Z\"}}");
        Assert.Equal(new ExtraArgs("/J /Z", string.Empty), read.Settings.ExtraArgs);
        Assert.Contains(read.Problems, p => p.StartsWith("'extraArgs.move' ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_args_have_a_length_cap()
    {
        var longArgs = string.Join(' ', Enumerable.Repeat("/J", 400));
        Assert.True(longArgs.Length > RobocopyArgs.MaxExtraArgsLength);
        Assert.NotEmpty(RobocopyArgs.ExtraArgProblems(longArgs));
    }

    [Fact]
    public void Quoted_log_switch_in_settings_built_in_code_is_never_emitted()
    {
        var s = Settings.Default with { ExtraArgs = new ExtraArgs("\"/LOG:C:\\x.txt\"", "\"/UNILOG:C:\\y.txt\"") };
        Assert.DoesNotContain("x.txt", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        Assert.DoesNotContain("y.txt", RobocopyArgs.Build(FileStep with { Move = true }, s, ConflictPolicy.Ask, Pipe));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("C:\\x\" \"D:\\y\" /MIR \"")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void Quote_refuses_values_that_could_break_out_of_the_argument(string value) =>
        Assert.Throws<ArgumentException>(() => RobocopyArgs.Quote(value));

    [Fact]
    public void Command_line_length_counts_exe_arguments_and_terminator()
    {
        Assert.Equal(1 + 4 + 1 + 1 + 3 + 1, RobocopyArgs.CommandLineLength("r.ex", "abc"));
        Assert.Equal(32_767, RobocopyArgs.MaxCommandLineLength);
    }

    [Theory]
    [InlineData(@"C:\", "\"C:\\.\"")]
    [InlineData(@"\\srv\share\", "\"\\\\srv\\share\\.\"")]
    [InlineData(@"C:\a b", "\"C:\\a b\"")]
    public void Quote_never_lets_a_trailing_backslash_escape_the_quote(string input, string expected) =>
        Assert.Equal(expected, RobocopyArgs.Quote(input));
}
