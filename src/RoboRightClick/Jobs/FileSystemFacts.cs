using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// Core's planning and scanning questions, answered from the real disk. Thread-safe and
/// stateless: every answer is a fresh look. Called only from job worker threads (the
/// Scanning state), never from the UI thread, which also serves COM calls: a stat on a
/// dead SMB share can block for the whole SMB timeout.
/// </summary>
internal sealed class FileSystemFacts : IPlanningFacts, IScanFacts
{
    public bool Exists(string path) => throw new NotImplementedException();

    /// <summary>
    /// Same volume by GetVolumePathName + volume serial number, so mount points and
    /// SUBST drives answer correctly. A wrong "true" is survivable: the rename step uses
    /// MoveFileEx without COPY_ALLOWED and fails with ERROR_NOT_SAME_DEVICE.
    /// </summary>
    public bool SameVolume(string a, string b) => throw new NotImplementedException();

    /// <summary>
    /// GetFileAttributesEx on the path itself (reparse points are not followed):
    /// FILE_ATTRIBUTE_DIRECTORY with FILE_ATTRIBUTE_REPARSE_POINT is
    /// <see cref="ItemKind.DirectoryLink"/>; a file symlink is a <see cref="ItemKind.File"/>.
    /// Any failure (not found, access denied, bad network path) is Missing.
    /// </summary>
    public ItemKind KindOf(string path) => throw new NotImplementedException();

    /// <summary>
    /// CreateFile(FILE_FLAG_BACKUP_SEMANTICS, no access rights, share all) +
    /// GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS), with the "\\?\"
    /// and "\\?\UNC\" prefixes removed. For a path that does not exist, the nearest existing
    /// ancestor is resolved and the rest appended. Any failure returns the input unchanged.
    /// </summary>
    public string FinalPath(string path) => throw new NotImplementedException();

    /// <summary>
    /// Lazy enumeration (FileSystemEnumerable / FindFirstFileEx with FindExInfoBasic and
    /// FIND_FIRST_EX_LARGE_FETCH) that yields name, kind, size and last-write time from one
    /// directory read. Directories with FILE_ATTRIBUTE_REPARSE_POINT are
    /// <see cref="ScanEntryKind.DirectoryLink"/>. A file symlink is a File with the target's
    /// size (robocopy copies the content). Null when the directory does not exist or cannot
    /// be opened; errors on individual entries skip that entry.
    /// </summary>
    public IEnumerable<ScanEntry>? List(string directory) => throw new NotImplementedException();

    public FileFacts? FileAt(string path) => throw new NotImplementedException();
}
