using System.Runtime.InteropServices;

namespace RoboRightClick.App;

/// <summary>Small user32/kernel32 calls the app shell needs.</summary>
internal static partial class AppNative
{
    /// <summary>AttachConsole: a WinExe has no console, so CLI output attaches to the parent's.</summary>
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    /// <summary>SetDefaultDllDirectories: search only System32 for DLLs loaded by name.</summary>
    public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    /// <summary>SetErrorMode: no "insert a disk" or "drive not ready" dialogs (also inherited by robocopy).</summary>
    public const uint SEM_FAILCRITICALERRORS = 0x0001;

    /// <summary>SetErrorMode: no Windows Error Reporting dialog for a fault.</summary>
    public const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    /// <summary>Frees HICONs from Bitmap.GetHicon (TrayIcons).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetErrorMode();

    /// <summary>Returns the previous mode.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint SetErrorMode(uint uMode);

    /// <summary>A pseudo-handle (-1) to this process; it needs no CloseHandle.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    /// <summary>Only from the thread that created <paramref name="hWnd"/>.</summary>
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShutdownBlockReasonCreate(nint hWnd, string pwszReason);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShutdownBlockReasonDestroy(nint hWnd);
}
