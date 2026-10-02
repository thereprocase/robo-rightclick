using RoboRightClick.Core;

namespace RoboRightClick.Cli;

/// <summary>
/// Non-tray commands: verb invocation through <see cref="Com.ComClient"/>, help and
/// usage errors. Attaches to the parent console (AppNative.AttachConsole) so scripted
/// runs see output; exit codes are <see cref="CliExitCodes"/>. The exe is a GUI-subsystem
/// program, so shells do not wait for it: scripts use "start /wait" or
/// "Start-Process -Wait -PassThru" (see <see cref="CommandLine.Usage"/>).
/// </summary>
internal static class CliRunner
{
    /// <summary>
    /// Resolves each path with Path.GetFullPath against the working directory, then
    /// ComClient.Invoke. Returns Ok once the tray has accepted the verb (not when a paste
    /// finishes), Failed otherwise, with a message: REGDB_E_CLASSNOTREG becomes
    /// "RoboRightClick is not installed for this user; run --install", E_ACCESSDENIED
    /// names the integrity check, and a path SHParseDisplayName rejects is named.
    /// </summary>
    public static int InvokeVerb(CliInvokeVerb command) => throw new NotImplementedException();

    /// <summary>Prints <see cref="CommandLine.Usage"/>, preceded by the error if there is one.</summary>
    public static int PrintUsage(string? error) => throw new NotImplementedException();
}
