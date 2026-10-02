using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// The steps robocopy cannot do: renames (same-volume moves, including a same-volume
/// keep-both) and single files written under a new name ("X - Copy.txt", keep-both copy).
/// Each returns a <see cref="StepOutcome"/> with per-item failures as
/// <see cref="ErrorReported"/> (Win32 code, operation, path, system message) so retry and the
/// error summary treat them like robocopy's.
/// </summary>
internal static class InProcessCopier
{
    /// <summary>
    /// <see cref="RenameStep"/> and a move-mode <see cref="KeepBothStep"/>: MoveFileEx with
    /// flags 0 for files and folders alike (not Directory.Move, whose cross-volume failure is
    /// an IOException without the Win32 code). Flags 0 never copies across volumes and never
    /// overwrites. ERROR_NOT_SAME_DEVICE returns <see cref="StepOutcome.ReplanAsMove"/>,
    /// not an error. Never deletes anything itself.
    /// </summary>
    public static StepOutcome Rename(string source, string destination) => throw new NotImplementedException();

    /// <summary>
    /// <see cref="DuplicateFileStep"/> and copy-mode <see cref="KeepBothStep"/>: CopyFileEx
    /// with COPY_FILE_FAIL_IF_EXISTS (the name was chosen to be free; never overwrite if it
    /// no longer is: ERROR_FILE_EXISTS is an error, and nothing of ours was written). Progress
    /// feeds <paramref name="onBytes"/>; the callback blocks on <paramref name="pause"/> and
    /// returns PROGRESS_CANCEL on cancel, which makes Windows delete the partial file. The
    /// copy runs on a dedicated thread because the callback blocks.
    /// </summary>
    public static Task<StepOutcome> CopyFileAsync(
        string source,
        string destination,
        PauseGate pause,
        Action<long> onBytes,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
