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
/// <param name="MultiSelectModel">
/// "Player": the whole selection arrives in one Execute call. "Single": the shell shows the
/// item only for a single selection, which is how Explorer's own Paste behaves on folders.
/// </param>
/// <param name="Label">The plain name, without an access key: the COM class name, messages, toasts.</param>
/// <param name="MenuLabel">
/// The classic-menu text (MUIVerb): <paramref name="Label"/> with one "&amp;" before its
/// access key, so a keyboard user can run the item with one letter once the menu is open.
/// </param>
public sealed record ShellVerbInfo(
    ShellVerb Verb,
    Guid Clsid,
    string KeyName,
    string Label,
    string MenuLabel,
    IReadOnlyList<string> Associations,
    string MultiSelectModel)
{
    /// <summary>The letter after the "&amp;" in <see cref="MenuLabel"/>, upper case.</summary>
    public char AccessKey => char.ToUpperInvariant(MenuLabel[MenuLabel.IndexOf('&', StringComparison.Ordinal) + 1]);
}

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

    // Access keys: the obvious letters are Explorer's own (C Copy, P Paste, T Cut, O Open,
    // R Properties, A Copy as path, E Edit or Refresh), so each label's free letter is used.
    // U is Undo, but only on the folder-background menu, where Robo-Cut never appears.
    // A duplicate letter would still work: Windows then cycles through the items with it.
    public static readonly ShellVerbInfo RoboCopy = new(
        ShellVerb.RoboCopy, new Guid("bd15dc6a-fbc1-4949-b61d-3b8fc390062f"),
        "RoboCopy", "Robo-Copy", "Robo-Cop&y", ["AllFilesystemObjects"], "Player");

    public static readonly ShellVerbInfo RoboCut = new(
        ShellVerb.RoboCut, new Guid("1a061376-a3f7-41bf-a516-e635ed91acdf"),
        "RoboCut", "Robo-Cut", "Robo-C&ut", ["AllFilesystemObjects"], "Player");

    // Background = right-click on empty space inside a folder; Directory and Drive =
    // right-click on a folder or drive, pasting into it, as Explorer's own Paste does.
    public static readonly ShellVerbInfo RoboPaste = new(
        ShellVerb.RoboPaste, new Guid("9d1bae79-13c3-427f-a7e6-34150d5c49ab"),
        "RoboPaste", "Robo-Paste", "Ro&bo-Paste", [BackgroundAssociation, "Directory", "Drive"], "Single");

    public static readonly IReadOnlyList<ShellVerbInfo> All = [RoboCopy, RoboCut, RoboPaste];

    /// <summary>
    /// The menu icon file for a verb, written next to the installed exe. Each .ico holds one
    /// pixel-fitted frame per common display scale (16-48 px), so Explorer picks a crisp one
    /// at 100-300% scaling instead of resampling (tools/icons/render_icons.py).
    /// </summary>
    public static string IconFileName(ShellVerbInfo verb) => verb.Verb switch
    {
        ShellVerb.RoboCopy => "robo-copy.ico",
        ShellVerb.RoboCut => "robo-cut.ico",
        ShellVerb.RoboPaste => "robo-paste.ico",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    /// <summary>Right-click on the empty space of a folder: a click with no selected item.</summary>
    public const string BackgroundAssociation = @"Directory\Background";

    /// <summary>
    /// The MultiSelectModel value written for <paramref name="verb"/> under
    /// <paramref name="association"/>, or null for none. A background click selects nothing,
    /// and Explorer hides a background verb marked "Single" (it counts zero items), so the
    /// background key gets no value; the item keys keep the verb's model.
    /// </summary>
    public static string? MultiSelectModelFor(ShellVerbInfo verb, string association) =>
        association == BackgroundAssociation ? null : verb.MultiSelectModel;

    /// <summary>
    /// The items one invocation acts on. Explorer invokes a folder-background verb with no
    /// selection and the folder in IExecuteCommand::SetDirectory; for a click on items it
    /// passes the items as the selection and their parent folder as the directory. The
    /// directory therefore stands in only for Robo-Paste, and only when nothing at all was
    /// selected: preferring it whenever present would paste a right-clicked folder's
    /// clipboard into that folder's parent, and a selection of virtual items (counted in
    /// <paramref name="skippedItems"/>) must be refused, not redirected to the parent.
    /// </summary>
    public static IReadOnlyList<string> InvocationItems(
        ShellVerb verb, IReadOnlyList<string> selected, int skippedItems, string? directory) =>
        verb == ShellVerb.RoboPaste && selected.Count == 0 && skippedItems == 0 && !string.IsNullOrEmpty(directory)
            ? [directory]
            : selected;

    public static ShellVerbInfo Get(ShellVerb verb) => All.Single(v => v.Verb == verb);

    public static ShellVerbInfo? FindByClsid(Guid clsid) => All.FirstOrDefault(v => v.Clsid == clsid);

    public const string NothingSelectedReason = "Nothing was selected.";
    public const string NotOneFolderReason = "Robo-Paste needs exactly one destination folder.";

    /// <summary>
    /// The folder a Robo-Paste targets. MultiSelectModel=Single already hides the item for
    /// a multi-folder selection; this still refuses that case (the CLI and any other COM
    /// client can send one) instead of guessing which folder was meant.
    /// </summary>
    public static (string? Folder, string? Problem) PasteDestination(IReadOnlyList<string> selectedPaths) =>
        selectedPaths.Count switch
        {
            0 => (null, NothingSelectedReason),
            1 => (WinPath.TrimTrailingSeparators(selectedPaths[0]), null),
            _ => (null, NotOneFolderReason),
        };
}
