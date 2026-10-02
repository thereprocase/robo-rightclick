using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public int Initialize(string? pszCommandName, nint ppb)
    {
        VerifyUiThread();
        return HResult.S_OK;
    }

    public int SetSelection(IShellItemArray? psia)
    {
        try
        {
            VerifyUiThread();
            ReleaseSelection();
            Selection = psia;
            return HResult.S_OK;
        }
        catch (Exception)
        {
            return HResult.E_FAIL;
        }
    }

    /// <summary>QueryInterface the held array for riid; E_FAIL with no selection.</summary>
    public unsafe int GetSelection(in Guid riid, out nint ppv)
    {
        ppv = 0;
        try
        {
            VerifyUiThread();
            if (Selection is null)
            {
                return HResult.E_FAIL;
            }

            // The marshaller hands back the foreign object's own interface pointer with a
            // reference of its own; a second QueryInterface then yields the requested one.
            var held = (nint)ComInterfaceMarshaller<IShellItemArray>.ConvertToUnmanaged(Selection);
            if (held == 0)
            {
                return HResult.E_FAIL;
            }
            try
            {
                var hr = Marshal.QueryInterface(held, in riid, out var requested);
                if (hr >= 0)
                {
                    ppv = requested;
                }
                return hr;
            }
            finally
            {
                Marshal.Release(held);
            }
        }
        catch (Exception)
        {
            ppv = 0;
            return HResult.E_FAIL;
        }
    }

    // Explorer's invocation details. None changes what a copy, cut or paste does.
    public int SetKeyState(uint grfKeyState) => HResult.S_OK;

    public int SetParameters(string? pszParameters) => HResult.S_OK;

    public int SetPosition(NativePoint pt) => HResult.S_OK;

    public int SetShowWindow(int nShow) => HResult.S_OK;

    public int SetNoShowUI(bool fNoShowUI) => HResult.S_OK;

    /// <summary>
    /// For a folder-background click this is the only place the folder arrives: Explorer
    /// passes no selection then. For a click on items it is their parent folder, and
    /// <see cref="ShellVerbs.InvocationItems"/> decides which of the two a verb uses.
    /// Untrusted like the selection; the handler checks it.
    /// </summary>
    public int SetDirectory(string? pszDirectory)
    {
        Directory = pszDirectory;
        return HResult.S_OK;
    }

    /// <summary>The folder Explorer set with SetDirectory, if any.</summary>
    public string? Directory { get; private set; }

    /// <summary>
    /// 1. <see cref="ComCallerSecurity.CallerIsAtLeastMediumIntegrity"/>, else E_ACCESSDENIED.
    /// 2. Reads every path from <see cref="Selection"/> with <see cref="ShellSelection.ReadPaths"/>
    /// (the array is an out-of-process proxy, so it is read here, synchronously, and then
    /// released). A background click has no selection. 3. <see cref="ShellVerbs.InvocationItems"/>
    /// picks the selection or, for a background paste, <see cref="Directory"/>; nothing at
    /// all is E_FAIL. 4. Hands the items to <see cref="Handler"/> and returns S_OK. No dialog,
    /// no file-system work and no waiting happens inside this call.
    /// </summary>
    public int Execute()
    {
        try
        {
            VerifyUiThread();
            if (!ComCallerSecurity.CallerIsAtLeastMediumIntegrity())
            {
                ReleaseSelection();
                return HResult.E_ACCESSDENIED;
            }

            var selection = new SelectionPaths([], 0);
            var readTimer = Stopwatch.StartNew();
            if (Selection is { } array)
            {
                try
                {
                    selection = ShellSelection.ReadPaths(array);
                }
                finally
                {
                    // Read once, then released: the array is a cross-process proxy.
                    ReleaseSelection();
                }
            }
            readTimer.Stop();

            var items = ShellVerbs.InvocationItems(Verb, selection.Paths, selection.SkippedItems, Directory);
            if (items.Count == 0 && selection.SkippedItems == 0)
            {
                return HResult.E_FAIL;
            }

            TraceExecute(items.Count, selection.SkippedItems, fromDirectory: !ReferenceEquals(items, selection.Paths), readTimer.Elapsed);
            Handler.Invoke(Verb, items, selection.SkippedItems);
            return HResult.S_OK;
        }
        catch (Exception)
        {
            // Nothing crosses into Explorer. The reason is not reported here: paths in an
            // exception message would reach a log that ephemeral mode forbids.
            return HResult.E_FAIL;
        }
    }

    /// <summary>
    /// One debug-output line per Execute (OutputDebugString through the default trace
    /// listener; nothing reaches a file): verb, item counts, how long the selection read
    /// took and how long after process start the call arrived. It is how a test sees that a
    /// whole selection arrived in one call and how long a COM cold start took. Counts and
    /// times only, never a path, so it is allowed in ephemeral mode.
    /// </summary>
    private void TraceExecute(int items, int skipped, bool fromDirectory, TimeSpan read)
    {
        var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
        Trace.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"RoboRightClick: Execute verb={Verb} items={items} skipped={skipped} fromDirectory={fromDirectory} readMs={read.TotalMilliseconds:F1} uptimeMs={uptime.TotalMilliseconds:F0}"));
    }

    private void ReleaseSelection()
    {
        var held = Selection;
        Selection = null;
        ComNative.FinalRelease(held);
    }

    private static bool _threadWarningLogged;

    /// <summary>
    /// The class objects are registered on the UI thread, so every call should arrive there.
    /// If the generated wrapper turns out to be agile that is false, and the unlocked state
    /// of this class would be unsafe: assert in Debug, write one trace line in Release.
    /// </summary>
    private static void VerifyUiThread()
    {
        if (Environment.CurrentManagedThreadId == ComServer.UiThreadId)
        {
            return;
        }
        Debug.Fail("VerbCommand was called off the UI thread.");
        if (!_threadWarningLogged)
        {
            _threadWarningLogged = true;
            Trace.TraceError("VerbCommand was called off the UI thread.");
        }
    }
}
