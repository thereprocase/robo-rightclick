using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <summary>
/// Invokes a verb exactly as Explorer does, for the CLI and VM automation:
/// CoCreateInstance(verb CLSID, CLSCTX_LOCAL_SERVER) reaches the running tray (or makes
/// COM start one with -Embedding), then SetSelection with a shell item array, then
/// Execute. Exercising the real activation path is the point; a shortcut into the job
/// engine would test nothing the right-click uses.
/// </summary>
internal static class ComClient
{
    /// <summary>
    /// Returns once Execute returns, which is when the verb has been accepted, not when a
    /// paste has finished. Calls CoAllowSetForegroundWindow on the proxy before Execute, so
    /// the tray may bring its progress or conflict window to the front as it does for
    /// Explorer. Releases every proxy with FinalRelease. Throws COMException with the failing
    /// HRESULT; REGDB_E_CLASSNOTREG means "not installed for this user".
    /// </summary>
    public static void Invoke(ShellVerb verb, IReadOnlyList<string> fullPaths)
    {
        var clsid = ShellVerbs.Get(verb).Clsid;

        // Parsed first: a mistyped path fails before COM starts a tray for nothing.
        var arrayPointer = ShellSelection.CreateArray(fullPaths);
        var serverPointer = nint.Zero;
        object? server = null;
        object? array = null;
        try
        {
            var iid = ShellConstants.IID_IUnknown;
            var hr = ComNative.CoCreateInstance(clsid, 0, ShellConstants.CLSCTX_LOCAL_SERVER, in iid, out serverPointer);
            ComNative.ThrowIfFailed(hr, "CoCreateInstance");

            // One proxy for the identity; the casts below ask the server for each interface
            // (a failed cast means the server does not implement it).
            server = ComNative.Wrappers.GetOrCreateObjectForComInstance(serverPointer, CreateObjectFlags.UniqueInstance);
            array = ComNative.Wrappers.GetOrCreateObjectForComInstance(arrayPointer, CreateObjectFlags.UniqueInstance);

            if (server is not IObjectWithSelection withSelection || server is not IExecuteCommand command)
            {
                throw new COMException("The server does not implement the verb interfaces.", HResult.E_NOINTERFACE);
            }

            // Best effort: without it the tray may not be allowed to bring a window forward.
            ComNative.CoAllowSetForegroundWindow(serverPointer, 0);

            ComNative.ThrowIfFailed(withSelection.SetSelection((IShellItemArray)array), "SetSelection");
            ComNative.ThrowIfFailed(command.Execute(), "Execute");
        }
        finally
        {
            ComNative.FinalRelease(array);
            ComNative.FinalRelease(server);
            if (serverPointer != 0)
            {
                Marshal.Release(serverPointer);
            }
            Marshal.Release(arrayPointer);
        }
    }
}
