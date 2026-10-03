namespace RoboRightClick.Core;

public abstract record CliCommand;

/// <summary>Run the tray app. <paramref name="StartedByCom"/> is true for COM's "-Embedding" launch.</summary>
/// <param name="AfterInstall">
/// "--after-install": the installer started this tray. It is the one durable first-run
/// signal (the hint about pinning the tray icon shows once per install) and needs no marker
/// file, which ephemeral mode would forbid and uninstall would have to know about.
/// </param>
public sealed record CliRunTray(bool StartedByCom, bool AfterInstall = false) : CliCommand;

/// <param name="StartWithWindows">
/// --autostart (true), --no-autostart (false) or neither (null: keep an existing config's
/// choice; see <see cref="Registration.ResolveStartWithWindows"/>).
/// </param>
/// <param name="Quiet">--quiet: no message box; the exit code is the only result (scripts, e2e).</param>
/// <param name="Force">--force: allow replacing a newer installed version with this older one.</param>
public sealed record CliInstall(bool? StartWithWindows, bool Quiet = false, bool Force = false) : CliCommand;

/// <param name="Quiet">--quiet: no message box; the exit code is the only result.</param>
public sealed record CliUninstall(bool Quiet = false) : CliCommand;

/// <summary>
/// Drive a verb through the same COM path a right-click takes. Paths are passed as
/// typed; the host resolves relative paths against the working directory.
/// </summary>
public sealed record CliInvokeVerb(ShellVerb Verb, IReadOnlyList<string> Paths) : CliCommand;

public sealed record CliHelp : CliCommand;

public sealed record CliError(string Message) : CliCommand;

public static class CliExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
}

public static class CommandLine
{
    public const string Usage =
        """
        RoboRightClick                     run the tray app
        RoboRightClick --install [--autostart | --no-autostart] [--quiet] [--force]
        RoboRightClick --uninstall [--quiet]
        RoboRightClick copy <path>...      Robo-Copy the items (same path as the right-click)
        RoboRightClick cut <path>...       Robo-Cut the items
        RoboRightClick paste <folder>      Robo-Paste the clipboard into the folder

        This is a Windows GUI program: cmd and PowerShell do not wait for it. In scripts use
        "start /wait RoboRightClick ..." or "Start-Process -Wait -PassThru" to get the exit code.
        --install installs, or updates or repairs an installed version; a newer installed version is
        kept unless --force. --quiet skips the install and uninstall message box; the exit code is the result.
        """;

    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new CliRunTray(StartedByCom: false);
        }

        var first = args[0];
        var rest = args.Skip(1).ToList();

        // COM documents "-Embedding"; "/Embedding" is accepted too because some
        // activation paths have used it.
        if (Is(first, "-Embedding") || Is(first, "/Embedding"))
        {
            return rest.Count == 0 ? new CliRunTray(StartedByCom: true) : Unexpected(rest[0]);
        }
        if (Is(first, AfterInstallSwitch))
        {
            return rest.Count == 0 ? new CliRunTray(StartedByCom: false, AfterInstall: true) : Unexpected(rest[0]);
        }
        if (Is(first, "--help") || Is(first, "-h") || Is(first, "/?"))
        {
            return new CliHelp();
        }
        if (Is(first, "--install"))
        {
            bool? startWithWindows = null;
            var quiet = false;
            var force = false;
            foreach (var option in rest)
            {
                if (Is(option, QuietSwitch) && !quiet)
                {
                    quiet = true;
                }
                else if (Is(option, ForceSwitch) && !force)
                {
                    force = true;
                }
                else if ((Is(option, "--autostart") || Is(option, "--no-autostart")) && startWithWindows is null)
                {
                    startWithWindows = Is(option, "--autostart");
                }
                else
                {
                    // A repeated or contradictory option is refused rather than "last one wins".
                    return Unexpected(option);
                }
            }
            return new CliInstall(startWithWindows, quiet, force);
        }
        if (Is(first, "--uninstall"))
        {
            if (rest.Count == 0)
            {
                return new CliUninstall();
            }
            if (rest.Count == 1 && Is(rest[0], QuietSwitch))
            {
                return new CliUninstall(Quiet: true);
            }
            return Unexpected(Is(rest[0], QuietSwitch) ? rest[1] : rest[0]);
        }

        var verb = first.ToLowerInvariant() switch
        {
            "copy" => ShellVerb.RoboCopy,
            "cut" => ShellVerb.RoboCut,
            "paste" => (ShellVerb?)ShellVerb.RoboPaste,
            _ => null,
        };
        if (verb is null)
        {
            return new CliError($"Unknown command '{first}'.");
        }
        if (rest.Count == 0 || rest.Any(string.IsNullOrWhiteSpace))
        {
            return new CliError($"'{first}' needs at least one path.");
        }
        if (verb == ShellVerb.RoboPaste && rest.Count != 1)
        {
            return new CliError("'paste' takes exactly one destination folder.");
        }
        return new CliInvokeVerb(verb.Value, rest);
    }

    /// <summary>Passed by the installer when it starts the installed tray; not meant for users.</summary>
    public const string AfterInstallSwitch = "--after-install";

    public const string QuietSwitch = "--quiet";

    public const string ForceSwitch = "--force";

    private static bool Is(string arg, string expected) => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);

    private static CliError Unexpected(string arg) => new($"Unexpected argument '{arg}'.");
}
