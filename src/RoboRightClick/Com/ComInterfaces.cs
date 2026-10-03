using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RoboRightClick.Com;

// COM interfaces used by the local server and the CLI client. Every method is declared
// in vtable order: the order in the IDL, not the alphabetical order of the Microsoft
// Learn method tables. IIDs and order checked against shobjidl_core.h as mirrored in
// Wine's and ReactOS's shobjidl.idl. Methods the app never calls are still declared,
// with pointer-sized placeholders, because they occupy vtable slots.
//
// Interface parameters the app releases itself use UniqueComInterfaceMarshaller. The
// default ComInterfaceMarshaller returns a cached, shared wrapper, and
// ComObject.FinalRelease does nothing on one of those (it acts only on unique
// instances), which would leave Explorer's proxies to the finalizer thread.

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    /// <summary>The class is not registered for this user: the CLI's "not installed" case.</summary>
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
}

internal static class ShellConstants
{
    /// <summary>SIGDN_FILESYSPATH: the item's file-system path; fails for non-file-system items.</summary>
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    public const uint SFGAO_STREAM = 0x00400000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;

    public const uint CLSCTX_LOCAL_SERVER = 0x4;

    /// <summary>Clipboard format of a file list (wtypes.h CF_HDROP).</summary>
    public const ushort CF_HDROP = 15;

    public const uint DVASPECT_CONTENT = 1;

    /// <summary>TYMED_HGLOBAL: the data is in a global memory block.</summary>
    public const uint TYMED_HGLOBAL = 1;

    /// <summary>BHID_DataObject: BindToHandler yields the item array's IDataObject.</summary>
    public static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    /// <summary>One registration serves every activation: each right-click reaches this process.</summary>
    public const uint REGCLS_MULTIPLEUSE = 1;

    /// <summary>Register all three classes, then expose them together with CoResumeClassObjects.</summary>
    public const uint REGCLS_SUSPENDED = 4;
}

/// <summary>objidl.h FORMATETC. Sequential layout gives the native padding after cfFormat.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    public ushort cfFormat;
    public nint ptd;
    public uint dwAspect;
    public int lindex;
    public uint tymed;
}

/// <summary>objidl.h STGMEDIUM. <c>data</c> is the union: an HGLOBAL for TYMED_HGLOBAL.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct STGMEDIUM
{
    public uint tymed;
    public nint data;
    public nint pUnkForRelease;
}

/// <summary>unknwn.h. Slots: QueryInterface, AddRef, Release, CreateInstance, LockServer.</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>shobjidl_core.h (Windows 7+). Explorer calls every setter, then Execute.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54")]
internal partial interface IExecuteCommand
{
    [PreserveSig]
    int SetKeyState(uint grfKeyState);

    [PreserveSig]
    int SetParameters(string? pszParameters);

    [PreserveSig]
    int SetPosition(NativePoint pt);

    [PreserveSig]
    int SetShowWindow(int nShow);

    [PreserveSig]
    int SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool fNoShowUI);

    [PreserveSig]
    int SetDirectory(string? pszDirectory);

    [PreserveSig]
    int Execute();
}

/// <summary>shobjidl_core.h (Vista+). The selection arrives here before Execute.</summary>
[GeneratedComInterface]
[Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
internal partial interface IObjectWithSelection
{
    [PreserveSig]
    int SetSelection([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellItemArray>))] IShellItemArray? psia);

    [PreserveSig]
    int GetSelection(in Guid riid, out nint ppv);
}

/// <summary>shobjidl_core.h (Vista+). Optional; Explorer passes the verb name and a property bag.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("85075ACF-231F-40EA-9610-D26B7B58F638")]
internal partial interface IInitializeCommand
{
    [PreserveSig]
    int Initialize(string? pszCommandName, nint ppb);
}

/// <summary>shobjidl_core.h. Placeholder slots: GetPropertyStore, GetPropertyDescriptionList, EnumItems.</summary>
[GeneratedComInterface]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
internal partial interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out nint ppv);

    /// <summary>keyType is a REFPROPERTYKEY (pointer).</summary>
    [PreserveSig]
    int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellItem>))] out IShellItem? ppsi);

    [PreserveSig]
    int EnumItems(out nint ppenumShellItems);
}

/// <summary>shobjidl_core.h. GetDisplayName's string is CoTaskMem-allocated; the caller frees it.</summary>
[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetParent([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellItem>))] out IShellItem? ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem? psi, uint hint, out int piOrder);
}

/// <summary>
/// objidl.h. Only GetData is called. The rest hold vtable slots with pointer-sized
/// placeholders. Slots: GetData, GetDataHere, QueryGetData, GetCanonicalFormatEtc, SetData,
/// EnumFormatEtc, DAdvise, DUnadvise, EnumDAdvise.
/// </summary>
[GeneratedComInterface]
[Guid("0000010E-0000-0000-C000-000000000046")]
internal partial interface IDataObject
{
    [PreserveSig]
    int GetData(in FORMATETC pformatetcIn, out STGMEDIUM pmedium);

    /// <summary>pformatetc and pmedium are pointers.</summary>
    [PreserveSig]
    int GetDataHere(nint pformatetc, nint pmedium);

    [PreserveSig]
    int QueryGetData(in FORMATETC pformatetc);

    [PreserveSig]
    int GetCanonicalFormatEtc(nint pformatectIn, nint pformatetcOut);

    [PreserveSig]
    int SetData(nint pformatetc, nint pmedium, [MarshalAs(UnmanagedType.Bool)] bool fRelease);

    [PreserveSig]
    int EnumFormatEtc(uint dwDirection, out nint ppenumFormatEtc);

    [PreserveSig]
    int DAdvise(nint pformatetc, uint advf, nint pAdvSink, out uint pdwConnection);

    [PreserveSig]
    int DUnadvise(uint dwConnection);

    [PreserveSig]
    int EnumDAdvise(out nint ppenumAdvise);
}

// ---------------------------------------------------------------------------
// The Robo-Paste hotkey's folder lookup (Verbs/ExplorerFolderLocator). Client side only:
// the tray calls these on File Explorer's objects from its locator thread (MTA).
// Slots checked against Wine's exdisp.idl (IShellWindows), servprov.idl
// (IServiceProvider), oleidl.idl (IOleWindow) and shobjidl.idl (IShellBrowser,
// IFolderView), 2026-10-02.
// ---------------------------------------------------------------------------

/// <summary>
/// oaidl.h VARIANT holding a VT_I4, laid out for win-x64 (24 bytes: vt, three reserved
/// words, then a 16-byte union whose first four bytes are lVal). A plain blittable struct,
/// because the COM source generator passes ComVariant by value only with runtime
/// marshalling disabled for the whole assembly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct VariantInt32
{
    private const ushort VT_I4 = 3;

    private readonly ushort _vt;
    private readonly ushort _reserved1;
    private readonly ushort _reserved2;
    private readonly ushort _reserved3;
    private readonly long _value;
    private readonly nint _record;

    public VariantInt32(int value)
    {
        _vt = VT_I4;
        // lVal is the low half of the union's first eight bytes (little-endian).
        _value = (uint)value;
    }
}

internal static class ShellWindowsConstants
{
    /// <summary>exdisp.idl coclass ShellWindows: File Explorer's collection of open folder windows (one entry per tab).</summary>
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    public static readonly Guid IID_IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    public static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>shlguid.h SID_STopLevelBrowser: the browser that owns a ShellWindows entry.</summary>
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>knownfolders.h FOLDERID_Desktop.</summary>
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    /// <summary>KF_FLAG_DONT_VERIFY: the path as configured, without touching the disk (or a redirected share).</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    /// <summary>CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER: File Explorer registers ShellWindows as a running class object.</summary>
    public const uint CLSCTX_SERVER = 0x1 | 0x4;
}

/// <summary>
/// exdisp.idl, a dual interface: IDispatch's four slots come first and are declared as
/// placeholders. Only GetCount and Item are called.
/// </summary>
[GeneratedComInterface]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
internal partial interface IShellWindows
{
    // IDispatch
    [PreserveSig]
    int GetTypeInfoCount(out uint pctinfo);

    [PreserveSig]
    int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo);

    [PreserveSig]
    int GetIDsOfNames(in Guid riid, nint rgszNames, uint cNames, uint lcid, nint rgDispId);

    [PreserveSig]
    int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, nint pDispParams, nint pVarResult, nint pExcepInfo, nint puArgErr);

    // IShellWindows
    /// <summary>[propget] Count.</summary>
    [PreserveSig]
    int GetCount(out int count);

    /// <summary>The entry's IDispatch, or S_FALSE with null past the end. The index is a VARIANT passed by value.</summary>
    [PreserveSig]
    int Item(VariantInt32 index, out nint folder);

    [PreserveSig]
    int NewEnum(out nint ppunk);

    [PreserveSig]
    int Register(nint pid, int hwnd, int swClass, out int plCookie);

    [PreserveSig]
    int RegisterPending(int lThreadId, nint pvarloc, nint pvarlocRoot, int swClass, out int plCookie);

    [PreserveSig]
    int Revoke(int lCookie);

    [PreserveSig]
    int OnNavigate(int lCookie, nint pvarLoc);

    [PreserveSig]
    int OnActivated(int lCookie, short fActive);

    [PreserveSig]
    int FindWindowSW(nint pvarLoc, nint pvarLocRoot, int swClass, out int phwnd, int swfwOptions, out nint ppdispOut);

    [PreserveSig]
    int OnCreated(int lCookie, nint punk);

    [PreserveSig]
    int ProcessAttachDetach(short fAttach);
}

/// <summary>
/// servprov.idl IServiceProvider (named apart from System.IServiceProvider). One slot:
/// the IDL's RemoteQueryService is the [call_as] wire form of QueryService, not a second slot.
/// </summary>
[GeneratedComInterface]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal partial interface IOleServiceProvider
{
    [PreserveSig]
    int QueryService(in Guid guidService, in Guid riid, out nint ppvObject);
}

/// <summary>
/// shobjidl.idl IShellBrowser, with its base IOleWindow (oleidl.idl) flattened in front.
/// Slots after IUnknown: GetWindow 3, ContextSensitiveHelp 4, InsertMenusSB 5 ...
/// SendControlMsg 14 ([local], still a slot), QueryActiveShellView 15, OnViewWindowActive 16,
/// SetToolbarItems 17. Only GetWindow and QueryActiveShellView are called.
/// </summary>
[GeneratedComInterface]
[Guid("000214E2-0000-0000-C000-000000000046")]
internal partial interface IShellBrowser
{
    // IOleWindow
    /// <summary>For File Explorer this is expected to be the tab's ShellTabWindowClass window (release gate).</summary>
    [PreserveSig]
    int GetWindow(out nint phwnd);

    [PreserveSig]
    int ContextSensitiveHelp(int fEnterMode);

    // IShellBrowser
    [PreserveSig]
    int InsertMenusSB(nint hmenuShared, nint lpMenuWidths);

    [PreserveSig]
    int SetMenuSB(nint hmenuShared, nint holemenuReserved, nint hwndActiveObject);

    [PreserveSig]
    int RemoveMenusSB(nint hmenuShared);

    [PreserveSig]
    int SetStatusTextSB(nint pszStatusText);

    [PreserveSig]
    int EnableModelessSB(int fEnable);

    [PreserveSig]
    int TranslateAcceleratorSB(nint pmsg, ushort wID);

    [PreserveSig]
    int BrowseObject(nint pidl, uint wFlags);

    [PreserveSig]
    int GetViewStateStream(uint grfMode, out nint ppStrm);

    [PreserveSig]
    int GetControlWindow(uint id, out nint phwnd);

    [PreserveSig]
    int SendControlMsg(uint id, uint uMsg, nint wParam, nint lParam, nint pret);

    /// <summary>The tab's IShellView, with a reference the caller releases.</summary>
    [PreserveSig]
    int QueryActiveShellView(out nint ppshv);

    [PreserveSig]
    int OnViewWindowActive(nint pshv);

    [PreserveSig]
    int SetToolbarItems(nint lpButtons, uint nButtons, uint uFlags);
}

/// <summary>shobjidl.idl IFolderView (Vista+). Slots after IUnknown: GetCurrentViewMode 3, SetCurrentViewMode 4, GetFolder 5. Only GetFolder is called.</summary>
[GeneratedComInterface]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
internal partial interface IFolderView
{
    [PreserveSig]
    int GetCurrentViewMode(out uint mode);

    [PreserveSig]
    int SetCurrentViewMode(uint mode);

    /// <summary>The folder the view shows, as riid (IShellItem here).</summary>
    [PreserveSig]
    int GetFolder(in Guid riid, out nint ppv);

    [PreserveSig]
    int Item(int index, out nint ppidl);

    [PreserveSig]
    int ItemCount(uint flags, out int items);

    [PreserveSig]
    int Items(uint flags, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetSelectionMarkedItem(out int item);

    [PreserveSig]
    int GetFocusedItem(out int item);

    [PreserveSig]
    int GetItemPosition(nint pidl, out NativePoint ppt);

    [PreserveSig]
    int GetSpacing(nint pt);

    [PreserveSig]
    int GetDefaultSpacing(out NativePoint pt);

    [PreserveSig]
    int GetAutoArrange();

    [PreserveSig]
    int SelectItem(int item, uint flags);

    [PreserveSig]
    int SelectAndPositionItems(uint cidl, nint apidl, nint apt, uint flags);
}
