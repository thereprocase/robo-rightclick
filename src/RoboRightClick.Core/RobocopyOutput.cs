using System.Globalization;
using System.Text.RegularExpressions;

namespace RoboRightClick.Core;

public abstract record RobocopyEvent;

/// <summary>A per-file line: with /NC /FP /BYTES it is "&lt;size&gt;&lt;tab&gt;&lt;full path&gt;".</summary>
public sealed record FileReported(long Size, string Path) : RobocopyEvent;

/// <summary>A failed operation; robocopy prints the system message on the following line.</summary>
public sealed record ErrorReported(int Code, string Operation, string Path, string Message) : RobocopyEvent;

public sealed record OtherOutput(string Text) : RobocopyEvent;

/// <summary>
/// Incremental parser for robocopy stdout produced with
/// <see cref="RobocopyArgs.OutputFlags"/>. Feed it one line at a time.
/// </summary>
/// <remarks>
/// Line shapes come from robocopy's documented output. Fixtures captured from
/// real /MT:32 runs on Windows replace the hand-written ones once M0 records
/// them; until then the start/finish timing of file lines under /MT is unknown.
/// </remarks>
public sealed partial class RobocopyOutputParser
{
    private (int Code, string Operation, string Path)? _pendingError;

    [GeneratedRegex(@"^\s*(\d+)\t(.+)$")]
    private static partial Regex FileLine();

    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} ERROR (\d+) \(0x[0-9A-Fa-f]+\) (.+?) ([A-Za-z]:\\.*|\\\\.*)$")]
    private static partial Regex ErrorLine();

    /// <summary>Returns the events completed by this line (zero or more).</summary>
    public IReadOnlyList<RobocopyEvent> Feed(string line)
    {
        var text = line.TrimStart('﻿').TrimEnd('\r', '\n');
        var events = new List<RobocopyEvent>();

        if (_pendingError is { } pending)
        {
            if (text.Trim().Length == 0)
            {
                return events;
            }
            _pendingError = null;
            if (!ErrorLine().IsMatch(text) && !FileLine().IsMatch(text))
            {
                events.Add(new ErrorReported(pending.Code, pending.Operation, pending.Path, text.Trim()));
                return events;
            }
            // The message line never arrived; report the error without it and
            // fall through so this line is not lost.
            events.Add(new ErrorReported(pending.Code, pending.Operation, pending.Path, string.Empty));
        }

        var error = ErrorLine().Match(text);
        if (error.Success)
        {
            _pendingError = (
                int.Parse(error.Groups[1].Value, CultureInfo.InvariantCulture),
                error.Groups[2].Value,
                error.Groups[3].Value.TrimEnd());
            return events;
        }

        var file = FileLine().Match(text);
        if (file.Success && long.TryParse(file.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            events.Add(new FileReported(size, file.Groups[2].Value.TrimEnd()));
            return events;
        }

        if (text.Trim().Length > 0)
        {
            events.Add(new OtherOutput(text.Trim()));
        }
        return events;
    }

    /// <summary>Call when the process exits, to flush an error whose message line never came.</summary>
    public IReadOnlyList<RobocopyEvent> Complete()
    {
        if (_pendingError is not { } pending)
        {
            return [];
        }
        _pendingError = null;
        return [new ErrorReported(pending.Code, pending.Operation, pending.Path, string.Empty)];
    }
}

/// <summary>Robocopy's exit code is a bit field; 8 and above means something failed.</summary>
public readonly record struct RobocopyExitCode(int Value)
{
    public bool FilesCopied => Value >= 0 && (Value & 1) != 0;
    public bool ExtraFilesAtDestination => Value >= 0 && (Value & 2) != 0;
    public bool MismatchesDetected => Value >= 0 && (Value & 4) != 0;
    public bool SomeCopiesFailed => Value < 0 || (Value & 8) != 0;
    public bool FatalError => Value < 0 || (Value & 16) != 0;

    /// <summary>Negative codes mean the process was killed or never ran properly.</summary>
    public bool IsFailure => Value < 0 || Value >= 8;
}
