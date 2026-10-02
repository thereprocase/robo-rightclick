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

    /// <summary>
    /// Switches users may not pass through extraArgs: they would delete files at
    /// the destination (/MIR, /PURGE), turn the paste into a no-op that still
    /// reports success (/L, /CREATE), write a log behind ephemeral mode's back
    /// (/LOG, /UNILOG, /TEE), or fight flags this class owns.
    /// </summary>
    public static readonly IReadOnlySet<string> ForbiddenSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/MIR", "/PURGE", "/CREATE", "/L", "/NOCOPY",
        "/LOG", "/LOG+", "/UNILOG", "/UNILOG+", "/TEE",
        "/MOV", "/MOVE", "/MT", "/UNICODE", "/NOSD", "/NODD",
        "/JOB", "/SAVE", "/QUIT", "/EFSRAW", "/IPG",
    };

    public static IReadOnlyList<string> FindForbiddenSwitches(string extraArgs)
    {
        var found = new List<string>();
        foreach (var token in extraArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith('/'))
            {
                continue;
            }
            var colon = token.IndexOf(':');
            var name = colon < 0 ? token : token[..colon];
            if (ForbiddenSwitches.Contains(name) && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(name.ToUpperInvariant());
            }
        }
        return found;
    }

    public static string ConflictFlags(ConflictPolicy policy) => policy switch
    {
        // Explorer's "Replace": overwrite even identical, tweaked or
        // otherwise-equal files instead of robocopy's skip-if-same default.
        ConflictPolicy.Replace => "/IS /IT /IM",
        // Explorer's "Skip": never touch a file that already exists.
        ConflictPolicy.Skip => "/XC /XN /XO",
        ConflictPolicy.KeepNewer => "/XO",
        // Ask is resolved to one of the above before a job runs; with no
        // conflicts found, robocopy's defaults already match Explorer.
        ConflictPolicy.Ask => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    public static string Build(RobocopyStep step, Settings settings, ConflictPolicy resolvedPolicy, string logPipeName)
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
        if (!string.IsNullOrWhiteSpace(extra) && FindForbiddenSwitches(extra).Count == 0)
        {
            sb.Append(' ').Append(extra.Trim());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quotes one argument. A trailing backslash would escape the closing
    /// quote under the C runtime's argument rules ("C:\" becomes C:"), so
    /// root paths get a harmless "." appended instead.
    /// </summary>
    public static string Quote(string value)
    {
        var v = value.EndsWith('\\') ? value + "." : value;
        return "\"" + v + "\"";
    }
}
