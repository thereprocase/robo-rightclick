using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RoboRightClick.Com;

// COM interfaces used by the local server and the CLI client. Every method is declared
// in vtable order: the order in the IDL, not the alphabetical order of the Microsoft
// Learn method tables. IIDs and order checked against shobjidl_core.h as mirrored in
// Wine's and ReactOS's shobjidl.idl. Methods the app never calls are still declared,
// with pointer-sized placeholders, because they occupy vtable slots.

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
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
}

internal static class ShellConstants
{
    /// <summary>SIGDN_FILESYSPATH: the item's file-system path; fails for non-file-system items.</summary>
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    public const uint SFGAO_STREAM = 0x00400000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;

    public const uint CLSCTX_LOCAL_SERVER = 0x4;

    /// <summary>One registration serves every activation: each right-click reaches this process.</summary>
    public const uint REGCLS_MULTIPLEUSE = 1;

    /// <summary>Register all three classes, then expose them together with CoResumeClassObjects.</summary>
    public const uint REGCLS_SUSPENDED = 4;
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
    int SetSelection(IShellItemArray? psia);

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
    int GetItemAt(uint dwIndex, out IShellItem? ppsi);

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
    int GetParent(out IShellItem? ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem? psi, uint hint, out int piOrder);
}
