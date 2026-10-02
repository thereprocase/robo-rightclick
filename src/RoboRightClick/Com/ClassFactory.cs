using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <summary>
/// What the COM layer hands a finished selection to. The COM code knows nothing about
/// clipboards or jobs; <see cref="Verbs.VerbDispatcher"/> implements this.
/// </summary>
internal interface IVerbHandler
{
    /// <summary>
    /// Called on the UI (STA) thread inside Explorer's Execute call. Must return quickly:
    /// Explorer waits on it. Implementations post the real work and return.
    /// </summary>
    void Invoke(ShellVerb verb, IReadOnlyList<string> paths);
}

/// <summary>
/// One class factory per verb CLSID. Registered with REGCLS_MULTIPLEUSE so every
/// right-click is served by this process; each CreateInstance makes a fresh
/// <see cref="VerbCommand"/> because Explorer sets per-invocation state on it.
/// </summary>
[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public ClassFactory(ShellVerb verb, IVerbHandler handler)
    {
        Verb = verb;
        Handler = handler;
    }

    public ShellVerb Verb { get; }

    public IVerbHandler Handler { get; }

    /// <summary>
    /// Rejects aggregation (CLASS_E_NOAGGREGATION), creates a VerbCommand, wraps it with
    /// <see cref="ComNative.Wrappers"/> and QueryInterfaces for riid. Never throws: any
    /// exception becomes E_FAIL with *ppvObject = 0.
    /// </summary>
    public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject) =>
        throw new NotImplementedException();

    /// <summary>The tray stays resident regardless, so locks are acknowledged and ignored.</summary>
    public int LockServer(bool fLock) => HResult.S_OK;
}
