namespace RoboRightClick.Com;

/// <param name="Paths">File-system paths in selection order.</param>
/// <param name="SkippedItems">Items with no file-system path (virtual folders); reported, not guessed at.</param>
internal sealed record SelectionPaths(IReadOnlyList<string> Paths, int SkippedItems);

/// <summary>Converts shell item arrays to and from file-system paths.</summary>
internal static class ShellSelection
{
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
    public static SelectionPaths ReadPaths(IShellItemArray array) => throw new NotImplementedException();

    /// <summary>
    /// CLI side (the exact shell path a right-click takes): SHParseDisplayName per full path, SHCreateShellItemArrayFromIDLists over
    /// the PIDLs, ILFree each PIDL. Returns an owned IShellItemArray COM pointer that the
    /// caller releases. Throws FileNotFoundException naming the first path that does not parse.
    /// </summary>
    public static nint CreateArray(IReadOnlyList<string> fullPaths) => throw new NotImplementedException();
}
