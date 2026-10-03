using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RoboRightClick.Com;

/// <summary>ole32/shell32/advapi32 entry points for the COM server and client.</summary>
internal static partial class ComNative
{
    /// <summary>
    /// The one ComWrappers instance for the process: converts [GeneratedComClass] objects
    /// to COM pointers (server side) and COM pointers to [GeneratedComInterface] proxies
    /// (client side).
    /// </summary>
    public static readonly StrategyBasedComWrappers Wrappers = new();

    public const uint RPC_C_AUTHN_LEVEL_PKT_PRIVACY = 6;
    public const uint RPC_C_IMP_LEVEL_IDENTIFY = 2;
    public const uint EOAC_NONE = 0;
    public const uint SDDL_REVISION_1 = 1;
    public const uint TOKEN_QUERY = 0x8;
    public const int TokenIntegrityLevel = 25;

    [LibraryImport("ole32.dll")]
    public static partial int CoRegisterClassObject(in Guid rclsid, nint pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    public static partial int CoRevokeClassObject(uint dwRegister);

    [LibraryImport("ole32.dll")]
    public static partial int CoResumeClassObjects();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    public const uint COINIT_MULTITHREADED = 0x0;

    /// <summary>S_OK, or S_FALSE when the thread was already in the MTA.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    /// <summary>Lets <see cref="CoCancelCall"/> cancel this thread's outgoing synchronous calls.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoEnableCallCancellation(nint pReserved);

    /// <summary>Cancels the outgoing call the given (native) thread is blocked in; that call returns RPC_E_CALL_CANCELED.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoCancelCall(uint dwThreadId, uint ulTimeout);

    /// <summary>The path is CoTaskMem-allocated; the caller frees it.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    /// <summary>Process-wide, once, before any COM object exists.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeSecurity(
        nint pSecDesc, int cAuthSvc, nint asAuthSvc, nint pReserved1, uint dwAuthnLevel,
        uint dwImpLevel, nint pAuthList, uint dwCapabilities, nint pReserved3);

    /// <summary>Valid only inside an incoming call, on the thread serving it.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoImpersonateClient();

    [LibraryImport("ole32.dll")]
    public static partial int CoRevertToSelf();

    /// <summary>Lets the server process take foreground for calls made through this proxy (an IUnknown pointer).</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoAllowSetForegroundWindow(nint pUnk, nint lpvReserved);

    [LibraryImport("ole32.dll")]
    public static partial void ReleaseStgMedium(ref STGMEDIUM pmedium);

    /// <summary>The id of a named clipboard format (0xC000-0xFFFF), or 0 on failure.</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string format);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSdRevision, out nint securityDescriptor, out uint securityDescriptorSize);

    /// <summary>
    /// Copies a self-relative security descriptor into absolute form: the descriptor and
    /// its DACL, SACL, owner and group each in a caller-supplied buffer. Called once with
    /// zero sizes to learn them (fails with ERROR_INSUFFICIENT_BUFFER), then with buffers.
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MakeAbsoluteSD(
        nint pSelfRelativeSd,
        nint pAbsoluteSd, ref uint lpdwAbsoluteSdSize,
        nint pDacl, ref uint lpdwDaclSize,
        nint pSacl, ref uint lpdwSaclSize,
        nint pOwner, ref uint lpdwOwnerSize,
        nint pPrimaryGroup, ref uint lpdwPrimaryGroupSize);

    /// <summary>Frees what ConvertStringSecurityDescriptorToSecurityDescriptor allocated. Returns 0 on success.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint hMem);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenThreadToken(nint threadHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    /// <summary>Returns a pointer to the sub-authority count (a byte) inside the SID.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthorityCount(nint pSid);

    /// <summary>Returns a pointer to the sub-authority (a DWORD) inside the SID.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthority(nint pSid, uint nSubAuthority);

    /// <summary>Pseudo-handle for the calling thread; needs no CloseHandle.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    /// <summary>Length in bytes of a (self-relative) security descriptor.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial uint GetSecurityDescriptorLength(nint pSecurityDescriptor);

    /// <summary>Returns a PIDL to free with <see cref="ILFree"/>.</summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    /// <summary>The array copies the PIDLs; the caller still frees its own.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHCreateShellItemArrayFromIDLists(uint cidl, nint[] rgpidl, out nint ppsiItemArray);

    [LibraryImport("shell32.dll")]
    public static partial void ILFree(nint pidl);

    /// <summary>
    /// Throws COMException carrying the HRESULT itself. Marshal.ThrowExceptionForHR is not
    /// used because it turns some HRESULTs (E_ACCESSDENIED) into other exception types,
    /// and the CLI needs the number.
    /// </summary>
    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} failed (0x{hr:X8}).", hr);
        }
    }

    /// <summary>
    /// Releases a proxy now, on this thread. A no-op for objects that are not proxies of a
    /// foreign COM object, and also for proxies that are not unique instances: every proxy
    /// this must release is therefore created with CreateObjectFlags.UniqueInstance or
    /// marshalled with UniqueComInterfaceMarshaller. Proxies are never left to the
    /// finalizer, which would release a cross-process reference from the wrong apartment at
    /// an unpredictable time.
    /// </summary>
    public static void FinalRelease(object? proxy)
    {
        if (proxy is ComObject comObject)
        {
            comObject.FinalRelease();
        }
    }
}
