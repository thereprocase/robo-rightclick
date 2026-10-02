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
    public static void Invoke(ShellVerb verb, IReadOnlyList<string> fullPaths) => throw new NotImplementedException();
}
