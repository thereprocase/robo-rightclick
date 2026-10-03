using System.Runtime.InteropServices;

namespace RoboRightClick.Jobs;

/// <summary>
/// Tells Explorer that the app changed a folder. Explorer refreshes local NTFS views from
/// change notifications on its own, but views of SMB shares and some mapped drives often do
/// not, and a paste that never appears looks like a paste that failed. Thread-safe;
/// SHChangeNotify with SHCNF_FLUSHNOWAIT does not block on Explorer.
/// </summary>
internal static partial class ShellNotify
{
    private const int SHCNE_RENAMEITEM = 0x00000001;
    private const int SHCNE_RENAMEFOLDER = 0x00020000;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x3000;

    private const uint Flags = SHCNF_PATHW | SHCNF_FLUSHNOWAIT;

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, string? dwItem1, string? dwItem2);

    /// <summary>SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT. Called after each step and in Finalizing for the destination, and for each source parent of a cut.</summary>
    public static void FolderChanged(string folder)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            SHChangeNotify(SHCNE_UPDATEDIR, Flags, folder, null);
        }
    }

    /// <summary>
    /// SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT: Explorer caches context-menu and
    /// file icons, so an update that replaced the menu icons asks it to reload them.
    /// </summary>
    public static void AssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0x3000, null, null);

    /// <summary>SHCNE_RENAMEITEM or SHCNE_RENAMEFOLDER after a rename step.</summary>
    public static void Renamed(string from, string to, bool isFolder)
    {
        if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
        {
            SHChangeNotify(isFolder ? SHCNE_RENAMEFOLDER : SHCNE_RENAMEITEM, Flags, from, to);
        }
    }
}
