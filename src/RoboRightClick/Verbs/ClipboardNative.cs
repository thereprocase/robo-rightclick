using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RoboRightClick.Verbs;

/// <summary>user32/kernel32/shell32 clipboard entry points (raw Win32, not OLE).</summary>
internal static partial class ClipboardNative
{
    public const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>The system's ANSI code page, which a narrow DROPFILES block is written in.</summary>
    public const uint CP_ACP = 0;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    /// <summary>On success the system owns hMem; on failure the caller must GlobalFree it.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    /// <summary>Changes on every clipboard write by anyone; the clear-after-cut guard.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint hMem);

    /// <summary>
    /// Converts one name of a narrow DROPFILES block (Core finds the names; the code page is
    /// only known here). With a null buffer and cchWideChar 0 it returns the length needed.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static unsafe partial int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte* lpMultiByteStr, int cbMultiByte, char* lpWideCharStr, int cchWideChar);
}
