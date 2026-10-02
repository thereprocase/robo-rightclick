using System.Text.Json.Nodes;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class HotkeySpecTests
{
    private static HotkeySpec Valid(string text)
    {
        var (spec, problem) = HotkeySpec.Parse(text);
        Assert.Null(problem);
        return Assert.IsType<HotkeySpec>(spec);
    }

    private static string Refused(string text)
    {
        var (spec, problem) = HotkeySpec.Parse(text);
        Assert.Null(spec);
        return Assert.IsType<string>(problem);
    }

    private static SettingsLoadResult Load(string pasteHotkeyJson) =>
        SettingsSerializer.Parse($$"""{ "pasteHotkey": {{pasteHotkeyJson}} }""");

    [Theory]
    [InlineData("Ctrl+Shift+V", "Ctrl+Shift+V")]
    [InlineData("ctrl+shift+v", "Ctrl+Shift+V")]
    [InlineData("  Control +  SHIFT+ v ", "Ctrl+Shift+V")]
    [InlineData("Shift+Ctrl+F5", "Ctrl+Shift+F5")]
    [InlineData("ctrl+f12", "Ctrl+F12")]
    [InlineData("Ctrl+Q", "Ctrl+Q")]
    [InlineData("Ctrl+0", "Ctrl+0")]
    [InlineData("Ctrl+Shift+0", "Ctrl+Shift+0")]
    public void Valid_text_parses_to_its_canonical_form(string text, string canonical)
    {
        var spec = Valid(text);
        Assert.Equal(canonical, spec.Format());
        Assert.Equal(spec, Valid(spec.Format()));
    }

    [Fact]
    public void Every_unreserved_combination_round_trips()
    {
        var keys = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range('0', 10).Select(c => ((char)c).ToString()))
            .Concat(Enumerable.Range(1, 12).Select(n => "F" + n));
        var count = 0;
        foreach (var key in keys)
        {
            foreach (var shift in new[] { false, true })
            {
                if (key == "F10" || HotkeySpec.ReservedReason(shift, key) is not null)
                {
                    continue;
                }
                var text = (shift ? "Ctrl+Shift+" : "Ctrl+") + key;
                var spec = Valid(text);
                Assert.Equal(text, spec.Format());
                Assert.Equal(shift, spec.Shift);
                count++;
            }
        }
        Assert.True(count > 40, $"only {count} combinations are free");
    }

    [Theory]
    [InlineData("Ctrl+Shift+V", 0x56)]
    [InlineData("Ctrl+Shift+B", 0x42)]
    [InlineData("Ctrl+0", 0x30)]
    [InlineData("Ctrl+F2", 0x71)]
    [InlineData("Ctrl+F12", 0x7B)]
    public void Keys_map_to_their_virtual_key_codes(string text, int virtualKey) =>
        Assert.Equal(virtualKey, Valid(text).VirtualKey);

    [Fact]
    public void The_default_is_ctrl_shift_v_and_is_not_reserved()
    {
        Assert.Equal("Ctrl+Shift+V", HotkeySpec.Default.Format());
        Assert.Equal(HotkeySpec.DefaultText, HotkeySpec.Default.Format());
        Assert.Equal(HotkeySpec.Default, Valid(HotkeySpec.DefaultText));
        Assert.Null(HotkeySpec.ReservedReason(shift: true, "V"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_is_off_without_a_problem(string text)
    {
        Assert.Equal((null, null), HotkeySpec.Parse(text));
    }

    [Theory]
    [InlineData("Shift+V", "must include Ctrl")]
    [InlineData("V", "must include Ctrl")]
    [InlineData("F5", "must include Ctrl")]
    [InlineData("Ctrl+Alt+V", "Alt")]
    [InlineData("Ctrl+Shift+Alt+V", "Alt")]
    [InlineData("Ctrl+AltGr+V", "Alt")]
    [InlineData("Ctrl+Win+V", "Windows key")]
    [InlineData("Win+Ctrl+V", "Windows key")]
    [InlineData("Ctrl+Meta+V", "Windows key")]
    [InlineData("Ctrl+Ctrl+V", "Ctrl twice")]
    [InlineData("Ctrl+Control+V", "Ctrl twice")]
    [InlineData("Ctrl+Shift+Shift+V", "Shift twice")]
    [InlineData("Ctrl+Shift+Space", "does not support")]
    [InlineData("Ctrl+Shift+F13", "does not support")]
    [InlineData("Ctrl+Shift+F0", "does not support")]
    [InlineData("Ctrl+Shift+F01", "does not support")]
    [InlineData("Ctrl+Shift+VV", "does not support")]
    [InlineData("Ctrl+Shift+é", "does not support")]
    [InlineData("none", "does not support")]
    [InlineData("Ctrl+V+Shift", "exactly one key")]
    [InlineData("Ctrl+Q+W", "exactly one key")]
    [InlineData("Ctrl++V", "empty part")]
    [InlineData("Ctrl+Shift+", "empty part")]
    [InlineData("+", "empty part")]
    [InlineData("Ctrl+Shift", "needs a key")]
    [InlineData("Ctrl", "needs a key")]
    [InlineData("Ctrl+F10", "F10")]
    [InlineData("Ctrl+Shift+F10", "F10")]
    public void Malformed_text_is_refused_with_a_reason(string text, string reasonPart)
    {
        Assert.Contains(reasonPart, Refused(text), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_over_the_length_limit_is_refused()
    {
        var padded = "Ctrl+Shift+" + new string(' ', HotkeySpec.MaxTextLength) + "V";
        Assert.Contains("longer than", Refused(padded), StringComparison.Ordinal);
        var problem = Refused(new string('x', 10_000));
        Assert.True(problem.Length < 100, "a huge value must not be echoed into the problem");
    }

    [Fact]
    public void An_unknown_key_is_shortened_in_the_problem()
    {
        var problem = Refused("Ctrl+" + new string('q', 25));
        Assert.DoesNotContain(new string('q', 13), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reserved_combination_is_refused_with_its_reason()
    {
        Assert.NotEmpty(HotkeySpec.Reserved);
        foreach (var reserved in HotkeySpec.Reserved)
        {
            Assert.False(string.IsNullOrWhiteSpace(reserved.Reason));
            var problem = Refused(reserved.Text);
            Assert.Contains(reserved.Reason, problem, StringComparison.Ordinal);
            Assert.Contains(reserved.Text, problem, StringComparison.Ordinal);
            // Case and spacing do not get around it.
            Refused(" " + reserved.Text.ToLowerInvariant().Replace("+", " + ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_reserved_table_is_exactly_the_documented_list()
    {
        var expected = new List<string>();
        expected.AddRange("ACDEFLNRTVWXYZ".Select(c => "Ctrl+" + c));
        expected.AddRange("123456789".Select(c => "Ctrl+" + c));
        expected.AddRange(["Ctrl+F1", "Ctrl+F4"]);
        expected.AddRange("CNET123456789".Select(c => "Ctrl+Shift+" + c));
        Assert.Equal(expected.Order(StringComparer.Ordinal), HotkeySpec.Reserved.Select(r => r.Text).Order(StringComparer.Ordinal));
    }

    // ---- config.json ----------------------------------------------------------------

    [Fact]
    public void A_missing_key_means_the_default()
    {
        var result = SettingsSerializer.Parse("{}");
        Assert.Empty(result.Problems);
        Assert.Equal(HotkeySpec.Default, result.Settings.PasteHotkey);
        Assert.Equal(HotkeySpec.Default, Settings.Default.PasteHotkey);
    }

    [Fact]
    public void An_empty_string_is_off_without_a_problem()
    {
        var result = Load("\"\"");
        Assert.Empty(result.Problems);
        Assert.Null(result.Settings.PasteHotkey);
    }

    [Theory]
    [InlineData("\"none\"")]
    [InlineData("\"Ctrl+V\"")]
    [InlineData("\"Ctrl+Alt+V\"")]
    [InlineData("\"Shift+V\"")]
    [InlineData("\"Ctrl+Shift+Space\"")]
    [InlineData("\"Ctrl+Shift+V                                   \"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("86")]
    [InlineData("{ \"key\": \"V\" }")]
    [InlineData("[\"Ctrl\", \"Shift\", \"V\"]")]
    public void Anything_invalid_is_off_not_the_default_and_names_the_setting(string json)
    {
        var result = Load(json);

        Assert.Null(result.Settings.PasteHotkey);
        var problem = Assert.Single(result.Problems);
        Assert.StartsWith("'pasteHotkey' ", problem, StringComparison.Ordinal);
        Assert.EndsWith(SettingsSerializer.PasteHotkeyOffSuffix, problem, StringComparison.Ordinal);
        Assert.True(SettingsSerializer.IsPasteHotkeyProblem(problem));
    }

    [Fact]
    public void An_invalid_hotkey_leaves_the_other_settings_alone()
    {
        var result = SettingsSerializer.Parse("""{ "pasteHotkey": "Ctrl+Alt+V", "threads": 8, "logging": "ephemeral" }""");
        Assert.Null(result.Settings.PasteHotkey);
        Assert.Equal(8, result.Settings.Threads);
        Assert.Equal(LoggingMode.Ephemeral, result.Settings.Logging);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Off_and_custom_round_trip_and_the_canonical_form_is_saved()
    {
        foreach (var hotkey in new[] { null, HotkeySpec.Default, Valid("shift+ctrl+f7") })
        {
            var settings = Settings.Default with { PasteHotkey = hotkey };
            var json = SettingsSerializer.Serialize(settings);
            var result = SettingsSerializer.Parse(json);
            Assert.Empty(result.Problems);
            Assert.Equal(settings, result.Settings);
            Assert.Equal(hotkey?.Format() ?? string.Empty, (string?)JsonNode.Parse(json)!["pasteHotkey"]);
        }
    }

    [Fact]
    public void The_setting_needs_no_format_version_change()
    {
        Assert.Equal(1, SettingsSerializer.CurrentVersion);
        var result = SettingsSerializer.Parse("""{ "version": 1, "pasteHotkey": "Ctrl+Shift+V" }""");
        Assert.Empty(result.Problems);
        Assert.False(result.SavesRefused);
    }

    /// <summary>
    /// A file damaged after the user turned the hotkey off must not switch the keyboard hook
    /// back on: whole-file fallback follows the same rule as a bad "pasteHotkey" alone.
    /// </summary>
    [Theory]
    [InlineData("""{ "pasteHotkey": "", }} """)]
    [InlineData("""{ "pasteHotkey": "" """)]
    [InlineData("""["pasteHotkey", ""]""")]
    [InlineData("null")]
    public void An_unreadable_file_turns_the_hotkey_off_and_says_why(string json)
    {
        var result = SettingsSerializer.Parse(json);

        Assert.True(result.Unreadable);
        Assert.Null(result.Settings.PasteHotkey);
        Assert.Equal(HotkeyStatus.Invalid, HotkeyStatusRules.Derive(result.Settings.PasteHotkey, result.Problems, hookFailed: false));
        Assert.EndsWith(SettingsSerializer.PasteHotkeyOffSuffix, Assert.Single(result.Problems, SettingsSerializer.IsPasteHotkeyProblem), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_could_not_be_read_from_disk_turns_the_hotkey_off_too()
    {
        var result = SettingsSerializer.UnreadableResult("config could not be read; using defaults");

        Assert.True(result.Unreadable);
        Assert.Null(result.Settings.PasteHotkey);
        Assert.Equal(Settings.Default with { PasteHotkey = null }, result.Settings);
        Assert.Equal("config could not be read; using defaults", result.Problems[0]);
        Assert.Equal(HotkeyStatus.Invalid, HotkeyStatusRules.Derive(null, result.Problems, hookFailed: false));
    }

    [Fact]
    public void Only_hotkey_problems_count_as_hotkey_problems()
    {
        var result = SettingsSerializer.Parse("""{ "threads": 500 }""");
        Assert.DoesNotContain(result.Problems, SettingsSerializer.IsPasteHotkeyProblem);
    }

    // ---- Settings load notice -------------------------------------------------------

    [Fact]
    public void A_hotkey_problem_alone_says_off_and_never_default()
    {
        var notice = SettingsLoadNotice.Compose(Load("\"Ctrl+V\"").Problems);

        Assert.StartsWith("config.json has a problem, so the hotkey is off: 'pasteHotkey' ", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("default", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOf("is off", notice));
        Assert.EndsWith("Save writes the values shown here and fixes the file.", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Hotkey_and_other_problems_get_a_sentence_each()
    {
        var result = SettingsSerializer.Parse("""{ "threads": 500, "pasteHotkey": "Ctrl+Alt+V" }""");
        var notice = SettingsLoadNotice.Compose(result.Problems);

        var defaults = notice.IndexOf("using their defaults: 'threads'", StringComparison.Ordinal);
        var off = notice.IndexOf("The hotkey is off: 'pasteHotkey'", StringComparison.Ordinal);
        Assert.True(defaults >= 0 && off > defaults, notice);
        Assert.DoesNotContain("pasteHotkey", notice[..off], StringComparison.Ordinal);
        Assert.Equal(1, CountOf("is off", notice));
    }

    [Fact]
    public void Other_problems_keep_the_defaults_wording_and_no_problems_say_nothing()
    {
        var notice = SettingsLoadNotice.Compose(SettingsSerializer.Parse("""{ "threads": 500 }""").Problems);
        Assert.StartsWith("config.json has problems, so these settings are using their defaults: 'threads' ", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("hotkey", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, SettingsLoadNotice.Compose([]));
    }

    private static int CountOf(string part, string text) =>
        (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    // ---- tray line ------------------------------------------------------------------

    [Fact]
    public void Status_distinguishes_off_invalid_failed_and_active()
    {
        var invalid = Load("\"Ctrl+V\"").Problems;
        Assert.Equal(HotkeyStatus.Off, HotkeyStatusRules.Derive(null, [], hookFailed: false));
        Assert.Equal(HotkeyStatus.Off, HotkeyStatusRules.Derive(null, ["'threads' must be an integer"], hookFailed: true));
        Assert.Equal(HotkeyStatus.Invalid, HotkeyStatusRules.Derive(null, invalid, hookFailed: false));
        Assert.Equal(HotkeyStatus.Failed, HotkeyStatusRules.Derive(HotkeySpec.Default, [], hookFailed: true));
        Assert.Equal(HotkeyStatus.Active, HotkeyStatusRules.Derive(HotkeySpec.Default, [], hookFailed: false));
    }

    [Fact]
    public void Tray_lines_name_the_state()
    {
        Assert.Equal("Robo-Paste hotkey: Ctrl+Shift+V…", ToastText.HotkeyTrayLine(HotkeyStatus.Active, HotkeySpec.Default));
        Assert.Equal("Robo-Paste hotkey: off…", ToastText.HotkeyTrayLine(HotkeyStatus.Off, null));
        Assert.Contains("off", ToastText.HotkeyTrayLine(HotkeyStatus.Invalid, null), StringComparison.Ordinal);
        Assert.Contains("invalid", ToastText.HotkeyTrayLine(HotkeyStatus.Invalid, null), StringComparison.Ordinal);
        Assert.Equal("Robo-Paste hotkey: not active…", ToastText.HotkeyTrayLine(HotkeyStatus.Failed, HotkeySpec.Default));
    }

    [Fact]
    public void The_tray_hint_names_the_hotkey_only_when_it_is_on_and_fits_a_balloon()
    {
        var on = ToastText.ForTrayHint(HotkeySpec.Default);
        var off = ToastText.ForTrayHint(null);
        Assert.Contains("Ctrl+Shift+V", on.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+", off.Body, StringComparison.Ordinal);
        Assert.True(on.Body.Length <= ToastText.MaxBalloonText, $"{on.Body.Length} characters");
        var longest = ToastText.ForTrayHint(Valid("Ctrl+Shift+F12"));
        Assert.True(longest.Body.Length <= ToastText.MaxBalloonText, $"{longest.Body.Length} characters");
    }

    [Fact]
    public void The_hotkey_refusals_are_path_free_and_distinct()
    {
        var refusals = new[] { VerbRefusal.ExplorerNotResponding, VerbRefusal.FolderNotIdentified, VerbRefusal.ClipboardNotReady };
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refusal in refusals)
        {
            var toast = ToastText.ForRefusal(refusal);
            Assert.DoesNotContain(@":\", toast.Title + toast.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\\", toast.Title + toast.Body, StringComparison.Ordinal);
            Assert.True(titles.Add(toast.Title));
            Assert.Contains("nothing was pasted", toast.Body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
