using System.Globalization;
using System.Text.RegularExpressions;

namespace RoboRightClick.Core;

public abstract record RobocopyEvent;

/// <summary>A file robocopy finished copying (its size in bytes and full source path).</summary>
public sealed record FileReported(long Size, string Path) : RobocopyEvent;

/// <summary>A failed operation; robocopy prints the system message on the following line.</summary>
public sealed record ErrorReported(int Code, string Operation, string Path, string Message) : RobocopyEvent;

public sealed record OtherOutput(string Text) : RobocopyEvent;

/// <summary>
/// Incremental parser for robocopy's /UNILOG output produced with
/// <see cref="RobocopyArgs.OutputFlags"/>. Feed it one line at a time.
/// </summary>
/// <remarks>
/// Observed on Windows build 26200 with /MT:32 (docs/testlog.md, 2026-10-02):
/// a file's line is printed when robocopy is done with that file, and a file
/// that fails still gets its line first, immediately followed by the ERROR
/// line for the same path. So a file line is held back until the next line
/// shows it was not a failure.
/// </remarks>
public sealed partial class RobocopyOutputParser
{
    private FileReported? _pendingFile;
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
        var blank = text.Trim().Length == 0;

        if (_pendingError is { } pending)
        {
            if (blank)
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

        if (blank)
        {
            return events;
        }

        var error = ErrorLine().Match(text);
        if (error.Success)
        {
            var path = error.Groups[3].Value.TrimEnd();
            if (_pendingFile is { } failed && WinPath.Comparer.Equals(failed.Path, path))
            {
                // The held file line belonged to this failure; it was not copied.
                _pendingFile = null;
            }
            FlushFile(events);
            _pendingError = (int.Parse(error.Groups[1].Value, CultureInfo.InvariantCulture), error.Groups[2].Value, path);
            return events;
        }

        var file = FileLine().Match(text);
        if (file.Success && long.TryParse(file.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            FlushFile(events);
            _pendingFile = new FileReported(size, file.Groups[2].Value.TrimEnd());
            return events;
        }

        FlushFile(events);
        events.Add(new OtherOutput(text.Trim()));
        return events;
    }

    /// <summary>Call when the output ends, to flush anything still held back.</summary>
    public IReadOnlyList<RobocopyEvent> Complete()
    {
        var events = new List<RobocopyEvent>();
        if (_pendingError is { } pending)
        {
            _pendingError = null;
            events.Add(new ErrorReported(pending.Code, pending.Operation, pending.Path, string.Empty));
        }
        FlushFile(events);
        return events;
    }

    private void FlushFile(List<RobocopyEvent> events)
    {
        if (_pendingFile is { } file)
        {
            _pendingFile = null;
            events.Add(file);
        }
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
