using System.Runtime.InteropServices.Marshalling;
using RoboRightClick.Core;

namespace RoboRightClick.Com;

/// <summary>
/// The object Explorer drives for one menu click through DelegateExecute:
/// Initialize (optional), SetSelection, the IExecuteCommand setters, then Execute.
/// All calls arrive on the UI thread, because the class objects are registered there,
/// so this type needs no locking.
/// </summary>
/// <remarks>
/// Every method returns an HRESULT and never lets an exception cross into Explorer.
/// "All calls arrive on the UI thread" depends on the generated CCW not being agile; each
/// method asserts (in Debug, and logs once in Release) that it runs on the UI thread id
/// recorded at startup, and M0 spike 1 confirms it.
/// </remarks>
[GeneratedComClass]
internal sealed partial class VerbCommand : IExecuteCommand, IObjectWithSelection, IInitializeCommand
{
    public VerbCommand(ShellVerb verb, IVerbHandler handler)
    {
        Verb = verb;
        Handler = handler;
    }

    public ShellVerb Verb { get; }

    public IVerbHandler Handler { get; }

    /// <summary>
    /// Holds Explorer's array until Execute. Released deterministically on the UI thread with
    /// ((ComObject)(object)array).FinalRelease() once read (and when replaced, and if Execute
    /// never comes), never left to the finalizer, which would release a cross-process proxy
    /// from the wrong apartment at an unpredictable time.
    /// </summary>
    public IShellItemArray? Selection { get; private set; }

    public int Initialize(string? pszCommandName, nint ppb) => HResult.S_OK;

    public int SetSelection(IShellItemArray? psia)
    {
        Selection = psia;
        return HResult.S_OK;
    }

    /// <summary>QueryInterface the held array for riid; E_FAIL with no selection.</summary>
    public int GetSelection(in Guid riid, out nint ppv) => throw new NotImplementedException();

    // Explorer's invocation details. None changes what a copy, cut or paste does.
    public int SetKeyState(uint grfKeyState) => HResult.S_OK;

    public int SetParameters(string? pszParameters) => HResult.S_OK;

    public int SetPosition(NativePoint pt) => HResult.S_OK;

    public int SetShowWindow(int nShow) => HResult.S_OK;

    public int SetNoShowUI(bool fNoShowUI) => HResult.S_OK;

    public int SetDirectory(string? pszDirectory) => HResult.S_OK;

    /// <summary>
    /// 1. <see cref="ComCallerSecurity.CallerIsAtLeastMediumIntegrity"/>, else E_ACCESSDENIED.
    /// 2. Reads every path from <see cref="Selection"/> with <see cref="ShellSelection.ReadPaths"/>
    /// (the array is an out-of-process proxy, so it is read here, synchronously, and then
    /// released). 3. Hands them to <see cref="Handler"/> and returns S_OK. No dialog, no
    /// file-system work and no waiting happens inside this call.
    /// </summary>
    public int Execute() => throw new NotImplementedException();
}
