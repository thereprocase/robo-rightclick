using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoboRightClick.Core;

public enum ConflictPolicy
{
    /// <summary>Show Explorer's Replace / Skip / Let me decide choice before copying.</summary>
    Ask,
    Replace,
    Skip,
    KeepNewer,
}

public enum LoggingMode
{
    Normal,

    /// <summary>Nothing about any job is written to disk.</summary>
    Ephemeral,
}

public sealed record ExtraArgs(string Copy, string Move)
{
    public static readonly ExtraArgs None = new(string.Empty, string.Empty);
}

/// <summary>
/// User configuration. Defaults reproduce Explorer's paste behavior, except that robocopy
/// copies in parallel, with a thread count chosen per drive (<see cref="ThreadPolicy"/>).
/// </summary>
public sealed record Settings
{
    public const int MinThreads = 1;
    public const int MaxThreads = 128; // robocopy's own /MT ceiling

    /// <summary>
    /// "threads": "auto" in config.json (the default): each robocopy run gets the thread
    /// count <see cref="ThreadPolicy"/> picks for its source and destination drives.
    /// </summary>
    public bool AutoThreads { get; init; } = true;

    /// <summary>The fixed /MT count when <see cref="AutoThreads"/> is off ("threads": n).</summary>
    public int Threads { get; init; } = ThreadPolicy.Fallback;

    // Explorer never retries silently; it stops and asks. Zero retries keeps
    // that behavior, with failures collected for an end-of-job "Try again".
    public int Retries { get; init; }
    public int RetryWaitSeconds { get; init; }

    public ConflictPolicy ConflictDefault { get; init; } = ConflictPolicy.Ask;

    /// <summary>0 means unlimited: every paste starts at once, as in Explorer.</summary>
    public int MaxConcurrentJobs { get; init; }

    public LoggingMode Logging { get; init; } = LoggingMode.Normal;
    public int LogRetentionJobs { get; init; } = 100;
    public bool StartWithWindows { get; init; } = true;
    public bool NotifyOnComplete { get; init; } = true;

    /// <summary>
    /// Open a progress window for each paste, as Explorer opens its copy dialog. Without
    /// it the only feedback is a tray icon that Windows 11 hides in the overflow by default.
    /// </summary>
    public bool ShowProgressWindow { get; init; } = true;

    public ExtraArgs ExtraArgs { get; init; } = ExtraArgs.None;

    /// <summary>
    /// The Robo-Paste keyboard shortcut in File Explorer and on the desktop; null is off.
    /// Unlike every other field, an invalid value means off, not the default (see
    /// <see cref="SettingsSerializer"/>): the default would switch on a keyboard hook the
    /// user may have been trying to switch off.
    /// </summary>
    public HotkeySpec? PasteHotkey { get; init; } = HotkeySpec.Default;

    public static readonly Settings Default = new();
}

public sealed record SettingsLoadResult(Settings Settings, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// The text was not a JSON object at all, so every field is a default. Only then does the
    /// next save keep a config.json.bad copy: per-field fallback already keeps everything
    /// else in a readable file, but an unreadable one would be overwritten by defaults and lost.
    /// </summary>
    public bool Unreadable { get; init; }

    /// <summary>
    /// The format version the file declares; a file without one is version 1. Null when the
    /// field is present but not a positive integer this version can read ("2", 2.0, 0, a
    /// number past int.MaxValue): such a file may come from any version.
    /// </summary>
    public int? Version { get; init; } = SettingsSerializer.CurrentVersion;

    /// <summary>
    /// The file comes from a newer RoboRightClick (after a downgrade). Its known fields are
    /// loaded, but nothing may save over it: this version would drop the fields it does not
    /// know, and the newer version would lose them.
    /// </summary>
    public bool WrittenByNewerVersion => Version > SettingsSerializer.CurrentVersion;

    /// <summary>
    /// Nothing may save over this file: it comes from a newer version, or its version cannot
    /// be read and so it may. Refusing costs one manual step (fix the field or delete the
    /// file); saving over a newer file loses its settings for good. This matches job.json,
    /// where an unreadable version also counts as newer (<see cref="JobRecords.IsNewerVersion"/>).
    /// </summary>
    public bool SavesRefused => Version is not { } version || version > SettingsSerializer.CurrentVersion;
}

/// <summary>
/// Reads and writes the config file format. Each bad field falls back to its
/// default independently, so one typo never discards the rest of the file.
/// </summary>
public static class SettingsSerializer
{
    /// <summary>
    /// The config.json format this build writes, as its "version" field. Raise it when a
    /// change means an older build would misread or lose something on save; an older build
    /// then loads what it knows and refuses to save over the file.
    /// </summary>
    /// <remarks>
    /// 2: "threads": n means a count the user chose. In format 1 the installer wrote
    /// <see cref="LegacyDefaultThreads"/> into every new config.json, so format 1's 32 is read
    /// as "auto" (<see cref="ReadThreads"/>); any other format-1 count stays fixed.
    /// </remarks>
    public const int CurrentVersion = 2;

    /// <summary>
    /// The thread count format 1 wrote as its default, before "auto" existed. Builds before the
    /// per-drive choice serialized it into every config.json, so it says nothing about the
    /// user's wish.
    /// </summary>
    public const int LegacyDefaultThreads = 32;

    public const string VersionKey = "version";

    /// <summary>Why a save was refused (<see cref="MayOverwrite"/>); path-free, shown in a message box or the Settings window.</summary>
    public const string NewerVersionSaveRefusal =
        "config.json was written by a newer version of RoboRightClick, or its \"version\" field is damaged, "
        + "so this version does not save over it. Install the newer version to change settings, "
        + "or delete config.json to start again from the defaults.";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SettingsLoadResult Parse(string json)
    {
        var problems = new List<string>();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            return UnreadableResult($"config is not valid JSON ({ex.Message}); using defaults");
        }
        if (root is null)
        {
            return UnreadableResult("config is not a JSON object; using defaults");
        }

        var version = ReadVersion(root);
        var savesRefused = version is not { } known || known > CurrentVersion;
        if (version is null)
        {
            problems.Add($"'{VersionKey}' is not a format version this one reads (a positive integer); "
                + "the settings this version knows are used and the file is left unchanged");
        }
        else if (version > CurrentVersion)
        {
            problems.Add($"written by a newer version of RoboRightClick (format {version}, this one reads {CurrentVersion}); "
                + "the settings this version knows are used and the file is left unchanged");
        }

        var d = Settings.Default;
        var s = d with
        {
            AutoThreads = ReadThreads(root, d, version, problems, out var threads),
            Threads = threads,
            Retries = ReadInt(root, "retries", d.Retries, 0, 1_000, problems),
            RetryWaitSeconds = ReadInt(root, "retryWaitSeconds", d.RetryWaitSeconds, 0, 3_600, problems),
            ConflictDefault = ReadEnum(root, "conflictDefault", d.ConflictDefault, problems),
            MaxConcurrentJobs = ReadInt(root, "maxConcurrentJobs", d.MaxConcurrentJobs, 0, 64, problems),
            Logging = ReadEnum(root, "logging", d.Logging, problems),
            LogRetentionJobs = ReadInt(root, "logRetentionJobs", d.LogRetentionJobs, 1, 10_000, problems),
            StartWithWindows = ReadBool(root, "startWithWindows", d.StartWithWindows, problems),
            NotifyOnComplete = ReadBool(root, "notifyOnComplete", d.NotifyOnComplete, problems),
            ShowProgressWindow = ReadBool(root, "showProgressWindow", d.ShowProgressWindow, problems),
            ExtraArgs = ReadExtraArgs(root, problems),
            PasteHotkey = ReadPasteHotkey(root, problems),
        };

        // A newer file is expected to hold settings this version does not know, and so may a
        // file whose version cannot be read; the one problem about its version covers them.
        if (!savesRefused)
        {
            foreach (var property in root)
            {
                if (!KnownKeys.Contains(property.Key))
                {
                    problems.Add($"unknown setting '{property.Key}' ignored");
                }
            }
        }
        return new(s, problems) { Version = version };
    }

    /// <summary>
    /// Whether a save may replace the config text now on disk (null: no file). False for a
    /// file whose version is newer or unreadable (<see cref="SettingsLoadResult.SavesRefused"/>);
    /// a file that is not JSON at all may be replaced, because the store keeps a .bad copy
    /// of it first.
    /// </summary>
    public static bool MayOverwrite(string? existingText) =>
        existingText is null || !Parse(existingText).SavesRefused;

    /// <summary>
    /// The settings store's decision before every save. The file as it is now decides
    /// (<paramref name="textOnDisk"/>, null when there is none): a newer version may have
    /// written it since the last load, and a user may have deleted a newer file to start over.
    /// Only when it exists but cannot be read does the last load decide
    /// (<paramref name="lastLoadRefusedSaves"/>, its <see cref="SettingsLoadResult.SavesRefused"/>).
    /// </summary>
    public static bool MaySave(string? textOnDisk, bool onDiskReadFailed, bool lastLoadRefusedSaves) =>
        onDiskReadFailed ? !lastLoadRefusedSaves : MayOverwrite(textOnDisk);

    public static string Serialize(Settings s)
    {
        var root = new JsonObject
        {
            [VersionKey] = CurrentVersion,
            ["threads"] = s.AutoThreads ? JsonValue.Create(ThreadsAuto) : JsonValue.Create(s.Threads),
            ["retries"] = s.Retries,
            ["retryWaitSeconds"] = s.RetryWaitSeconds,
            ["conflictDefault"] = ToJsonName(s.ConflictDefault),
            ["maxConcurrentJobs"] = s.MaxConcurrentJobs,
            ["logging"] = ToJsonName(s.Logging),
            ["logRetentionJobs"] = s.LogRetentionJobs,
            ["startWithWindows"] = s.StartWithWindows,
            ["notifyOnComplete"] = s.NotifyOnComplete,
            ["showProgressWindow"] = s.ShowProgressWindow,
            ["extraArgs"] = new JsonObject
            {
                ["copy"] = s.ExtraArgs.Copy,
                ["move"] = s.ExtraArgs.Move,
            },
            [PasteHotkeyKey] = s.PasteHotkey?.Format() ?? string.Empty,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        VersionKey, "threads", "retries", "retryWaitSeconds", "conflictDefault", "maxConcurrentJobs",
        "logging", "logRetentionJobs", "startWithWindows", "notifyOnComplete", "showProgressWindow", "extraArgs",
        PasteHotkeyKey,
    };

    /// <summary>
    /// The Robo-Paste hotkey. Added without a format version change: an older build reports
    /// it as an unknown setting and drops it when it saves, and a file without it means the
    /// default (<see cref="HotkeySpec.Default"/>), so an upgrade turns the hotkey on.
    /// </summary>
    public const string PasteHotkeyKey = "pasteHotkey";

    /// <summary>The end of every "pasteHotkey" problem: unlike other fields it falls back to off.</summary>
    public const string PasteHotkeyOffSuffix = "; the hotkey is off";

    /// <summary>The second problem of an unreadable file (<see cref="UnreadableResult"/>).</summary>
    public const string PasteHotkeyUnreadableProblem =
        $"'{PasteHotkeyKey}' is unknown because the file could not be read{PasteHotkeyOffSuffix}";

    /// <summary>
    /// What a config.json that is not a JSON object, or that exists but could not be read from
    /// disk, loads as: every default except the hotkey, which is off. Its default is a keyboard
    /// hook, and the damaged file may be one that switched it off; a fallback must never switch
    /// on a hook the user may have switched off, as for an invalid "pasteHotkey" alone. The
    /// second problem names the setting, so the tray says "off (setting invalid)" rather than
    /// "off", and Settings marks the field.
    /// </summary>
    /// <param name="problem">Why the file could not be used, path-free.</param>
    public static SettingsLoadResult UnreadableResult(string problem) =>
        new(Settings.Default with { PasteHotkey = null }, [problem, PasteHotkeyUnreadableProblem]) { Unreadable = true };

    /// <summary>A load problem about "pasteHotkey" (the tray then says the hotkey is off because of it).</summary>
    public static bool IsPasteHotkeyProblem(string problem) =>
        problem.StartsWith($"'{PasteHotkeyKey}'", StringComparison.Ordinal);

    private static string ToJsonName<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The config text for automatic, per-drive thread counts.</summary>
    public const string ThreadsAuto = "auto";

    /// <summary>
    /// "version": missing means 1 (files from before the field existed). Present but not a
    /// positive JSON integer within int range (null, a string, 2.0, 0, 3000000000) means
    /// null: unlike other fields it does not fall back to a default, because the default
    /// would allow saving over a file that a newer version may have written.
    /// </summary>
    private static int? ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue(VersionKey, out var node))
        {
            return 1;
        }
        return node is JsonValue v
            && v.GetValueKind() == JsonValueKind.Number
            && v.TryGetValue<int>(out var version)
            && version >= 1
                ? version
                : null;
    }

    /// <summary>
    /// "threads" is either "auto" or a fixed count; anything else falls back to auto. A format-1
    /// file's <see cref="LegacyDefaultThreads"/> is the old default, not a choice, and reads as
    /// auto, so an update brings the per-drive default to everyone who never set the field; the
    /// next save writes "auto" with the current version.
    /// </summary>
    private static bool ReadThreads(JsonObject root, Settings defaults, int? version, List<string> problems, out int threads)
    {
        threads = defaults.Threads;
        if (!root.TryGetPropertyValue("threads", out var node) || node is null)
        {
            return defaults.AutoThreads;
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text) && string.Equals(text.Trim(), ThreadsAuto, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (v.TryGetValue<int>(out var n) && n >= Settings.MinThreads && n <= Settings.MaxThreads)
            {
                if (version == 1 && n == LegacyDefaultThreads)
                {
                    return true;
                }
                threads = n;
                return false;
            }
        }
        problems.Add($"'threads' must be \"{ThreadsAuto}\" or an integer from {Settings.MinThreads} to {Settings.MaxThreads}; using \"{ThreadsAuto}\"");
        return true;
    }

    private static int ReadInt(JsonObject root, string key, int fallback, int min, int max, List<string> problems)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue<int>(out var i) && i >= min && i <= max)
        {
            return i;
        }
        problems.Add($"'{key}' must be an integer from {min} to {max}; using {fallback}");
        return fallback;
    }

    private static bool ReadBool(JsonObject root, string key, bool fallback, List<string> problems)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue<bool>(out var b))
        {
            return b;
        }
        problems.Add($"'{key}' must be true or false; using {fallback.ToString().ToLowerInvariant()}");
        return fallback;
    }

    private static T ReadEnum<T>(JsonObject root, string key, T fallback, List<string> problems) where T : struct, Enum
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var text)
            && Enum.TryParse<T>(text, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && !int.TryParse(text, out _))
        {
            return parsed;
        }
        var allowed = string.Join(" | ", Enum.GetValues<T>().Select(ToJsonName));
        problems.Add($"'{key}' must be one of {allowed}; using {ToJsonName(fallback)}");
        return fallback;
    }

    /// <summary>
    /// "pasteHotkey": missing means the default; "" means off. Anything else that is not a
    /// valid combination (a non-string, null, over 32 characters, a reserved or malformed
    /// one) also means off, with a problem naming the setting. This is the one field that
    /// does not fall back to its default, like "version": the default here is a keyboard hook,
    /// and a typo must not switch on what the user meant to switch off.
    /// </summary>
    private static HotkeySpec? ReadPasteHotkey(JsonObject root, List<string> problems)
    {
        if (!root.TryGetPropertyValue(PasteHotkeyKey, out var node))
        {
            return HotkeySpec.Default;
        }
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String || !v.TryGetValue<string>(out var text))
        {
            problems.Add($"'{PasteHotkeyKey}' must be a shortcut such as \"{HotkeySpec.DefaultText}\", or \"\" for none{PasteHotkeyOffSuffix}");
            return null;
        }
        var (spec, problem) = HotkeySpec.Parse(text);
        if (problem is not null)
        {
            problems.Add($"'{PasteHotkeyKey}' {problem}{PasteHotkeyOffSuffix}");
        }
        return spec;
    }

    private static ExtraArgs ReadExtraArgs(JsonObject root, List<string> problems)
    {
        if (!root.TryGetPropertyValue("extraArgs", out var node) || node is null)
        {
            return ExtraArgs.None;
        }
        if (node is not JsonObject obj)
        {
            problems.Add("'extraArgs' must be an object with 'copy' and 'move' strings; ignored");
            return ExtraArgs.None;
        }
        return new ExtraArgs(
            ReadExtraArgString(obj, "copy", problems),
            ReadExtraArgString(obj, "move", problems));
    }

    private static string ReadExtraArgString(JsonObject obj, string key, List<string> problems)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return string.Empty;
        }
        if (node is not JsonValue v || !v.TryGetValue<string>(out var text))
        {
            problems.Add($"'extraArgs.{key}' must be a string; ignored");
            return string.Empty;
        }
        var rejected = RobocopyArgs.ExtraArgProblems(text);
        if (rejected.Count > 0)
        {
            problems.Add($"'extraArgs.{key}' ignored: {string.Join("; ", rejected)}");
            return string.Empty;
        }
        return text.Trim();
    }
}
