using System.Text;

namespace RoboRightClick.Core;

/// <summary>
/// Builds robocopy command lines. Output-shaping flags are fixed because the
/// output parser depends on them; behavior flags come from settings.
/// </summary>
public static class RobocopyArgs
{
    /// <summary>Flags that make each output line one file event the parser understands.</summary>
    public const string OutputFlags = "/NP /NDL /NC /NJH /NJS /BYTES /FP";

    /// <summary>
    /// Robocopy's output channel. Redirected stdout cannot carry non-ASCII
    /// names (with /UNICODE it emits a UTF-16 BOM and then narrow text with '?'
    /// substitutions, observed 2026-10-02), so output goes through /UNILOG
    /// into a named pipe the app owns: true UTF-16, and nothing is written to
    /// disk in any logging mode.
    /// </summary>
    public static string LogPipeArgument(string pipeName)
    {
        if (pipeName.Length is 0 or > 200 || !pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("Pipe names are limited to ASCII letters, digits and '-'.", nameof(pipeName));
        }
        return @"/UNILOG:\\.\pipe\" + pipeName;
    }

    /// <summary>
    /// Closest robocopy match to Explorer's copy, measured side by side on
    /// Windows build 26200 (docs/parity.md):
    /// /COPY:DAT keeps data, attributes, streams and modified time, with ACLs
    /// inherited from the destination as Explorer does; /A+:A sets the archive
    /// bit Explorer sets on every copy; /DCOPY:DA gives new folders a fresh
    /// created time; /XJD stops robocopy following junctions and directory
    /// symlinks, which Explorer does not follow either.
    /// </summary>
    public const string MetadataFlags = "/COPY:DAT /DCOPY:DA /A+:A /XJD";

    /// <summary>Longest extraArgs value accepted, per verb.</summary>
    public const int MaxExtraArgsLength = 1_024;

    /// <summary>
    /// The only switches users may add through extraArgs. An allow-list, not a deny-list:
    /// robocopy strips quotes from its arguments, so a deny-list can be bypassed by quoting
    /// ("/MIR"), and any switch that changes which files are selected (/S, /XF, /XO, /MAX,
    /// ...) would make the app's own accounting of planned files wrong, which cancel cleanup
    /// and retry depend on. These change only how each selected file is copied.
    /// Extending this list is a reviewed change.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/J", "/Z", "/SL", "/COMPRESS", "/NOOFFLOAD", "/FFT", "/DST",
    };

    /// <summary>Allowed switches that take a size value, such as /IORATE:50M.</summary>
    public static readonly IReadOnlySet<string> AllowedSizeSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/IORATE", "/IOMAXSIZE", "/THRESHOLD",
    };

    /// <summary>
    /// Problems with an extraArgs value, one per offending token; empty means it is safe to
    /// append. Every token must be a bare '/'-switch from <see cref="AllowedSwitches"/> or
    /// <see cref="AllowedSizeSwitches"/>: no quotes, no '^', no positional tokens (robocopy
    /// would read those as extra file filters).
    /// </summary>
    public static IReadOnlyList<string> ExtraArgProblems(string extraArgs)
    {
        var problems = new List<string>();
        if (extraArgs.Length > MaxExtraArgsLength)
        {
            problems.Add($"longer than {MaxExtraArgsLength} characters");
            return problems;
        }
        foreach (var token in extraArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsAllowedToken(token))
            {
                problems.Add($"'{token}' is not an allowed extra switch");
            }
        }
        return problems;
    }

    /// <remarks>
    /// Exact names and a digits-plus-unit value grammar leave no room for quotes, carets or
    /// other characters robocopy's argument parser would reinterpret.
    /// </remarks>
    private static bool IsAllowedToken(string token)
    {
        var colon = token.IndexOf(':');
        if (colon < 0)
        {
            return AllowedSwitches.Contains(token);
        }
        var value = token[(colon + 1)..];
        var digits = value.Length > 0 && char.IsAsciiLetter(value[^1]) ? value[..^1] : value;
        return AllowedSizeSwitches.Contains(token[..colon])
            && digits.Length is > 0 and <= 12
            && digits.All(char.IsAsciiDigit)
            && (digits.Length == value.Length || "KMGkmg".Contains(value[^1]));
    }

    public static string ConflictFlags(ConflictPolicy policy) => policy switch
    {
        // Explorer's "Replace": overwrite even identical, tweaked or
        // otherwise-equal files instead of robocopy's skip-if-same default.
        ConflictPolicy.Replace => "/IS /IT /IM",
        // Explorer's "Skip": never touch a file that already exists.
        ConflictPolicy.Skip => "/XC /XN /XO",
        // Replace only where the source is newer: exclude older and same-time-but-changed
        // sources. Used as-is only for copies; a cut with KeepNewer is split by
        // ExecutionPlanner so kept files never reach a /MOV run.
        ConflictPolicy.KeepNewer => "/XC /XO",
        // Ask reaches a run only when the scan found no conflicts. Skip flags then make
        // a file that appears at the destination after the scan (another program, the
        // user) get skipped rather than silently overwritten without the prompt.
        ConflictPolicy.Ask => "/XC /XN /XO",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    /// <summary>
    /// One robocopy argument string. Files the user chose to keep are never protected by a
    /// filter here: <see cref="ExecutionPlanner"/> leaves them out of every step by
    /// construction (robocopy /MOV can delete the source of a "same" file it skipped).
    /// </summary>
    public static string Build(
        RobocopyStep step,
        Settings settings,
        ConflictPolicy resolvedPolicy,
        string logPipeName)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(step.SourceDirectory)).Append(' ').Append(Quote(step.DestinationDirectory));
        foreach (var name in step.FileNames)
        {
            sb.Append(' ').Append(Quote(name));
        }

        if (step.Recursive)
        {
            sb.Append(" /E");
        }
        if (step.Move)
        {
            // Robocopy deletes each source only after that file copied
            // successfully, so a failed file always keeps its original.
            sb.Append(step.Recursive ? " /MOVE" : " /MOV");
        }

        sb.Append(" /MT:").Append(settings.Threads);
        sb.Append(" /R:").Append(settings.Retries);
        sb.Append(" /W:").Append(settings.RetryWaitSeconds);
        sb.Append(' ').Append(MetadataFlags);
        sb.Append(' ').Append(OutputFlags);
        sb.Append(' ').Append(LogPipeArgument(logPipeName));

        var conflict = ConflictFlags(resolvedPolicy);
        if (conflict.Length > 0)
        {
            sb.Append(' ').Append(conflict);
        }

        var extra = step.Move ? settings.ExtraArgs.Move : settings.ExtraArgs.Copy;
        if (!string.IsNullOrWhiteSpace(extra) && ExtraArgProblems(extra).Count == 0)
        {
            sb.Append(' ').Append(extra.Trim());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quotes one argument. A trailing backslash would escape the closing
    /// quote under the C runtime's argument rules ("C:\" becomes C:"), so
    /// root paths get a harmless "." appended instead. A value containing a quote or a
    /// control character is refused outright: there is no escaping that robocopy's parser
    /// is known to honor, and <see cref="PathPolicy"/> never lets such a path through.
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Any(c => c == '"' || c < 0x20))
        {
            throw new ArgumentException("Robocopy arguments cannot contain quotes or control characters.", nameof(value));
        }
        var v = value.EndsWith('\\') ? value + "." : value;
        return "\"" + v + "\"";
    }

    /// <summary>CreateProcess's limit for the whole command line, including the terminating NUL.</summary>
    public const int MaxCommandLineLength = 32_767;

    /// <summary>
    /// Length of the command line CreateProcess will receive: quoted exe, a space, the
    /// arguments and the NUL. A run whose line does not fit must fail before starting.
    /// </summary>
    public static int CommandLineLength(string exePath, string arguments) => Quote(exePath).Length + 1 + arguments.Length + 1;
}
