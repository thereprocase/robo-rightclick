using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <param name="Paths">File-system paths in selection order.</param>
/// <param name="SkippedItems">Items with no file-system path (virtual folders); reported, not guessed at.</param>
/// <param name="ShellIdList">
/// The selection's "Shell IDList Array" from the same data object as <paramref name="Paths"/>,
/// when it is a well-formed CIDA for exactly those items
/// (<see cref="ClipboardPayload.IsShellIdListFor"/>); otherwise null.
/// </param>
internal sealed record SelectionPaths(IReadOnlyList<string> Paths, int SkippedItems, byte[]? ShellIdList = null);

/// <summary>Converts shell item arrays to and from file-system paths.</summary>
internal static class ShellSelection
{
    /// <summary>Longest path Windows accepts (32,767 UTF-16 units).</summary>
    private const int MaxPathChars = 32_767;

    /// <summary>
    /// Total characters accepted across every path of one selection: the same 64 MiB budget
    /// as the CF_HDROP block, in UTF-16 units. The array may come from any same-user process
    /// that implements IShellItemArray itself, so its size is never trusted.
    /// </summary>
    private const long MaxTotalChars = ClipboardPayload.MaxDropFilesBytes / 2;

    /// <summary>
    /// Default path, one cross-process call: BindToHandler(BHID_DataObject, IID_IDataObject),
    /// GetData(CF_HDROP, TYMED_HGLOBAL), decoded with <see cref="Core.ClipboardPayload.DecodeDropFiles"/>
    /// (same limits as the clipboard), ReleaseStgMedium. Fallback when that fails (virtual
    /// items have no CF_HDROP): GetCount, then GetItemAt + GetDisplayName(SIGDN_FILESYSPATH)
    /// per item, freeing each string with Marshal.FreeCoTaskMem and each IShellItem with
    /// FinalRelease; items without a path count as skipped. Per item, each call is a round
    /// trip to Explorer, roughly 50k items × 2-3 calls × 30-50 µs ≈ 3-7 s of frozen Explorer,
    /// which is why it is only the fallback. Spike 1 times both on 50k items.
    /// </summary>
    /// <exception cref="InvalidDataException">The selection exceeds the size limits.</exception>
    public static SelectionPaths ReadPaths(IShellItemArray array)
    {
        var (viaDataObject, shellIdList) = TryReadDataObject(array);
        if (viaDataObject is { Count: > 0 })
        {
            var list = shellIdList is not null && ClipboardPayload.IsShellIdListFor(shellIdList, viaDataObject.Count)
                ? shellIdList
                : null;
            return new SelectionPaths(viaDataObject, SkippedCount(array, viaDataObject.Count), list);
        }
        return ReadPerItem(array);
    }

    /// <summary>
    /// CLI side (the exact shell path a right-click takes): SHParseDisplayName per full path, SHCreateShellItemArrayFromIDLists over
    /// the PIDLs, ILFree each PIDL. Returns an owned IShellItemArray COM pointer that the
    /// caller releases. Throws FileNotFoundException naming the first path that does not parse.
    /// </summary>
    public static nint CreateArray(IReadOnlyList<string> fullPaths)
    {
        if (fullPaths.Count == 0)
        {
            throw new ArgumentException("A selection needs at least one path.", nameof(fullPaths));
        }

        var pidls = new List<nint>(fullPaths.Count);
        try
        {
            foreach (var path in fullPaths)
            {
                var hr = ComNative.SHParseDisplayName(path, 0, out var pidl, 0, out _);
                if (hr < 0 || pidl == 0)
                {
                    throw new FileNotFoundException($"'{path}' is not a path the shell can resolve.", path);
                }
                pidls.Add(pidl);
            }

            var created = ComNative.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out var array);
            ComNative.ThrowIfFailed(created, "SHCreateShellItemArrayFromIDLists");
            return array;
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                ComNative.ILFree(pidl);
            }
        }
    }

    /// <summary>
    /// Paths null when the data object path is unavailable or unusable; the caller falls
    /// back. The shell ID list is read from the same object, best effort: Explorer's own
    /// paste needs it on the clipboard (<see cref="ClipboardPayload.ShellIdListFormat"/>).
    /// </summary>
    private static (List<string>? Paths, byte[]? ShellIdList) TryReadDataObject(IShellItemArray array)
    {
        IDataObject? dataObject = null;
        var pointer = nint.Zero;
        try
        {
            var bhid = ShellConstants.BHID_DataObject;
            var iid = ShellConstants.IID_IDataObject;
            if (array.BindToHandler(0, in bhid, in iid, out pointer) < 0 || pointer == 0)
            {
                return (null, null);
            }
            dataObject = (IDataObject)ComNative.Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);

            var format = new FORMATETC
            {
                cfFormat = ShellConstants.CF_HDROP,
                ptd = 0,
                dwAspect = ShellConstants.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ShellConstants.TYMED_HGLOBAL,
            };
            if (dataObject.GetData(in format, out var medium) < 0)
            {
                return (null, null);
            }

            List<string>? paths;
            try
            {
                paths = medium.tymed == ShellConstants.TYMED_HGLOBAL ? DecodeHGlobal(medium.data) : null;
            }
            finally
            {
                ComNative.ReleaseStgMedium(ref medium);
            }
            return (paths, paths is { Count: > 0 } ? TryReadShellIdList(dataObject) : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null);
        }
        finally
        {
            ComNative.FinalRelease(dataObject);
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }
    }

    /// <summary>The CIDA bytes, or null when the format is missing, not an HGLOBAL or too large.</summary>
    private static byte[]? TryReadShellIdList(IDataObject dataObject)
    {
        var id = ComNative.RegisterClipboardFormat(ClipboardPayload.ShellIdListFormat);
        if (id is 0 or > ushort.MaxValue)
        {
            return null;
        }
        var format = new FORMATETC
        {
            cfFormat = (ushort)id,
            ptd = 0,
            dwAspect = ShellConstants.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ShellConstants.TYMED_HGLOBAL,
        };
        if (dataObject.GetData(in format, out var medium) < 0)
        {
            return null;
        }
        try
        {
            return medium.tymed == ShellConstants.TYMED_HGLOBAL
                ? CopyHGlobal(medium.data, ClipboardPayload.MaxShellIdListBytes)
                : null;
        }
        finally
        {
            ComNative.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>
    /// The block's bytes; null when empty, unlockable or larger than <paramref name="maxBytes"/>.
    /// The size is checked before anything is copied: the block belongs to another process.
    /// </summary>
    private static byte[]? CopyHGlobal(nint handle, int maxBytes)
    {
        if (handle == 0)
        {
            return null;
        }
        var size = ComNative.GlobalSize(handle);
        if (size == 0 || size > (nuint)maxBytes)
        {
            return null;
        }
        var locked = ComNative.GlobalLock(handle);
        if (locked == 0)
        {
            return null;
        }
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(locked, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            ComNative.GlobalUnlock(handle);
        }
    }

    private static List<string>? DecodeHGlobal(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        // The block's size is checked before anything is copied: it belongs to another process.
        var size = ComNative.GlobalSize(handle);
        if (size == 0)
        {
            return null;
        }
        if (size > ClipboardPayload.MaxDropFilesBytes)
        {
            throw new InvalidDataException("The selection is too large.");
        }

        var locked = ComNative.GlobalLock(handle);
        if (locked == 0)
        {
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = new byte[(int)size];
            Marshal.Copy(locked, bytes, 0, bytes.Length);
        }
        finally
        {
            ComNative.GlobalUnlock(handle);
        }

        var decoded = ClipboardPayload.DecodeDropFiles(bytes);
        return decoded.Status switch
        {
            DropFilesStatus.Ok => [.. decoded.Paths],
            DropFilesStatus.TooLarge => throw new InvalidDataException("The selection is too large."),
            _ => null, // Empty, or ANSI: the per-item path reads those exactly.
        };
    }

    /// <summary>Items in the array that the CF_HDROP block left out (no file-system path).</summary>
    private static int SkippedCount(IShellItemArray array, int pathCount)
    {
        try
        {
            if (array.GetCount(out var count) < 0)
            {
                return 0;
            }
            return count > pathCount ? (int)Math.Min(count - pathCount, int.MaxValue) : 0;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static SelectionPaths ReadPerItem(IShellItemArray array)
    {
        ComNative.ThrowIfFailed(array.GetCount(out var count), "IShellItemArray.GetCount");
        if (count > ClipboardPayload.MaxDropFilesPaths)
        {
            throw new InvalidDataException("The selection is too large.");
        }

        var paths = new List<string>((int)count);
        var skipped = 0;
        long totalChars = 0;
        for (uint i = 0; i < count; i++)
        {
            IShellItem? item = null;
            try
            {
                if (array.GetItemAt(i, out item) < 0 || item is null)
                {
                    skipped++;
                    continue;
                }

                var path = ReadFileSystemPath(item);
                if (path is null)
                {
                    skipped++;
                    continue;
                }
                totalChars += path.Length;
                if (path.Length > MaxPathChars || totalChars > MaxTotalChars)
                {
                    throw new InvalidDataException("The selection is too large.");
                }
                paths.Add(path);
            }
            finally
            {
                ComNative.FinalRelease(item);
            }
        }
        return new SelectionPaths(paths, skipped);
    }

    private static string? ReadFileSystemPath(IShellItem item)
    {
        if (item.GetDisplayName(ShellConstants.SIGDN_FILESYSPATH, out var buffer) < 0 || buffer == 0)
        {
            return null;
        }
        try
        {
            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
