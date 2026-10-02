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
    /// paste has finished. Throws COMException with the failing HRESULT.
    /// </summary>
    public static void Invoke(ShellVerb verb, IReadOnlyList<string> fullPaths) => throw new NotImplementedException();
}
