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

    private const int FileIdInfoClass = 18;
    private const int FileProcessIdsUsingFileInformationClass = 47;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;

    /// <summary>Longest PID list asked for; beyond it the answer is "unknown", never a guess.</summary>
    private const int MaxProcessIdsUsingFile = 4_096;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    /// <summary>
    /// BY_HANDLE_FILE_INFORMATION. Its times are FILETIMEs, two DWORDs aligned to 4, so the
    /// struct is 52 bytes with CreationTime at offset 4. Pack = 4 keeps the longs there; with
    /// the default packing every field after the attributes would be read 4 bytes off.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle hFile, ByHandleFileInformation* lpFileInformation);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationFile(SafeFileHandle fileHandle, IoStatusBlock* ioStatusBlock, void* fileInformation, uint length, int fileInformationClass);

    /// <summary>
    /// Looks at one destination while the robocopy being killed is suspended: is there a
    /// plain file, which one (<see cref="FileIdentity"/>), and does robocopy hold it open
    /// (FileProcessIdsUsingFileInformation, the list Restart Manager uses)? The handle asks
    /// for FILE_READ_ATTRIBUTES only, which no sharing mode refuses, and opens a link as
    /// itself. Observed on NTFS, FAT32, exFAT and an SMB loopback share (testlog 2026-10-02).
    /// Any failure is <see cref="KillEvidence.Unknown"/>: cleanup then deletes nothing there.
    /// </summary>
    public static KillObservation ObserveAtKill(string path, int robocopyProcessId)
    {
        using var handle = CreateFile(
            WinPath.ExtendedLengthPath(path),
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_ALL,
            0,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return new KillObservation(
                error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND ? KillEvidence.Absent : KillEvidence.Unknown,
                default);
        }

        if (!TryReadPlainFileIdentity(handle, out var identity))
        {
            return default;
        }

        var openBy = ProcessIdsUsingFile(handle);
        if (openBy is null)
        {
            return new KillObservation(KillEvidence.Unknown, identity);
        }
        var evidence = openBy.Contains((nuint)robocopyProcessId) ? KillEvidence.OpenByRobocopy : KillEvidence.NotOpenByRobocopy;
        return new KillObservation(evidence, identity);
    }

    /// <summary>Process IDs with the file open, or null when the file system cannot say.</summary>
    private static HashSet<nuint>? ProcessIdsUsingFile(SafeFileHandle handle)
    {
        // FILE_PROCESS_IDS_USING_FILE_INFORMATION: a ULONG count, then ULONG_PTR IDs (aligned).
        var capacity = 64;
        while (capacity <= MaxProcessIdsUsingFile)
        {
            var bytes = (uint)(sizeof(nuint) * (capacity + 1));
            var buffer = NativeMemory.Alloc(bytes);
            try
            {
                IoStatusBlock io;
                var status = NtQueryInformationFile(handle, &io, buffer, bytes, FileProcessIdsUsingFileInformationClass);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    capacity *= 4;
                    continue;
                }
                if (status < 0)
                {
                    return null;
                }
                var count = *(uint*)buffer;
                if (count > capacity)
                {
                    return null;
                }
                var ids = (nuint*)((byte*)buffer + sizeof(nuint));
                var set = new HashSet<nuint>();
                for (var i = 0; i < count; i++)
                {
                    set.Add(ids[i]);
                }
                return set;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return null;
    }

    /// <summary>
    /// The identity of a plain file (not a directory, not a reparse point) on an open handle.
    /// FILE_ID_INFO where the file system has it (NTFS, ReFS, SMB); otherwise the 64-bit file
    /// index (FAT32 and exFAT answer only that, observed 2026-10-02). The same handle-based
    /// read is used when the file is observed and when it is deleted, so the two compare.
    /// </summary>
    private static bool TryReadPlainFileIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        ByHandleFileInformation legacy;
        if (!GetFileInformationByHandle(handle, &legacy))
        {
            return false;
        }
        if ((legacy.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0)
        {
            return false;
        }

        FileIdInfo id;
        identity = GetFileInformationByHandleEx(handle, FileIdInfoClass, &id, (uint)sizeof(FileIdInfo))
            ? new FileIdentity(id.VolumeSerialNumber, id.FileIdHigh, id.FileIdLow, legacy.CreationTime)
            : new FileIdentity(legacy.VolumeSerialNumber, 0, ((ulong)legacy.FileIndexHigh << 32) | legacy.FileIndexLow, legacy.CreationTime);
        return identity.IsKnown;
    }

    /// <summary>
    /// Deletes <paramref name="path"/> only if, on the handle that does the deleting, it is a
    /// plain file and still the file <paramref name="expected"/> identifies: opened with
    /// FILE_FLAG_OPEN_REPARSE_POINT (a link is opened as itself, never followed) and without
    /// FILE_FLAG_BACKUP_SEMANTICS (a directory does not open at all), identity and attributes
    /// checked on that handle, then deleted through FileDispositionInfo on the same handle. A
    /// check by path followed by a delete by path would leave a window in which another
    /// process could swap in a link, or its own file, under the same name.
    /// A read-only file (robocopy copies attributes, so a partial copy of a read-only source
    /// is read-only) has the attribute cleared first, and restored if the delete fails.
    /// Returns true when the file was deleted; false for anything else, without throwing.
    /// </summary>
    public static bool DeleteFileIfSameFile(string path, FileIdentity expected)
    {
        if (!expected.IsKnown)
        {
            return false;
        }
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

        if (!TryReadPlainFileIdentity(handle, out var actual) || actual != expected)
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
