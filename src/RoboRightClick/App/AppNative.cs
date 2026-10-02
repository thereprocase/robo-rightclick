using System.Runtime.InteropServices;

namespace RoboRightClick.App;

/// <summary>Small user32/kernel32 calls the app shell needs.</summary>
internal static partial class AppNative
{
    /// <summary>AttachConsole: a WinExe has no console, so CLI output attaches to the parent's.</summary>
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    /// <summary>Frees HICONs from Bitmap.GetHicon (TrayIcons).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);
}
