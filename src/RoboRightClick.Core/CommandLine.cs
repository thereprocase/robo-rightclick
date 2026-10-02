namespace RoboRightClick.Core;

public abstract record CliCommand;

/// <summary>Run the tray app. <paramref name="StartedByCom"/> is true for COM's "-Embedding" launch.</summary>
public sealed record CliRunTray(bool StartedByCom) : CliCommand;

public sealed record CliInstall(bool StartWithWindows) : CliCommand;

public sealed record CliUninstall : CliCommand;

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
        RoboRightClick --install [--no-autostart]
        RoboRightClick --uninstall
        RoboRightClick copy <path>...      Robo-Copy the items (same path as the right-click)
        RoboRightClick cut <path>...       Robo-Cut the items
        RoboRightClick paste <folder>      Robo-Paste the clipboard into the folder
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
        if (Is(first, "--help") || Is(first, "-h") || Is(first, "/?"))
        {
            return new CliHelp();
        }
        if (Is(first, "--install"))
        {
            if (rest.Count == 0)
            {
                return new CliInstall(StartWithWindows: true);
            }
            return rest.Count == 1 && Is(rest[0], "--no-autostart")
                ? new CliInstall(StartWithWindows: false)
                : Unexpected(rest.FirstOrDefault(a => !Is(a, "--no-autostart")) ?? rest[^1]);
        }
        if (Is(first, "--uninstall"))
        {
            return rest.Count == 0 ? new CliUninstall() : Unexpected(rest[0]);
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

    private static bool Is(string arg, string expected) => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);

    private static CliError Unexpected(string arg) => new($"Unexpected argument '{arg}'.");
}
