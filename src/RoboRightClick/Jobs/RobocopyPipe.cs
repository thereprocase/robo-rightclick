using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using RoboRightClick.Core;

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
    public const int MaxLineChars = Utf16Lines.MaxLineChars;

    private const int ReadBufferBytes = 64 * 1024;

    // HRESULT_FROM_WIN32 of the two errors a pipe gives once the writer has gone.
    private const int BrokenPipeHResult = unchecked((int)0x8007006D);
    private const int PipeNotConnectedHResult = unchecked((int)0x800700E9);

    private static readonly TimeSpan RejectedClientBackoff = TimeSpan.FromMilliseconds(20);

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
    public static RobocopyPipe Create(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");

        var security = new PipeSecurity();
        // Deny first for readability; the DACL is ordered canonically either way. NETWORK is
        // denied explicitly so that a remote client is refused even if the user rule were
        // ever broadened.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var stream = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0,
            0,
            security);
        return new RobocopyPipe(stream);
    }

    /// <summary>
    /// Waits for a client and compares GetNamedPipeClientProcessId with
    /// <paramref name="expectedPid"/> before reading. On a mismatch it disconnects that
    /// client (DisconnectNamedPipe) and waits again, rather than failing the step, until
    /// the right client connects or the token fires. The caller links the token to
    /// robocopy's exit so a robocopy that dies before connecting does not hang the step.
    /// </summary>
    public async Task WaitForRobocopyAsync(int expectedPid, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Fail closed: a client whose identity cannot be read is treated as the wrong one.
            if (ProcessNative.GetNamedPipeClientProcessId(Stream.SafePipeHandle, out var clientPid)
                && clientPid == (uint)expectedPid)
            {
                return;
            }

            Stream.Disconnect();
            await Task.Delay(RejectedClientBackoff, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Batches of UTF-16LE lines, decoded with one persistent decoder (a read can split a
    /// code unit or a surrogate pair) from 64 KB reads. Lines are not trimmed: the parser
    /// depends on leading tabs. A broken pipe (ERROR_PIPE_BROKEN, after robocopy exits or
    /// is killed) is the end of output, not an error. Lines over <see cref="MaxLineChars"/>
    /// are cut.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<string>> ReadLineBatchesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferBytes];
        var splitter = new Utf16Lines();

        while (true)
        {
            int read;
            try
            {
                read = await Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.HResult is BrokenPipeHResult or PipeNotConnectedHResult)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            var lines = splitter.Feed(buffer.AsSpan(0, read));
            if (lines.Count > 0)
            {
                yield return lines;
            }
        }

        if (splitter.Flush() is { } last)
        {
            yield return [last];
        }
    }

    public void Dispose() => Stream.Dispose();
}
