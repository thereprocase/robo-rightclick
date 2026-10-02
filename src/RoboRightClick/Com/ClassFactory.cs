using System.Runtime.InteropServices;
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
    /// Explorer waits on it. Implementations post the real work and return. The paths are
    /// untrusted (any same-user COM client can send them) and are checked by the handler.
    /// </summary>
    /// <param name="skippedItems">Selected items that had no file-system path.</param>
    /// <param name="shellIdList">
    /// The selection's CIDA, validated for exactly <paramref name="paths"/>, or null. Robo-Copy
    /// and Robo-Cut put it on the clipboard beside CF_HDROP for Explorer's own paste.
    /// </param>
    void Invoke(ShellVerb verb, IReadOnlyList<string> paths, int skippedItems, byte[]? shellIdList);

    /// <summary>
    /// Called on the UI thread inside Execute when the selection was refused before any path
    /// could be handed on (<see cref="VerbRefusal.SelectionTooLarge"/>), so the click does not
    /// end in silence. Must return quickly: implementations post the toast and return.
    /// </summary>
    void RefuseSelection(ShellVerb verb, VerbRefusal refusal);
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
    public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject)
    {
        ppvObject = 0;
        if (pUnkOuter != 0)
        {
            return HResult.CLASS_E_NOAGGREGATION;
        }

        try
        {
            var unknown = ComNative.Wrappers.GetOrCreateComInterfaceForObject(
                new VerbCommand(Verb, Handler), CreateComInterfaceFlags.None);
            try
            {
                var hr = Marshal.QueryInterface(unknown, in riid, out var requested);
                if (hr < 0)
                {
                    return hr;
                }
                ppvObject = requested;
                return HResult.S_OK;
            }
            finally
            {
                // The QueryInterface result carries its own reference.
                Marshal.Release(unknown);
            }
        }
        catch (Exception)
        {
            ppvObject = 0;
            return HResult.E_FAIL;
        }
    }

    /// <summary>The tray stays resident regardless, so locks are acknowledged and ignored.</summary>
    public int LockServer(bool fLock) => HResult.S_OK;
}
