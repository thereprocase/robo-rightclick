using System.Runtime.InteropServices;
using RoboRightClick.App;
using RoboRightClick.Com;
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
    public static int InvokeVerb(CliInvokeVerb command)
    {
        AttachConsole();
        try
        {
            var fullPaths = command.Paths.Select(Path.GetFullPath).ToList();
            ComClient.Invoke(command.Verb, fullPaths);
            return CliExitCodes.Ok;
        }
        catch (COMException ex) when (ex.HResult == HResult.REGDB_E_CLASSNOTREG)
        {
            return Fail("RoboRightClick is not installed for this user; run --install.");
        }
        catch (COMException ex) when (ex.HResult == HResult.E_ACCESSDENIED)
        {
            return Fail("The tray refused the call: the caller must run at medium integrity or above, as the signed-in user.");
        }
        catch (COMException ex)
        {
            return Fail($"The tray could not run the command (0x{ex.HResult:X8}). {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return Fail($"A path is not valid: {ex.Message}");
        }
        catch (Exception ex)
        {
            // A script depends on the documented exit codes: anything unexpected (a proxy
            // cast the server refuses, for example) is a failure, not a crash dialog.
            return Fail($"The command failed: {ex.Message}");
        }
    }

    /// <summary>Prints <see cref="CommandLine.Usage"/>, preceded by the error if there is one.</summary>
    public static int PrintUsage(string? error)
    {
        AttachConsole();
        if (error is null)
        {
            Console.Out.WriteLine(CommandLine.Usage);
            return CliExitCodes.Ok;
        }

        Console.Error.WriteLine(error);
        Console.Error.WriteLine();
        Console.Error.WriteLine(CommandLine.Usage);
        return CliExitCodes.Usage;
    }

    /// <summary>
    /// A GUI-subsystem exe starts without a console. When launched from cmd or PowerShell
    /// this joins theirs; with redirected handles Console already writes to those. Failure
    /// (started from Explorer, no parent console) just means no output, which is correct.
    /// </summary>
    private static void AttachConsole() => AppNative.AttachConsole(AppNative.ATTACH_PARENT_PROCESS);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return CliExitCodes.Failed;
    }
}
