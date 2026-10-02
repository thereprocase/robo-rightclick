using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// The steps robocopy cannot do: renames (same-volume moves) and single files written
/// under a new name ("X - Copy.txt", keep-both). Each returns a <see cref="StepOutcome"/>
/// with per-item failures as <see cref="ErrorReported"/> (Win32 code, operation, path,
/// system message) so retry and the error summary treat them like robocopy's.
/// </summary>
internal static class InProcessCopier
{
    /// <summary>
    /// <see cref="RenameStep"/>: MoveFileEx(flags 0) for files, Directory.Move for folders;
    /// both refuse to cross volumes or overwrite. ERROR_NOT_SAME_DEVICE is returned as an
    /// error with that code so the job can re-plan the item as a robocopy /MOVE step.
    /// Never deletes anything itself.
    /// </summary>
    public static StepOutcome Rename(RenameStep step) => throw new NotImplementedException();

    /// <summary>
    /// <see cref="DuplicateFileStep"/> and copy-mode <see cref="KeepBothStep"/>: CopyFileEx
    /// with COPY_FILE_FAIL_IF_EXISTS (the name was chosen to be free; never overwrite if it
    /// no longer is). Progress feeds <paramref name="onBytes"/>; the callback blocks on
    /// <paramref name="pause"/> and returns PROGRESS_CANCEL on cancel, which makes Windows
    /// delete the partial file.
    /// </summary>
    public static Task<StepOutcome> CopyFileAsync(
        string source,
        string destination,
        PauseGate pause,
        Action<long> onBytes,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
