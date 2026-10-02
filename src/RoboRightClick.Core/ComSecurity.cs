using System.Text.RegularExpressions;

namespace RoboRightClick.Core;

/// <summary>
/// Who may launch and call the app's COM server, as SDDL. Without an explicit descriptor
/// the server runs under machine defaults, and nothing stops a low-integrity process of
/// the same user (a browser renderer, a protected-view document) from calling Execute and
/// making the medium-integrity tray copy or move files. The host applies the access
/// descriptor with CoInitializeSecurity at startup and install writes both descriptors
/// to the AppID key (converted to binary).
/// </summary>
/// <remarks>
/// Rights are COM_RIGHTS_*: EXECUTE 0x1, EXECUTE_LOCAL 0x2, ACTIVATE_LOCAL 0x8. Remote
/// rights are never granted. The mandatory label "S:(ML;;NX;;;ME)" is
/// SYSTEM_MANDATORY_LABEL_NO_EXECUTE_UP at medium integrity: COM evaluates its rights as
/// execute rights, so callers below medium integrity are refused.
/// </remarks>
public static partial class ComSecurity
{
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")]
    private static partial Regex SidPattern();

    /// <summary>Calls into the running server: EXECUTE | EXECUTE_LOCAL for the user and SYSTEM.</summary>
    public static string AccessPermissionSddl(string userSid) => Build(userSid, "0x3");

    /// <summary>Starting the server: EXECUTE | EXECUTE_LOCAL | ACTIVATE_LOCAL for the user and SYSTEM.</summary>
    public static string LaunchPermissionSddl(string userSid) => Build(userSid, "0xb");

    private static string Build(string userSid, string rights)
    {
        // The SID is spliced into SDDL, so only the canonical "S-1-..." form is accepted.
        if (!SidPattern().IsMatch(userSid))
        {
            throw new ArgumentException("Expected a SID in S-1-... form.", nameof(userSid));
        }
        return $"O:{userSid}G:{userSid}D:(A;;{rights};;;{userSid})(A;;{rights};;;SY)S:(ML;;NX;;;ME)";
    }
}
