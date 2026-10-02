using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <summary>
/// Registers the three verb class objects with COM for the lifetime of the tray.
/// Must be created and disposed on the UI thread: that STA's message loop is what
/// dispatches incoming activations and Execute calls.
/// </summary>
internal sealed class ComServer : IDisposable
{
    public ComServer(IVerbHandler handler)
    {
        Handler = handler;
    }

    public IVerbHandler Handler { get; }

    /// <summary>
    /// For each entry in <see cref="ShellVerbs.All"/>: wraps a <see cref="ClassFactory"/>
    /// and calls CoRegisterClassObject(CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE |
    /// REGCLS_SUSPENDED), then CoResumeClassObjects once all three are in, so a client never
    /// sees one verb available and another not. Throws COMException on failure: the tray
    /// cannot do its job without these registrations.
    /// </summary>
    public void Register() => throw new NotImplementedException();

    /// <summary>
    /// CoRevokeClassObject for each cookie. Called first on exit, so a right-click during
    /// shutdown starts a fresh instance instead of reaching a dying one.
    /// </summary>
    public void Dispose()
    {
    }
}
