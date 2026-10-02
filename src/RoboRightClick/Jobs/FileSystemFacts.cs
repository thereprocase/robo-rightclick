using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// Core's planning and scanning questions, answered from the real disk. Thread-safe and
/// stateless: every answer is a fresh look.
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
    /// Directory children without following reparse points. Directories with
    /// FILE_ATTRIBUTE_REPARSE_POINT are <see cref="ScanEntryKind.DirectoryLink"/>.
    /// A file symlink is a File whose size is the target's (robocopy copies the content).
    /// An unreadable directory yields no entries; robocopy reports the error in the run.
    /// </summary>
    public IEnumerable<ScanEntry> List(string directory) => throw new NotImplementedException();

    public FileFacts? FileAt(string path) => throw new NotImplementedException();

    public bool IsDirectory(string path) => throw new NotImplementedException();
}
