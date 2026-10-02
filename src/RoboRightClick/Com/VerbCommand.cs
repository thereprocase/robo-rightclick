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

    /// <summary>Holds Explorer's array until Execute; released (set to null) once read.</summary>
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
    /// Reads every path from <see cref="Selection"/> with <see cref="ShellSelection.ReadPaths"/>
    /// (the array is an out-of-process proxy, so it is read here, synchronously, and then
    /// released), hands them to <see cref="Handler"/> and returns S_OK. No dialog, no
    /// file-system work and no waiting happens inside this call.
    /// </summary>
    public int Execute() => throw new NotImplementedException();
}
