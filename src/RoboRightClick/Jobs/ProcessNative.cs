using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;
using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>kernel32/ntdll entry points for running robocopy and copying in-process.</summary>
internal static unsafe partial class ProcessNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;

        /// <summary>Live progress source: tracks robocopy's reads continuously (testlog 2026-10-02).</summary>
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    public const int JobObjectExtendedLimitInformationClass = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;
    public const uint PROGRESS_CONTINUE = 0;
    public const uint PROGRESS_CANCEL = 1;

    public const uint FILE_SHARE_ALL = 0x7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const int ERROR_NOT_SAME_DEVICE = 17;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_REQUEST_ABORTED = 1235;

    /// <summary>Pause (verified on /MT:32 robocopy, testlog 2026-10-02). Returns an NTSTATUS.</summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(SafeProcessHandle processHandle);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessIoCounters(SafeProcessHandle hProcess, out IoCounters lpIoCounters);

    /// <summary>Who is on the other end of the output pipe; must be the robocopy the app started.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// Rename only. Called with dwFlags = 0: no MOVEFILE_COPY_ALLOWED, so a move between
    /// volumes fails with ERROR_NOT_SAME_DEVICE instead of quietly becoming an in-process
    /// copy-and-delete (which File.Move would do), and no MOVEFILE_REPLACE_EXISTING, so it
    /// never overwrites.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    /// <summary>
    /// In-process copy that keeps attributes, streams and timestamps like Explorer.
    /// lpProgressRoutine is an unmanaged function pointer (LPPROGRESS_ROUTINE). Returning
    /// PROGRESS_CANCEL makes the system delete the partial destination; blocking inside the
    /// routine pauses the copy.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        nint lpProgressRoutine,
        nint lpData,
        nint pbCancel,
        uint dwCopyFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial SafeFileHandle CreateJobObject(nint lpJobAttributes, nint lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle hJob, int jobObjectInfoClass, in JobObjectExtendedLimitInformation lpJobObjectInfo, uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

    /// <summary>
    /// Opens a path to ask about it, never to read or write: dwDesiredAccess 0 and
    /// FILE_FLAG_BACKUP_SEMANTICS (so directories open too). The final reparse point is
    /// followed, which is the point for resolving a location.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>Returns the length needed when the buffer is too small, 0 on failure. dwFlags 0 = FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string lpszFileName, char* lpszVolumePathName, uint cchBufferLength);

    /// <summary>Only the serial number is wanted; the other outputs are passed as null.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(
        string lpRootPathName,
        nint lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        nint lpMaximumComponentLength,
        nint lpFileSystemFlags,
        nint lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_ATTRIBUTE_READONLY = 0x1;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int FileBasicInfoClass = 0;
    private const int FileDispositionInfoClass = 4;

    /// <summary>FILE_BASIC_INFO. Zero times mean "leave unchanged" when set.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    /// <summary>
    /// Deletes <paramref name="path"/> only if, on the handle that does the deleting, it is a
    /// plain file: opened with FILE_FLAG_OPEN_REPARSE_POINT (a link is opened as itself, never
    /// followed) and without FILE_FLAG_BACKUP_SEMANTICS (a directory does not open at all),
    /// its attributes checked on that handle, then deleted through FileDispositionInfo on the
    /// same handle. A check by path followed by a delete by path would leave a window in
    /// which another process could swap in a link to a file outside the job.
    /// A read-only file (robocopy copies attributes, so a partial copy of a read-only source
    /// is read-only) has the attribute cleared first, and restored if the delete fails.
    /// Returns true when the file was deleted; false for anything else, without throwing.
    /// </summary>
    public static bool DeleteFileIfNotReparsePoint(string path)
    {
        using var handle = CreateFile(
            WinPath.ExtendedLengthPath(path),
            DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_ALL,
            0,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT,
            0);
        if (handle.IsInvalid)
        {
            return false;
        }

        FileBasicInfo basic;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, &basic, (uint)sizeof(FileBasicInfo)))
        {
            return false;
        }
        if ((basic.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0)
        {
            return false;
        }

        var wasReadOnly = (basic.FileAttributes & FILE_ATTRIBUTE_READONLY) != 0;
        if (wasReadOnly && !SetAttributes(handle, basic.FileAttributes & ~FILE_ATTRIBUTE_READONLY))
        {
            return false;
        }

        byte deleteFile = 1;
        if (SetFileInformationByHandle(handle, FileDispositionInfoClass, &deleteFile, sizeof(byte)))
        {
            // The file is removed when this handle closes.
            return true;
        }
        if (wasReadOnly)
        {
            SetAttributes(handle, basic.FileAttributes);
        }
        return false;
    }

    private static bool SetAttributes(SafeFileHandle handle, uint attributes)
    {
        // 0 would mean "leave unchanged"; FILE_ATTRIBUTE_NORMAL (0x80) is the explicit "none".
        var info = new FileBasicInfo { FileAttributes = attributes == 0 ? 0x80u : attributes };
        return SetFileInformationByHandle(handle, FileBasicInfoClass, &info, (uint)sizeof(FileBasicInfo));
    }
}
