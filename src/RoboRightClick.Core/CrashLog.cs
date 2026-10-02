using System.Globalization;
using System.Text;

namespace RoboRightClick.Core;

/// <summary>One exception in a crash: the thrown one, or one of its inner exceptions.</summary>
/// <param name="TypeName">The exception's full type name.</param>
/// <param name="Message">The message as thrown; <see cref="CrashLog.Format"/> scrubs it.</param>
/// <param name="StackTrace">The stack as the runtime reports it, or null when it was never thrown.</param>
public sealed record CrashLayer(string TypeName, string Message, string? StackTrace);

/// <param name="At">When the crash was handled (written as UTC).</param>
/// <param name="AppVersion">The informational version, "1.0.0-beta.1".</param>
/// <param name="OsBuild">Windows' version and build, "10.0.26200".</param>
/// <param name="UiThread">The UI thread (Application.ThreadException) or another thread (AppDomain.UnhandledException).</param>
public sealed record CrashFacts(
    DateTimeOffset At,
    string AppVersion,
    string OsBuild,
    bool UiThread,
    IReadOnlyList<CrashLayer> Layers);

/// <summary>
/// %LOCALAPPDATA%\RoboRightClick\crash.log: one entry per unhandled exception in the tray, so
/// a beta bug report can carry the actual exception instead of a remembered message box.
/// Normal mode only (<see cref="MayWrite"/>). An entry holds exception types, messages with
/// path-looking parts replaced (<see cref="PathHeuristic.Scrub"/>), stack traces, the app
/// version, the UTC time and the Windows build; no job data. The host appends it and rotates
/// the file to crash.1.log (one kept) when it would grow past <see cref="RotateAtBytes"/>.
/// </summary>
public static class CrashLog
{
    public const string FileName = "crash.log";
    public const string RotatedFileName = "crash.1.log";

    /// <summary>crash.log is rotated before an append would take it past this size.</summary>
    public const long RotateAtBytes = 256 * 1024;

    /// <summary>
    /// Characters kept per entry, so one entry is always well under <see cref="RotateAtBytes"/>
    /// in UTF-8 (at most three bytes per UTF-16 unit) and the log stays bounded.
    /// </summary>
    public const int MaxEntryChars = 32 * 1024;

    /// <summary>Exceptions written per entry: the thrown one and its inner exceptions, outermost first.</summary>
    public const int MaxLayers = 8;

    /// <summary>Characters kept of one exception's message, after scrubbing.</summary>
    public const int MaxMessageChars = 1_000;

    /// <summary>
    /// Entries one process writes. A crash loop on the UI thread (the app survives a
    /// ThreadException) would otherwise rotate the first, most useful entry out of the log.
    /// </summary>
    public const int MaxEntriesPerRun = 20;

    /// <summary>
    /// Only in normal mode, and only while no ephemeral job has existed in this session: an
    /// exception message can carry a job's path, and the scrubbing is a heuristic. Unknown
    /// mode (no tray running, or the question itself failed) means no log.
    /// </summary>
    /// <param name="currentMode">The logging setting now, or null when it cannot be known.</param>
    /// <param name="ephemeralJobsThisSession">Any ephemeral job created since the tray started, finished or not.</param>
    public static bool MayWrite(LoggingMode? currentMode, bool ephemeralJobsThisSession) =>
        currentMode == LoggingMode.Normal && !ephemeralJobsThisSession;

    /// <summary>Rotate before appending when the existing file plus the entry would exceed <see cref="RotateAtBytes"/>.</summary>
    public static bool ShouldRotate(long existingBytes, long entryBytes) =>
        existingBytes > 0 && existingBytes + entryBytes > RotateAtBytes;

    /// <summary>
    /// The thrown exception and its inner exceptions (every one of an AggregateException's),
    /// outermost first, at most <see cref="MaxLayers"/>.
    /// </summary>
    public static IReadOnlyList<CrashLayer> LayersOf(Exception exception)
    {
        var layers = new List<CrashLayer>();
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        while (pending.Count > 0 && layers.Count < MaxLayers)
        {
            var current = pending.Dequeue();
            layers.Add(new CrashLayer(current.GetType().FullName ?? current.GetType().Name, current.Message, current.StackTrace));
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }
        return layers;
    }

    /// <summary>
    /// One entry, ending in a blank line: a header line (UTC time, version, Windows build,
    /// thread), then per exception its type, its scrubbed message and its stack trace.
    /// At most <see cref="MaxEntryChars"/> characters.
    /// </summary>
    public static string Format(CrashFacts facts)
    {
        var entry = new StringBuilder();
        entry.Append("=== ")
            .Append(facts.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            .Append(" | ").Append(AppInfo.Name).Append(' ').Append(OneLine(facts.AppVersion))
            .Append(" | Windows ").Append(OneLine(facts.OsBuild))
            .Append(" | thread ").Append(facts.UiThread ? "UI" : "worker")
            .Append('\n');

        for (var i = 0; i < facts.Layers.Count && i < MaxLayers; i++)
        {
            var layer = facts.Layers[i];
            entry.Append(i == 0 ? string.Empty : "--- inner: ")
                .Append(OneLine(layer.TypeName))
                .Append(": ")
                .Append(PathHeuristic.Scrub(layer.Message, MaxMessageChars))
                .Append('\n');
            if (!string.IsNullOrEmpty(layer.StackTrace))
            {
                foreach (var line in layer.StackTrace.Split('\n'))
                {
                    var trimmed = OneLine(line).TrimEnd();
                    if (trimmed.Length > 0)
                    {
                        entry.Append(trimmed).Append('\n');
                    }
                }
            }
        }

        // Room is kept for the blank line that ends the entry.
        if (entry.Length > MaxEntryChars - 1)
        {
            entry.Length = MaxEntryChars - 3;
            entry.Append("…\n");
        }
        entry.Append('\n');
        return entry.ToString();
    }

    /// <summary>Control characters (a stray CR, a NUL) become spaces, so each part stays on its line.</summary>
    private static string OneLine(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }
}
