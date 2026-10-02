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
/// User configuration. Defaults reproduce Explorer's paste behavior, except
/// that robocopy runs with 32 threads.
/// </summary>
public sealed record Settings
{
    public const int MinThreads = 1;
    public const int MaxThreads = 128; // robocopy's own /MT ceiling

    public int Threads { get; init; } = 32;

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
    public ExtraArgs ExtraArgs { get; init; } = ExtraArgs.None;

    public static readonly Settings Default = new();
}

public sealed record SettingsLoadResult(Settings Settings, IReadOnlyList<string> Problems);

/// <summary>
/// Reads and writes the config file format. Each bad field falls back to its
/// default independently, so one typo never discards the rest of the file.
/// </summary>
public static class SettingsSerializer
{
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
            return new(Settings.Default, [$"config is not valid JSON ({ex.Message}); using defaults"]);
        }
        if (root is null)
        {
            return new(Settings.Default, ["config is not a JSON object; using defaults"]);
        }

        var d = Settings.Default;
        var s = d with
        {
            Threads = ReadInt(root, "threads", d.Threads, Settings.MinThreads, Settings.MaxThreads, problems),
            Retries = ReadInt(root, "retries", d.Retries, 0, 1_000, problems),
            RetryWaitSeconds = ReadInt(root, "retryWaitSeconds", d.RetryWaitSeconds, 0, 3_600, problems),
            ConflictDefault = ReadEnum(root, "conflictDefault", d.ConflictDefault, problems),
            MaxConcurrentJobs = ReadInt(root, "maxConcurrentJobs", d.MaxConcurrentJobs, 0, 64, problems),
            Logging = ReadEnum(root, "logging", d.Logging, problems),
            LogRetentionJobs = ReadInt(root, "logRetentionJobs", d.LogRetentionJobs, 1, 10_000, problems),
            StartWithWindows = ReadBool(root, "startWithWindows", d.StartWithWindows, problems),
            NotifyOnComplete = ReadBool(root, "notifyOnComplete", d.NotifyOnComplete, problems),
            ExtraArgs = ReadExtraArgs(root, problems),
        };

        foreach (var property in root)
        {
            if (!KnownKeys.Contains(property.Key))
            {
                problems.Add($"unknown setting '{property.Key}' ignored");
            }
        }
        return new(s, problems);
    }

    public static string Serialize(Settings s)
    {
        var root = new JsonObject
        {
            ["threads"] = s.Threads,
            ["retries"] = s.Retries,
            ["retryWaitSeconds"] = s.RetryWaitSeconds,
            ["conflictDefault"] = ToJsonName(s.ConflictDefault),
            ["maxConcurrentJobs"] = s.MaxConcurrentJobs,
            ["logging"] = ToJsonName(s.Logging),
            ["logRetentionJobs"] = s.LogRetentionJobs,
            ["startWithWindows"] = s.StartWithWindows,
            ["notifyOnComplete"] = s.NotifyOnComplete,
            ["extraArgs"] = new JsonObject
            {
                ["copy"] = s.ExtraArgs.Copy,
                ["move"] = s.ExtraArgs.Move,
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "threads", "retries", "retryWaitSeconds", "conflictDefault", "maxConcurrentJobs",
        "logging", "logRetentionJobs", "startWithWindows", "notifyOnComplete", "extraArgs",
    };

    private static string ToJsonName<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
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
        var rejected = RobocopyArgs.FindForbiddenSwitches(text);
        if (rejected.Count > 0)
        {
            problems.Add($"'extraArgs.{key}' contains {string.Join(", ", rejected)}, which would break paste semantics or the logging guarantee; ignored");
            return string.Empty;
        }
        return text.Trim();
    }
}
