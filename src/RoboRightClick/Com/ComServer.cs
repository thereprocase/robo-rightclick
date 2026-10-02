using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <summary>
/// Registers the three verb class objects with COM for the lifetime of the tray.
/// Must be created and disposed on the UI thread: that STA's message loop is what
/// dispatches incoming activations and Execute calls. <see cref="ComCallerSecurity.InitializeProcess"/>
/// must have run first.
/// </summary>
internal sealed class ComServer : IDisposable
{
    private readonly List<ClassFactory> _factories = [];
    private readonly List<nint> _factoryPointers = [];
    private readonly List<uint> _cookies = [];

    public ComServer(IVerbHandler handler)
    {
        Handler = handler;
        UiThreadId = Environment.CurrentManagedThreadId;
    }

    public IVerbHandler Handler { get; }

    /// <summary>
    /// The managed thread id of the UI thread, recorded when the server is created there.
    /// <see cref="VerbCommand"/> checks its calls against it.
    /// </summary>
    public static int UiThreadId { get; private set; }

    /// <summary>
    /// For each entry in <see cref="ShellVerbs.All"/>: wraps a <see cref="ClassFactory"/>
    /// and calls CoRegisterClassObject(CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE |
    /// REGCLS_SUSPENDED), then CoResumeClassObjects once all three are in, so a client never
    /// sees one verb available and another not. Throws COMException on failure: the tray
    /// cannot do its job without these registrations.
    /// </summary>
    public void Register()
    {
        try
        {
            foreach (var info in ShellVerbs.All)
            {
                var factory = new ClassFactory(info.Verb, Handler);
                // Rooted here for the server's lifetime, as well as through the pointer's
                // reference count: COM's reference is not something the GC can see.
                _factories.Add(factory);

                var pointer = ComNative.Wrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
                _factoryPointers.Add(pointer);

                var hr = ComNative.CoRegisterClassObject(
                    info.Clsid, pointer, ShellConstants.CLSCTX_LOCAL_SERVER,
                    ShellConstants.REGCLS_MULTIPLEUSE | ShellConstants.REGCLS_SUSPENDED, out var cookie);
                ComNative.ThrowIfFailed(hr, $"CoRegisterClassObject for {info.Label}");
                _cookies.Add(cookie);
            }
            ComNative.ThrowIfFailed(ComNative.CoResumeClassObjects(), "CoResumeClassObjects");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// CoRevokeClassObject for each cookie. Called first on exit, so a right-click during
    /// shutdown starts a fresh instance instead of reaching a dying one.
    /// </summary>
    public void Dispose()
    {
        foreach (var cookie in _cookies)
        {
            ComNative.CoRevokeClassObject(cookie);
        }
        _cookies.Clear();

        foreach (var pointer in _factoryPointers)
        {
            Marshal.Release(pointer);
        }
        _factoryPointers.Clear();
        _factories.Clear();
    }
}
