using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RoboRightClick.Core;

namespace RoboRightClick.Jobs;

/// <summary>
/// The steps robocopy cannot do: renames (same-volume moves, including a same-volume
/// keep-both) and single files written under a new name ("X - Copy.txt", keep-both copy).
/// Each returns a <see cref="StepOutcome"/> with per-item failures as
/// <see cref="ErrorReported"/> (Win32 code, operation, path, system message) so retry and the
/// error summary treat them like robocopy's.
/// </summary>
internal static unsafe class InProcessCopier
{
    private static readonly StepOutcome NothingDone = new([], [], null, null);

    /// <summary>
    /// <see cref="RenameStep"/> and a move-mode <see cref="KeepBothStep"/>: MoveFileEx with
    /// flags 0 for files and folders alike (not Directory.Move, whose cross-volume failure is
    /// an IOException without the Win32 code). Flags 0 never copies across volumes and never
    /// overwrites. ERROR_NOT_SAME_DEVICE returns <see cref="StepOutcome.ReplanAsMove"/>,
    /// not an error. Never deletes anything itself.
    /// </summary>
    public static StepOutcome Rename(string source, string destination)
    {
        if (ProcessNative.MoveFileEx(WinPath.ExtendedLengthPath(source), WinPath.ExtendedLengthPath(destination), 0))
        {
            return new StepOutcome([source], [], null, null);
        }

        var code = Marshal.GetLastPInvokeError();
        if (code == ProcessNative.ERROR_NOT_SAME_DEVICE)
        {
            return NothingDone with { ReplanAsMove = true };
        }

        // The kind is read only to word the operation; a failure to read it is not an error.
        var operation = Directory.Exists(source) ? "Moving Directory" : "Moving File";
        return Failed(code, operation, source);
    }

    /// <summary>
    /// <see cref="DuplicateFileStep"/> and copy-mode <see cref="KeepBothStep"/>: CopyFileEx
    /// with COPY_FILE_FAIL_IF_EXISTS (the name was chosen to be free; never overwrite if it
    /// no longer is: ERROR_FILE_EXISTS is an error, and nothing of ours was written). Progress
    /// feeds <paramref name="onBytes"/> with the cumulative bytes copied so far in this file;
    /// the callback blocks on <paramref name="pause"/> and returns PROGRESS_CANCEL on cancel,
    /// which makes Windows delete the partial file. The copy runs on a dedicated thread
    /// because the callback blocks.
    /// </summary>
    public static Task<StepOutcome> CopyFileAsync(
        string source,
        string destination,
        PauseGate pause,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StepOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(CopyFile(source, destination, pause, onBytes, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "InProcessCopy",
        };
        thread.Start();
        return completion.Task;
    }

    private static StepOutcome CopyFile(string source, string destination, PauseGate pause, Action<long> onBytes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return NothingDone;
        }

        var state = new CopyState(pause, onBytes, cancellationToken);
        var handle = GCHandle.Alloc(state);
        try
        {
            var routine = (nint)(delegate* unmanaged[Stdcall]<long, long, long, long, uint, uint, nint, nint, nint, uint>)&ProgressRoutine;
            var copied = ProcessNative.CopyFileEx(
                WinPath.ExtendedLengthPath(source),
                WinPath.ExtendedLengthPath(destination),
                routine,
                GCHandle.ToIntPtr(handle),
                0,
                ProcessNative.COPY_FILE_FAIL_IF_EXISTS);
            if (copied)
            {
                return new StepOutcome([source], [], null, null);
            }

            var code = Marshal.GetLastPInvokeError();
            if (code == ProcessNative.ERROR_REQUEST_ABORTED && cancellationToken.IsCancellationRequested)
            {
                return NothingDone;
            }

            return Failed(code, "Copying File", source);
        }
        finally
        {
            handle.Free();
        }
    }

    private static StepOutcome Failed(int code, string operation, string path) =>
        new([], [new ErrorReported(code, operation, path, new Win32Exception(code).Message)], null, null);

    /// <summary>
    /// LPPROGRESS_ROUTINE. Runs on the copying thread, so blocking here pauses the copy. It
    /// must not throw into native code: a failure of the pause gate or the state cancels
    /// the copy instead; a failure of the progress observer is ignored.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        uint callbackReason,
        nint sourceFile,
        nint destinationFile,
        nint data)
    {
        try
        {
            var state = (CopyState)GCHandle.FromIntPtr(data).Target!;
            if (state.Cancellation.IsCancellationRequested)
            {
                return ProcessNative.PROGRESS_CANCEL;
            }

            try
            {
                state.OnBytes(totalBytesTransferred);
            }
            catch (Exception)
            {
                // Progress is display only; a faulty observer must not abort a good copy
                // and surface as error 1235 the user never caused.
            }

            state.Pause.WaitWhilePaused(state.Cancellation);
            return state.Cancellation.IsCancellationRequested ? ProcessNative.PROGRESS_CANCEL : ProcessNative.PROGRESS_CONTINUE;
        }
        catch
        {
            return ProcessNative.PROGRESS_CANCEL;
        }
    }

    private sealed record CopyState(PauseGate Pause, Action<long> OnBytes, CancellationToken Cancellation);
}
