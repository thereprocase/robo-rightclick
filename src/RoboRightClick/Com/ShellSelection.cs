namespace RoboRightClick.Com;

/// <param name="Paths">File-system paths in selection order.</param>
/// <param name="SkippedItems">Items with no file-system path (virtual folders); reported, not guessed at.</param>
internal sealed record SelectionPaths(IReadOnlyList<string> Paths, int SkippedItems);

/// <summary>Converts shell item arrays to and from file-system paths.</summary>
internal static class ShellSelection
{
    /// <summary>
    /// GetCount, then GetItemAt + GetDisplayName(SIGDN_FILESYSPATH) per item, freeing each
    /// string with Marshal.FreeCoTaskMem. Each call is a cross-process round trip to
    /// Explorer; if 500-item selections prove slow (M0 spike 1), switch to one
    /// BindToHandler(BHID_DataObject) and read CF_HDROP in a single call.
    /// </summary>
    public static SelectionPaths ReadPaths(IShellItemArray array) => throw new NotImplementedException();

    /// <summary>
    /// CLI side: SHParseDisplayName per full path, SHCreateShellItemArrayFromIDLists over
    /// the PIDLs, ILFree each PIDL. Returns an owned IShellItemArray COM pointer that the
    /// caller releases. Throws FileNotFoundException naming the first path that does not parse.
    /// </summary>
    public static nint CreateArray(IReadOnlyList<string> fullPaths) => throw new NotImplementedException();
}
