using System.IO.Pipes;

namespace RoboRightClick.Jobs;

/// <summary>
/// The app-owned end of robocopy's /UNILOG:\\.\pipe\name output (verified on Windows,
/// testlog 2026-10-02). Created before robocopy starts; nothing ever touches disk.
/// </summary>
/// <remarks>
/// The pipe name is not a secret (it is on robocopy's command line). The security
/// properties are: the app creates the pipe first (FirstPipeInstance), one instance only,
/// a DACL granting only the current user, remote clients refused, and the client's PID
/// checked before a single byte is read. Whether remote clients are refused is a VM check.
/// </remarks>
internal sealed class RobocopyPipe : IDisposable
{
    /// <summary>A line longer than this is cut and the rest discarded: robocopy's longest real line is a 32K path plus a few fields.</summary>
    public const int MaxLineChars = 64 * 1024;

    private RobocopyPipe(NamedPipeServerStream stream)
    {
        Stream = stream;
    }

    public NamedPipeServerStream Stream { get; }

    /// <summary>
    /// NamedPipeServerStreamAcl.Create: inbound, byte mode, maxNumberOfServerInstances 1,
    /// PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, PipeSecurity with one rule
    /// granting the current user ReadWrite, and PipeAccessRule denying NETWORK (S-1-5-2).
    /// Fails if the name already exists, which means someone is squatting: the step fails.
    /// </summary>
    public static RobocopyPipe Create(string pipeName) => throw new NotImplementedException();

    /// <summary>
    /// Waits for a client and compares GetNamedPipeClientProcessId with
    /// <paramref name="expectedPid"/> before reading. On a mismatch it disconnects that
    /// client (DisconnectNamedPipe) and waits again, rather than failing the step, until
    /// the right client connects or the token fires. The caller links the token to
    /// robocopy's exit so a robocopy that dies before connecting does not hang the step.
    /// </summary>
    public Task WaitForRobocopyAsync(int expectedPid, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <summary>
    /// Batches of UTF-16LE lines, decoded with one persistent decoder (a read can split a
    /// code unit or a surrogate pair) from 64 KB reads. Lines are not trimmed: the parser
    /// depends on leading tabs. A broken pipe (ERROR_PIPE_BROKEN, after robocopy exits or
    /// is killed) is the end of output, not an error. Lines over <see cref="MaxLineChars"/>
    /// are cut.
    /// </summary>
    public IAsyncEnumerable<IReadOnlyList<string>> ReadLineBatchesAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public void Dispose() => Stream.Dispose();
}
