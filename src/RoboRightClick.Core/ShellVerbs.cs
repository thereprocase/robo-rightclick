namespace RoboRightClick.Core;

public enum ShellVerb
{
    RoboCopy,
    RoboCut,
    RoboPaste,
}

/// <summary>One classic context-menu item and the COM class Explorer delegates it to.</summary>
/// <param name="KeyName">Verb key name under each association's "shell" key.</param>
/// <param name="Associations">Keys under HKCU\Software\Classes that get this verb.</param>
public sealed record ShellVerbInfo(
    ShellVerb Verb,
    Guid Clsid,
    string KeyName,
    string Label,
    IReadOnlyList<string> Associations);

/// <summary>
/// The verb table. The CLSIDs are part of the installed registry footprint and of the
/// CLI's activation path: changing one orphans every existing install's keys.
/// </summary>
public static class ShellVerbs
{
    /// <summary>
    /// AppID shared by the three classes, registered the way Microsoft's
    /// ExecuteCommandVerb local-server sample registers its own.
    /// </summary>
    public static readonly Guid AppId = new("b708f29c-8ed8-40bd-832e-f05180f1b285");

    public static readonly ShellVerbInfo RoboCopy = new(
        ShellVerb.RoboCopy, new Guid("bd15dc6a-fbc1-4949-b61d-3b8fc390062f"),
        "RoboCopy", "Robo-Copy", ["AllFilesystemObjects"]);

    public static readonly ShellVerbInfo RoboCut = new(
        ShellVerb.RoboCut, new Guid("1a061376-a3f7-41bf-a516-e635ed91acdf"),
        "RoboCut", "Robo-Cut", ["AllFilesystemObjects"]);

    // Background = right-click on empty space inside a folder; Directory and Drive =
    // right-click on a folder or drive, pasting into it, as Explorer's own Paste does.
    public static readonly ShellVerbInfo RoboPaste = new(
        ShellVerb.RoboPaste, new Guid("9d1bae79-13c3-427f-a7e6-34150d5c49ab"),
        "RoboPaste", "Robo-Paste", [@"Directory\Background", "Directory", "Drive"]);

    public static readonly IReadOnlyList<ShellVerbInfo> All = [RoboCopy, RoboCut, RoboPaste];

    public static ShellVerbInfo Get(ShellVerb verb) => All.Single(v => v.Verb == verb);

    public static ShellVerbInfo? FindByClsid(Guid clsid) => All.FirstOrDefault(v => v.Clsid == clsid);

    public const string NothingSelectedReason = "Nothing was selected.";
    public const string NotOneFolderReason = "Robo-Paste needs exactly one destination folder.";

    /// <summary>
    /// The folder a Robo-Paste targets. Static verbs cannot hide themselves for a
    /// multi-folder selection the way Explorer's own Paste does, so that case is
    /// refused with a message instead of guessing which folder was meant.
    /// </summary>
    public static (string? Folder, string? Problem) PasteDestination(IReadOnlyList<string> selectedPaths) =>
        selectedPaths.Count switch
        {
            0 => (null, NothingSelectedReason),
            1 => (WinPath.TrimTrailingSeparators(selectedPaths[0]), null),
            _ => (null, NotOneFolderReason),
        };
}
