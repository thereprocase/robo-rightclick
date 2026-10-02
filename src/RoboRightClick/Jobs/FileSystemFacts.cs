using System.Collections;
using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;
using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// Core's planning and scanning questions, answered from the real disk. Thread-safe and
/// stateless: every answer is a fresh look. Called only from job worker threads (the
/// Scanning state), never from the UI thread, which also serves COM calls: a stat on a
/// dead SMB share can block for the whole SMB timeout.
/// </summary>
internal sealed unsafe class FileSystemFacts : IPlanningFacts, IScanFacts
{
    private const string VerbatimPrefix = @"\\?\";
    private const string VerbatimUncPrefix = @"\\?\UNC\";

    /// <summary>Starting buffer for the path APIs; a longer path is retried at the size Windows asks for.</summary>
    private const int InitialPathBuffer = 1024;

    /// <summary>True for anything at the path itself, including a dangling link (not followed).</summary>
    public bool Exists(string path) => KindOf(path) != ItemKind.Missing;

    /// <summary>
    /// Same volume by GetVolumePathName + volume serial number, so mount points and
    /// SUBST drives answer correctly. A wrong "true" is survivable: the rename step uses
    /// MoveFileEx without COPY_ALLOWED and fails with ERROR_NOT_SAME_DEVICE.
    /// </summary>
    public bool SameVolume(string a, string b)
    {
        try
        {
            var rootA = VolumeRoot(a);
            var rootB = VolumeRoot(b);
            if (rootA is null || rootB is null)
            {
                return false;
            }

            if (WinPath.AreSame(rootA, rootB))
            {
                return true;
            }

            return VolumeSerial(rootA) is { } serialA && VolumeSerial(rootB) is { } serialB && serialA == serialB;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path's own attributes (reparse points are not followed):
    /// FILE_ATTRIBUTE_DIRECTORY with FILE_ATTRIBUTE_REPARSE_POINT is
    /// <see cref="ItemKind.DirectoryLink"/>; a file symlink is a <see cref="ItemKind.File"/>.
    /// Any failure (not found, access denied, bad network path) is Missing.
    /// </summary>
    public ItemKind KindOf(string path)
    {
        try
        {
            // FileSystemInfo.Attributes reads the attributes of the path itself and reports
            // -1 for a path that does not exist, so no exception is needed for the common case.
            var attributes = new FileInfo(path).Attributes;
            if (attributes == (FileAttributes)(-1))
            {
                return ItemKind.Missing;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                return ItemKind.File;
            }

            return attributes.HasFlag(FileAttributes.ReparsePoint) ? ItemKind.DirectoryLink : ItemKind.Directory;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ItemKind.Missing;
        }
    }

    /// <summary>
    /// CreateFile(FILE_FLAG_BACKUP_SEMANTICS, no access rights, share all) +
    /// GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS), with the "\\?\"
    /// and "\\?\UNC\" prefixes removed. For a path that does not exist, the nearest existing
    /// ancestor is resolved and the rest appended. Any failure returns the input unchanged.
    /// </summary>
    public string FinalPath(string path)
    {
        try
        {
            var missing = new Stack<string>();
            var current = path;
            while (true)
            {
                if (ResolveExisting(current) is { } resolved)
                {
                    return missing.Aggregate(resolved, WinPath.Combine);
                }

                var name = WinPath.GetFileName(current);
                var parent = WinPath.GetParent(current);
                if (name.Length == 0 || parent.Length == 0)
                {
                    return path;
                }

                missing.Push(name);
                current = parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// Lazy enumeration yielding name, kind, size and last-write time from one directory
    /// read. Directories with FILE_ATTRIBUTE_REPARSE_POINT are
    /// <see cref="ScanEntryKind.DirectoryLink"/>. A file symlink is a File with the target's
    /// size (robocopy copies the content). Null when the directory does not exist or cannot
    /// be opened; errors on individual entries skip that entry.
    /// </summary>
    public IEnumerable<ScanEntry>? List(string directory)
    {
        if (KindOf(directory) is not (ItemKind.Directory or ItemKind.DirectoryLink))
        {
            return null;
        }

        try
        {
            // Opened now, with an inaccessible directory reported as an error, so "cannot be
            // opened" is answered here instead of looking like an empty folder later.
            var opened = Enumerate(directory, ignoreInaccessible: false).GetEnumerator();
            return new OpenedListing(opened, () => Enumerate(directory, ignoreInaccessible: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public FileFacts? FileAt(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // FileInfo.Exists is false for a directory, which is the answer wanted here.
            return info.Exists ? FactsOf(info.FullName, info.Length, info.LastWriteTimeUtc, info.Attributes) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string StripVerbatimPrefix(string path)
    {
        if (path.StartsWith(VerbatimUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[VerbatimUncPrefix.Length..];
        }

        return path.StartsWith(VerbatimPrefix, StringComparison.Ordinal) ? path[VerbatimPrefix.Length..] : path;
    }

    private static FileSystemEnumerable<ScanEntry> Enumerate(string directory, bool ignoreInaccessible)
    {
        var options = new EnumerationOptions
        {
            // 0, not the default (Hidden | System): hidden and system files are copied too.
            AttributesToSkip = 0,
            IgnoreInaccessible = ignoreInaccessible,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        return new FileSystemEnumerable<ScanEntry>(directory, ToScanEntry, options);
    }

    private static ScanEntry ToScanEntry(ref FileSystemEntry entry)
    {
        var name = entry.FileName.ToString();
        if (entry.IsDirectory)
        {
            var kind = entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ? ScanEntryKind.DirectoryLink : ScanEntryKind.Directory;
            return new ScanEntry(name, kind, null);
        }

        var facts = FactsOf(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc, entry.Attributes);
        return new ScanEntry(name, ScanEntryKind.File, facts);
    }

    /// <summary>
    /// A file symlink's own directory entry reports the link's size (0); robocopy copies the
    /// target's content, so the target's size and time are what the scan needs. Other reparse
    /// points (cloud placeholders) have no link target and keep their own values.
    /// </summary>
    private static FileFacts FactsOf(string fullPath, long size, DateTimeOffset lastWriteUtc, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try
            {
                if (new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target)
                {
                    return new FileFacts(target.Length, target.LastWriteTimeUtc);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep the entry's own values; robocopy reports the real failure.
            }
        }

        return new FileFacts(size, lastWriteUtc);
    }

    private static string? ResolveExisting(string path)
    {
        using var handle = ProcessNative.CreateFile(
            ProcessNative.ExtendedLengthPath(path),
            0,
            ProcessNative.FILE_SHARE_ALL,
            0,
            ProcessNative.OPEN_EXISTING,
            ProcessNative.FILE_FLAG_BACKUP_SEMANTICS,
            0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[InitialPathBuffer];
        var length = FinalPathName(handle, buffer);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = FinalPathName(handle, buffer);
        }

        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        return StripVerbatimPrefix(new string(buffer, 0, (int)length));
    }

    private static string? VolumeRoot(string path)
    {
        var buffer = new char[InitialPathBuffer];
        fixed (char* p = buffer)
        {
            if (!ProcessNative.GetVolumePathName(ProcessNative.ExtendedLengthPath(path), p, (uint)buffer.Length))
            {
                return null;
            }
        }

        // A prefixed query answers with a prefixed root; strip it so roots compare alike.
        var end = Array.IndexOf(buffer, '\0');
        return end > 0 ? StripVerbatimPrefix(new string(buffer, 0, end)) : null;
    }

    private static uint FinalPathName(SafeFileHandle handle, char[] buffer)
    {
        fixed (char* p = buffer)
        {
            return ProcessNative.GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
        }
    }

    private static uint? VolumeSerial(string root) =>
        ProcessNative.GetVolumeInformation(root, 0, 0, out var serial, 0, 0, 0, 0) ? serial : null;

    /// <summary>
    /// A listing whose directory was opened up front. The first enumeration continues from
    /// that open enumerator; any later one starts a fresh read.
    /// </summary>
    private sealed class OpenedListing(IEnumerator<ScanEntry> first, Func<IEnumerable<ScanEntry>> again) : IEnumerable<ScanEntry>
    {
        private IEnumerator<ScanEntry>? _first = first;

        public IEnumerator<ScanEntry> GetEnumerator() =>
            Interlocked.Exchange(ref _first, null) ?? again().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
