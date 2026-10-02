using System.Runtime.InteropServices;

namespace RoboRightClick.Com;

/// <summary>
/// The COM server's caller restrictions (descriptors from <see cref="Core.ComSecurity"/>).
/// Without them any same-user process, including a low-integrity sandbox, could make the
/// medium-integrity tray copy or move files.
/// </summary>
internal static class ComCallerSecurity
{
    /// <summary>SECURITY_MANDATORY_MEDIUM_RID.</summary>
    private const uint MediumIntegrityRid = 0x2000;

    /// <summary>A TOKEN_MANDATORY_LABEL is a pointer plus a DWORD; real ones are well under 100 bytes.</summary>
    private const uint MaxLabelBytes = 256;

    /// <summary>
    /// CoInitializeSecurity once per tray process, on the UI thread, before any COM object is
    /// created, marshaled or registered and before WinForms creates a window (OLE initializes
    /// lazily for the clipboard and drag-and-drop): the access descriptor from
    /// <see cref="Core.ComSecurity.AccessPermissionSddl"/> converted with
    /// ConvertStringSecurityDescriptorToSecurityDescriptor, RPC_C_AUTHN_LEVEL_PKT_PRIVACY,
    /// RPC_C_IMP_LEVEL_IDENTIFY, EOAC_NONE. Throws COMException on failure: a tray that could
    /// not restrict its callers must not register its class objects. Not called by the CLI
    /// client process.
    /// </summary>
    public static void InitializeProcess(string userSid)
    {
        var sddl = Core.ComSecurity.AccessPermissionSddl(userSid);
        if (!ComNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, ComNative.SDDL_REVISION_1, out var descriptor, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new COMException(
                $"The COM access descriptor could not be built (Win32 error {error}).",
                HResult.E_FAIL);
        }

        nint absolute;
        try
        {
            absolute = ToAbsolute(descriptor);
        }
        finally
        {
            ComNative.LocalFree(descriptor);
        }

        // cAuthSvc = -1 lets COM pick its authentication services; the descriptor is the
        // part that matters. The call fails if COM security was already initialized,
        // which is the point: it must be the first COM call of the process.
        var hr = ComNative.CoInitializeSecurity(
            absolute, -1, 0, 0,
            ComNative.RPC_C_AUTHN_LEVEL_PKT_PRIVACY, ComNative.RPC_C_IMP_LEVEL_IDENTIFY,
            0, ComNative.EOAC_NONE, 0);
        ComNative.ThrowIfFailed(hr, "CoInitializeSecurity");
    }

    /// <summary>
    /// CoInitializeSecurity accepts only an absolute-format descriptor; the SDDL converter
    /// produces a self-relative one, which COM rejects with ERROR_INVALID_SECURITY_DESCR
    /// (0x80070551, observed on Windows 11 build 26200, docs/testlog.md 2026-10-02).
    /// The returned buffers are never freed: COM documents no point after which it stops
    /// reading the descriptor, and they are a few hundred bytes allocated once per process.
    /// </summary>
    private static nint ToAbsolute(nint selfRelative)
    {
        uint sdSize = 0, daclSize = 0, saclSize = 0, ownerSize = 0, groupSize = 0;
        // Sizing call: expected to fail with ERROR_INSUFFICIENT_BUFFER and fill the sizes.
        ComNative.MakeAbsoluteSD(
            selfRelative, 0, ref sdSize, 0, ref daclSize, 0, ref saclSize, 0, ref ownerSize, 0, ref groupSize);
        if (sdSize == 0)
        {
            throw new COMException(
                $"The COM access descriptor could not be sized (Win32 error {Marshal.GetLastPInvokeError()}).",
                HResult.E_FAIL);
        }

        var sd = Allocate(sdSize);
        var dacl = Allocate(daclSize);
        var sacl = Allocate(saclSize);
        var owner = Allocate(ownerSize);
        var group = Allocate(groupSize);
        var buffers = new[] { sd, dacl, sacl, owner, group };
        if (!ComNative.MakeAbsoluteSD(
                selfRelative, sd, ref sdSize, dacl, ref daclSize, sacl, ref saclSize, owner, ref ownerSize, group, ref groupSize))
        {
            var error = Marshal.GetLastPInvokeError();
            foreach (var buffer in buffers)
            {
                Marshal.FreeHGlobal(buffer);
            }
            throw new COMException(
                $"The COM access descriptor could not be made absolute (Win32 error {error}).",
                HResult.E_FAIL);
        }
        return sd;
    }

    /// <summary>Zero bytes (an absent DACL, SACL, owner or group) is a null pointer, which MakeAbsoluteSD accepts.</summary>
    private static nint Allocate(uint size) => size == 0 ? 0 : Marshal.AllocHGlobal((int)size);

    /// <summary>
    /// Inside an incoming call: CoImpersonateClient, OpenThreadToken, GetTokenInformation
    /// (TokenIntegrityLevel), CoRevertToSelf. True when the caller is at medium integrity or
    /// above. Defense in depth behind the descriptor's mandatory label; false on any failure.
    /// </summary>
    public static bool CallerIsAtLeastMediumIntegrity()
    {
        if (ComNative.CoImpersonateClient() < 0)
        {
            return false;
        }

        var token = nint.Zero;
        var result = false;
        try
        {
            // openAsSelf: the check must use the server's identity. At identification
            // level the impersonated identity cannot be used for access checks.
            if (ComNative.OpenThreadToken(ComNative.GetCurrentThread(), ComNative.TOKEN_QUERY, true, out token))
            {
                result = ReadIntegrityRid(token) is { } rid && rid >= MediumIntegrityRid;
            }
        }
        catch (Exception)
        {
            result = false;
        }
        finally
        {
            if (token != nint.Zero)
            {
                ComNative.CloseHandle(token);
            }
            // A thread left impersonating the caller would run the rest of the tray as it.
            // The revert has no way to fail in practice; if it does, refuse the call.
            if (ComNative.CoRevertToSelf() < 0)
            {
                result = false;
            }
        }
        return result;
    }

    private static uint? ReadIntegrityRid(nint token)
    {
        ComNative.GetTokenInformation(token, ComNative.TokenIntegrityLevel, 0, 0, out var needed);
        if (needed == 0 || needed > MaxLabelBytes)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!ComNative.GetTokenInformation(token, ComNative.TokenIntegrityLevel, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL begins with the label SID's pointer.
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == nint.Zero)
            {
                return null;
            }
            var countPointer = ComNative.GetSidSubAuthorityCount(sid);
            if (countPointer == nint.Zero)
            {
                return null;
            }
            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return null;
            }
            var ridPointer = ComNative.GetSidSubAuthority(sid, (uint)(count - 1));
            return ridPointer == nint.Zero ? null : (uint)Marshal.ReadInt32(ridPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
