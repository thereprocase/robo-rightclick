using System.IO.Pipes;

namespace RoboRightClick.Jobs;

/// <summary>The output pipe's client was not the robocopy process the app started.</summary>
internal sealed class UntrustedPipeClientException(uint clientPid, int expectedPid)
    : Exception($"Output pipe client was process {clientPid}, expected robocopy {expectedPid}.");

/// <summary>
/// The app-owned end of robocopy's /UNILOG:\\.\pipe\name output (verified on Windows,
/// testlog 2026-10-02). Created before robocopy starts; nothing ever touches disk.
/// </summary>
internal sealed class RobocopyPipe : IDisposable
{
    private RobocopyPipe(NamedPipeServerStream stream)
    {
        Stream = stream;
    }

    public NamedPipeServerStream Stream { get; }

    /// <summary>
    /// Inbound byte pipe, one instance, async, with a PipeSecurity that grants only the
    /// current user (NamedPipeServerStreamAcl.Create). Creation fails if the name already
    /// exists, which with an unguessable name means someone is squatting: the step fails.
    /// </summary>
    public static RobocopyPipe Create(string pipeName) => throw new NotImplementedException();

    /// <summary>
    /// Waits for the connection, then compares GetNamedPipeClientProcessId with
    /// <paramref name="expectedPid"/> and throws <see cref="UntrustedPipeClientException"/>
    /// on mismatch, before a single byte is read. The caller links the token to robocopy's
    /// exit so a robocopy that dies before connecting does not hang the step.
    /// </summary>
    public Task WaitForRobocopyAsync(int expectedPid, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <summary>
    /// UTF-16LE lines (BOM stripped by the parser). Ends when robocopy closes the pipe.
    /// Lines are not trimmed: the parser depends on leading tabs.
    /// </summary>
    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public void Dispose() => Stream.Dispose();
}
