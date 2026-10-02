using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;

namespace RoboRightClick.Jobs;

/// <summary>kernel32/ntdll entry points for running robocopy and copying in-process.</summary>
internal static partial class ProcessNative
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
}
