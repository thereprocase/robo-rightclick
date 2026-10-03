using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// Classifies the drive under a path for <see cref="ThreadPolicy"/>: network share, SSD or
/// spinning disk, plus the physical disk number so a copy within one disk can be detected.
/// </summary>
/// <remarks>
/// <para>The seek-penalty query (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty)
/// is what Windows itself uses to tell SSDs from spinning disks, and it works on a volume
/// handle opened with no access rights, so a standard user can ask without elevation.</para>
/// <para>Answers are cached per volume for the life of the process: a drive does not change
/// medium, and a stat on a sleeping disk or a slow share should be paid once, not per run.
/// Every failure is <see cref="DriveMedium.Unknown"/>, which <see cref="ThreadPolicy"/> treats
/// as the plain default, so detection can only ever tune a copy, never stop one.</para>
/// <para>Called on job worker threads only, never on the UI thread that also serves COM.</para>
/// </remarks>
internal static unsafe partial class DriveMedia
{
    public readonly record struct Drive(DriveMedium Medium, string? VolumeRoot, uint? DiskNumber);

    private static readonly ConcurrentDictionary<string, Drive> Cache = new(WinPath.Comparer);

    public static Drive Classify(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return new Drive(DriveMedium.Network, WinPath.GetRoot(path), null);
            }
            var root = VolumeRoot(path);
            if (root is null)
            {
                return new Drive(DriveMedium.Unknown, null, null);
            }
            return Cache.GetOrAdd(root, Query);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new Drive(DriveMedium.Unknown, null, null);
        }
    }

    /// <summary>Both ends on one physical disk: the same volume, or the same disk number.</summary>
    public static bool SameDisk(Drive a, Drive b) =>
        (a.VolumeRoot is not null && b.VolumeRoot is not null && WinPath.AreSame(a.VolumeRoot, b.VolumeRoot))
        || (a.DiskNumber is { } x && b.DiskNumber is { } y && x == y);

    /// <summary>
    /// The /MT count for one robocopy run, from the settings and both drives. Never throws:
    /// <see cref="ThreadPolicy.Choose"/> turns any classification failure into
    /// <see cref="ThreadPolicy.Fallback"/>. May block on a slow drive, so callers run it on a
    /// job worker thread.
    /// </summary>
    public static ThreadChoice ThreadsFor(Settings settings, string sourceDirectory, string destinationDirectory) =>
        ThreadPolicy.Choose(settings, () =>
        {
            var source = Classify(sourceDirectory);
            // The destination folder may not exist yet; its volume does.
            var destination = Classify(destinationDirectory);
            return new DrivePair(source.Medium, destination.Medium, SameDisk(source, destination));
        });

    private static Drive Query(string root)
    {
        if (GetDriveType(root) == DriveRemote)
        {
            return new Drive(DriveMedium.Network, root, null);
        }

        using var handle = OpenVolume(root);
        if (handle is null || handle.IsInvalid)
        {
            return new Drive(DriveMedium.Unknown, root, null);
        }

        var medium = DriveMedium.Unknown;
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
        DeviceSeekPenaltyDescriptor seek;
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery),
                &seek, (uint)sizeof(DeviceSeekPenaltyDescriptor), out var returned, 0)
            && returned >= (uint)sizeof(DeviceSeekPenaltyDescriptor))
        {
            medium = seek.IncursSeekPenalty != 0 ? DriveMedium.Rotational : DriveMedium.SolidState;
        }

        uint? disk = null;
        StorageDeviceNumber number;
        if (DeviceIoControl(handle, IoctlStorageGetDeviceNumber, null, 0, &number, (uint)sizeof(StorageDeviceNumber), out returned, 0)
            && returned >= (uint)sizeof(StorageDeviceNumber))
        {
            disk = number.DeviceNumber;
        }
        return new Drive(medium, root, disk);
    }

    /// <summary>
    /// A no-access handle on the volume: "\\.\C:" for a drive letter, the volume GUID path
    /// (without its trailing backslash) for a folder mount point.
    /// </summary>
    private static SafeFileHandle? OpenVolume(string root)
    {
        string device;
        if (root.Length == 3 && root[1] == ':' && root[2] == WinPath.Separator)
        {
            device = @"\\.\" + root[..2];
        }
        else
        {
            var buffer = stackalloc char[64];
            if (!GetVolumeNameForVolumeMountPoint(root, buffer, 64))
            {
                return null;
            }
            device = new string(buffer).TrimEnd(WinPath.Separator);
        }
        return CreateFile(device, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
    }

    private static string? VolumeRoot(string path)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            if (!GetVolumePathName(WinPath.ExtendedLengthPath(path), p, (uint)buffer.Length))
            {
                return null;
            }
            return WinPath.StripVerbatimPrefix(new string(p));
        }
    }

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint DriveRemote = 4;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
