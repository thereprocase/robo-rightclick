namespace RoboRightClick.Com;

/// <summary>
/// The COM server's caller restrictions (descriptors from <see cref="Core.ComSecurity"/>).
/// Without them any same-user process, including a low-integrity sandbox, could make the
/// medium-integrity tray copy or move files.
/// </summary>
internal static class ComCallerSecurity
{
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
    public static void InitializeProcess(string userSid) => throw new NotImplementedException();

    /// <summary>
    /// Inside an incoming call: CoImpersonateClient, OpenThreadToken, GetTokenInformation
    /// (TokenIntegrityLevel), CoRevertToSelf. True when the caller is at medium integrity or
    /// above. Defense in depth behind the descriptor's mandatory label; false on any failure.
    /// </summary>
    public static bool CallerIsAtLeastMediumIntegrity() => throw new NotImplementedException();
}
