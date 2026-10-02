namespace RoboRightClick.Jobs;

/// <summary>
/// Tells Explorer that the app changed a folder. Explorer refreshes local NTFS views from
/// change notifications on its own, but views of SMB shares and some mapped drives often do
/// not, and a paste that never appears looks like a paste that failed. Thread-safe;
/// SHChangeNotify with SHCNF_FLUSHNOWAIT does not block on Explorer.
/// </summary>
internal static class ShellNotify
{
    /// <summary>SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT. Called after each step and in Finalizing for the destination, and for each source parent of a cut.</summary>
    public static void FolderChanged(string folder) => throw new NotImplementedException();

    /// <summary>SHCNE_RENAMEITEM or SHCNE_RENAMEFOLDER after a rename step.</summary>
    public static void Renamed(string from, string to, bool isFolder) => throw new NotImplementedException();
}
