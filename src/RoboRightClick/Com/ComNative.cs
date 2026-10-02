using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RoboRightClick.Com;

/// <summary>ole32/shell32 entry points for the COM server and client.</summary>
internal static partial class ComNative
{
    /// <summary>
    /// The one ComWrappers instance for the process: converts [GeneratedComClass] objects
    /// to COM pointers (server side) and COM pointers to [GeneratedComInterface] proxies
    /// (client side).
    /// </summary>
    public static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32.dll")]
    public static partial int CoRegisterClassObject(in Guid rclsid, nint pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    public static partial int CoRevokeClassObject(uint dwRegister);

    [LibraryImport("ole32.dll")]
    public static partial int CoResumeClassObjects();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    /// <summary>Returns a PIDL to free with <see cref="ILFree"/>.</summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    /// <summary>The array copies the PIDLs; the caller still frees its own.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHCreateShellItemArrayFromIDLists(uint cidl, nint[] rgpidl, out nint ppsiItemArray);

    [LibraryImport("shell32.dll")]
    public static partial void ILFree(nint pidl);
}
