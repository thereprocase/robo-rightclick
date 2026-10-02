using System.Buffers.Binary;

namespace RoboRightClick.Core;

/// <summary>The byte range of each name in a narrow DROPFILES block, before code-page conversion.</summary>
public sealed record AnsiDropFiles(IReadOnlyList<Range> Names, DropFilesStatus Status)
{
    public static readonly AnsiDropFiles TooLarge = new([], DropFilesStatus.TooLarge);
}

/// <summary>
/// The documented size limits of one selection handed to a verb, by Explorer or by the CLI
/// (which takes the same COM path). The selection may come from any same-user process, so
/// its size is never trusted: the host checks each limit before copying or keeping anything,
/// and refuses the whole selection with <see cref="VerbRefusal.SelectionTooLarge"/>.
/// The limits are the clipboard's, so a selection that is accepted also fits the CF_HDROP
/// block Robo-Copy writes and Robo-Paste reads back.
/// </summary>
public static class SelectionLimits
{
    /// <summary>Most items one click accepts (the CF_HDROP path limit).</summary>
    public const int MaxItems = ClipboardPayload.MaxDropFilesPaths;

    /// <summary>Largest CF_HDROP block read from the selection's data object.</summary>
    public const int MaxBlockBytes = ClipboardPayload.MaxDropFilesBytes;

    /// <summary>Longest single path Windows accepts, in UTF-16 units.</summary>
    public const int MaxPathChars = 32_767;

    /// <summary>All paths together, in UTF-16 units: the CF_HDROP byte budget.</summary>
    public const long MaxTotalChars = MaxBlockBytes / 2;

    /// <summary>The shell reports more items than one click accepts.</summary>
    public static bool TooManyItems(long count) => count > MaxItems;

    /// <summary>A CF_HDROP block (from the selection's data object) larger than is read.</summary>
    public static bool BlockTooLarge(ulong bytes) => bytes > MaxBlockBytes;

    /// <summary>A path read one item at a time is too long, or brings the running total past the budget.</summary>
    public static bool PathTooLong(int pathChars, long totalCharsIncludingThis) =>
        pathChars > MaxPathChars || totalCharsIncludingThis > MaxTotalChars;
}

/// <summary>
/// The decisions the verb dispatcher makes that need no Windows: which clicks are refused,
/// which paste is Explorer's no-op, how a clipboard read is classified and how long the
/// clipboard open is retried. Pure string and number logic: the dispatcher runs on the UI
/// thread, which also serves every COM call, so none of it may touch the file system.
/// </summary>
public static class VerbRules
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Robo-Copy / Robo-Cut. The selection is untrusted (any same-user COM client can send
    /// one), so each path goes through <see cref="PathPolicy"/> before it can be written to
    /// the clipboard, where another program would read it back as a file list. Items with no
    /// file-system path refuse the whole click: copying "the rest" would put a different set
    /// on the clipboard than the one the user selected.
    /// </summary>
    public static VerbRefusal? SelectionRefusal(IReadOnlyList<string> paths, int skippedItems)
    {
        if (paths.Count == 0 || skippedItems > 0)
        {
            return VerbRefusal.SelectionNotFiles;
        }
        foreach (var path in paths)
        {
            if (!PathPolicy.IsAcceptable(path))
            {
                return VerbRefusal.SelectionNotFiles;
            }
        }
        return null;
    }

    /// <summary>
    /// Robo-Paste: exactly one selected item, with a plain drive or network path. The
    /// destination is checked after <see cref="ShellVerbs.PasteDestination"/> trims it, which
    /// is the form the job receives.
    /// </summary>
    public static VerbRefusal? PasteDestinationRefusal(IReadOnlyList<string> selection, int skippedItems)
    {
        var (folder, _) = ShellVerbs.PasteDestination(selection);
        if (folder is null)
        {
            return selection.Count > 1 ? VerbRefusal.NotOneDestination : VerbRefusal.DestinationNotFileSystem;
        }
        if (skippedItems > 0 || !PathPolicy.IsAcceptable(folder))
        {
            return VerbRefusal.DestinationNotFileSystem;
        }
        return null;
    }

    /// <summary>
    /// Explorer's no-op: moving items into the folder they are already in. Pure string
    /// comparison (trailing separators and case ignored): the dispatcher may not stat
    /// anything. A source with no parent (a drive root) never matches.
    /// </summary>
    public static bool IsSameFolderMove(IReadOnlyList<string> sources, string destination, TransferVerb verb)
    {
        if (verb != TransferVerb.Move || sources.Count == 0 || destination.Length == 0)
        {
            return false;
        }
        foreach (var source in sources)
        {
            var parent = WinPath.GetParent(source);
            if (parent.Length == 0 || !WinPath.AreSame(parent, destination))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// What a clipboard read found. <paramref name="decoded"/> is the CF_HDROP result after
    /// the host has resolved any ANSI form, or null when the data could not be read.
    /// A shell IDList without CF_HDROP means virtual items (zip contents, attachments); the
    /// host also reports virtual-file descriptors through <paramref name="hasShellIdList"/>.
    /// Returns null when the paths can be pasted.
    /// </summary>
    public static VerbRefusal? ClassifyClipboard(bool hasHdrop, DropFilesResult? decoded, bool hasShellIdList)
    {
        if (!hasHdrop)
        {
            return hasShellIdList ? VerbRefusal.ClipboardNotFiles : VerbRefusal.ClipboardEmpty;
        }
        if (decoded is null)
        {
            return VerbRefusal.ClipboardEmpty;
        }
        if (decoded.Status == DropFilesStatus.TooLarge)
        {
            return VerbRefusal.ClipboardTooLarge;
        }
        return decoded.Paths.Count == 0 ? VerbRefusal.ClipboardEmpty : null;
    }

    /// <summary>
    /// Finds the names in a legacy narrow (ANSI) DROPFILES block without converting them:
    /// the code page is the host's to apply. The block is untrusted, so every offset is
    /// bounded here instead of trusting pFiles and the terminators the way DragQueryFile
    /// does, which walks past the end of a block with a bad offset or no terminator. One
    /// linear pass; DragQueryFile per index would rescan from the start for each name.
    /// </summary>
    /// <returns>
    /// The byte range of each name, or <see cref="DropFilesStatus.TooLarge"/> over
    /// <see cref="ClipboardPayload.MaxDropFilesBytes"/> or
    /// <see cref="ClipboardPayload.MaxDropFilesPaths"/> (refused, never truncated), or null
    /// for a malformed block: header too short, pFiles outside the block, or a name without
    /// its terminating NUL. A list missing only its final empty name ends at the block's end.
    /// </returns>
    /// <remarks>
    /// Splitting on single zero bytes is safe for every Windows ANSI code page: DBCS trail
    /// bytes are never zero.
    /// </remarks>
    public static AnsiDropFiles? SplitAnsiDropFiles(ReadOnlySpan<byte> block)
    {
        if (block.Length > ClipboardPayload.MaxDropFilesBytes)
        {
            return AnsiDropFiles.TooLarge;
        }
        if (block.Length < ClipboardPayload.DropFilesHeaderSize)
        {
            return null;
        }
        var start = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if (start < ClipboardPayload.DropFilesHeaderSize || start >= (uint)block.Length)
        {
            return null;
        }

        var names = new List<Range>();
        var position = (int)start;
        while (position < block.Length)
        {
            var length = block[position..].IndexOf((byte)0);
            if (length < 0)
            {
                return null; // the last name runs off the end of the block
            }
            if (length == 0)
            {
                break; // the empty name that ends the list
            }
            if (names.Count == ClipboardPayload.MaxDropFilesPaths)
            {
                return AnsiDropFiles.TooLarge;
            }
            names.Add(new Range(position, position + length));
            position += length + 1;
        }
        return new AnsiDropFiles(names, DropFilesStatus.Ok);
    }

    /// <summary>
    /// The waits between OpenClipboard attempts: 10 ms, doubling, the last one shortened so
    /// the total is exactly <paramref name="budget"/>. Never more in total, so a busy
    /// clipboard costs the UI context at most the budget. A zero or negative budget gives
    /// no retries.
    /// </summary>
    public static IReadOnlyList<TimeSpan> RetryDelays(TimeSpan budget)
    {
        var delays = new List<TimeSpan>();
        var remaining = budget;
        var next = FirstRetryDelay;
        while (remaining > TimeSpan.Zero)
        {
            var delay = next < remaining ? next : remaining;
            delays.Add(delay);
            remaining -= delay;
            next = next > TimeSpan.MaxValue / 2 ? TimeSpan.MaxValue : next * 2;
        }
        return delays;
    }
}
