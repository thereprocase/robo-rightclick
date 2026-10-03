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

    /// <summary>Position of a window's scroll bar (SB_HORZ = 0); for a report-view ListView, horizontal pixels.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetScrollPos(nint hWnd, int nBar);

    /// <summary>
    /// Makes a font in memory available to GDI in this process only. GDI+'s
    /// PrivateFontCollection does not: TextRenderer and every standard control draw with GDI,
    /// which would otherwise substitute another font. Returns 0 on failure.
    /// </summary>
    [LibraryImport("gdi32.dll")]
    public static partial nint AddFontMemResourceEx(nint pFileView, uint cjSize, nint pvReserved, ref uint pNumFonts);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    /// <summary>The face name of the font GDI actually selected, which differs from the one asked for when it substituted.</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetTextFaceW")]
    public static unsafe partial int GetTextFace(nint hdc, int c, char* lpName);

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>Only the Settings window's probe uses this: the hotkey itself is a keyboard hook, not a registration.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);
}
