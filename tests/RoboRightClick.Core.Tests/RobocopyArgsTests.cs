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
            "/NP /NDL /NC /NJH /NJS /BYTES /FP /UNILOG:\\\\.\\pipe\\rrc-test-1",
            args);
    }

    [Fact]
    public void Folder_copy_is_recursive()
    {
        var args = RobocopyArgs.Build(DirStep, Settings.Default, ConflictPolicy.Ask, Pipe);
        Assert.Contains(" /E ", args);
        Assert.DoesNotContain("/MOV", args);
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
    [InlineData(ConflictPolicy.KeepNewer, "/XO")]
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
        var s = Settings.Default with { ExtraArgs = new ExtraArgs("/J", "/XJ") };
        Assert.EndsWith(" /J", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        Assert.EndsWith(" /XJ", RobocopyArgs.Build(FileStep with { Move = true }, s, ConflictPolicy.Ask, Pipe));
    }

    [Fact]
    public void Forbidden_extra_args_are_never_emitted_even_if_settings_were_built_in_code()
    {
        var s = Settings.Default with { ExtraArgs = new ExtraArgs("/MIR", "/LOG:C:\\x.log") };
        Assert.DoesNotContain("/MIR", RobocopyArgs.Build(FileStep, s, ConflictPolicy.Ask, Pipe));
        Assert.DoesNotContain("/LOG", RobocopyArgs.Build(FileStep with { Move = true }, s, ConflictPolicy.Ask, Pipe));
    }

    [Theory]
    [InlineData("/MIR", "/MIR")]
    [InlineData("/j /purge", "/PURGE")]
    [InlineData("/LOG+:C:\\x.log", "/LOG+")]
    [InlineData("/unilog:x /tee", "/UNILOG,/TEE")]
    [InlineData("/MT:64", "/MT")]
    [InlineData("/L", "/L")]
    [InlineData("/J /XJ /SL", "")]
    [InlineData("/XF *.tmp", "")]
    public void FindForbiddenSwitches(string extra, string expected)
    {
        var found = string.Join(",", RobocopyArgs.FindForbiddenSwitches(extra));
        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData(@"C:\", "\"C:\\.\"")]
    [InlineData(@"\\srv\share\", "\"\\\\srv\\share\\.\"")]
    [InlineData(@"C:\a b", "\"C:\\a b\"")]
    public void Quote_never_lets_a_trailing_backslash_escape_the_quote(string input, string expected) =>
        Assert.Equal(expected, RobocopyArgs.Quote(input));
}
