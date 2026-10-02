using System.Globalization;

namespace RoboRightClick.Core;

public enum RegistryDataKind
{
    /// <summary>REG_SZ.</summary>
    String,

    /// <summary>REG_DWORD; Data is the decimal value.</summary>
    DWord,

    /// <summary>REG_BINARY self-relative security descriptor; Data is SDDL the host converts.</summary>
    SecurityDescriptor,
}

/// <summary>A value under HKEY_CURRENT_USER. An empty <paramref name="Name"/> is the key's default value.</summary>
public sealed record RegistryValue(string Key, string Name, string Data, RegistryDataKind Kind = RegistryDataKind.String);

/// <summary>Something uninstall deletes under HKEY_CURRENT_USER.</summary>
public abstract record RegistryRemoval(string Key);

/// <summary>Deletes a key and everything below it. Only ever a key install itself created.</summary>
public sealed record RemoveKeyTree(string Key) : RegistryRemoval(Key);

/// <summary>Deletes one value, leaving its (shared) key in place.</summary>
public sealed record RemoveValue(string Key, string Name) : RegistryRemoval(Key);

/// <summary>What install needs to know to produce the registry footprint.</summary>
/// <param name="ExePath">The installed executable (AppPaths.InstalledExe).</param>
/// <param name="UserSid">The installing user's SID, for the COM launch and access descriptors.</param>
/// <param name="Version">Shown in Settings → Apps → Installed apps.</param>
public sealed record InstallTarget(string ExePath, bool StartWithWindows, string UserSid, string Version);

/// <summary>
/// The complete per-user registry footprint, as data. Install writes exactly
/// <see cref="InstallValues"/>; uninstall deletes exactly <see cref="UninstallRemovals"/>.
/// Both derive from the same verb table, so they cannot drift apart, and nothing here
/// touches an Explorer setting (product invariant 4).
/// </summary>
public static class Registration
{
    public const string ClassesRoot = @"Software\Classes";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = AppInfo.Name;

    /// <summary>The per-user "Installed apps" entry, so the app can be removed the usual way.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Name;

    /// <summary>Registry form of a GUID: "{XXXXXXXX-...}".</summary>
    public static string FormatGuid(Guid guid) => guid.ToString("B").ToUpperInvariant();

    public static string ClsidKey(Guid clsid) => $@"{ClassesRoot}\CLSID\{FormatGuid(clsid)}";

    public static string AppIdKey => $@"{ClassesRoot}\AppID\{FormatGuid(ShellVerbs.AppId)}";

    public static string VerbKey(string association, ShellVerbInfo verb) =>
        $@"{ClassesRoot}\{association}\shell\{verb.KeyName}";

    /// <summary>
    /// COM appends " -Embedding" to this command line when it has to start the server.
    /// The path is quoted because the per-user profile path may contain spaces.
    /// </summary>
    public static string LocalServerCommand(string exePath) => "\"" + exePath + "\"";

    /// <summary>
    /// The verb's menu icon: a plain path to an .ico beside the exe (no ",index"), so the
    /// shell loads the frame that matches the menu's DPI from the file itself.
    /// </summary>
    public static string IconPath(string exePath, ShellVerbInfo verb) =>
        WinPath.Combine(WinPath.GetParent(exePath), ShellVerbs.IconFileName(verb));

    public static string UninstallCommand(string exePath) => LocalServerCommand(exePath) + " --uninstall";

    public static IReadOnlyList<RegistryValue> InstallValues(InstallTarget target)
    {
        var exe = target.ExePath;
        var values = new List<RegistryValue>
        {
            new(AppIdKey, string.Empty, AppInfo.Name),
            new(AppIdKey, "AccessPermission", ComSecurity.AccessPermissionSddl(target.UserSid), RegistryDataKind.SecurityDescriptor),
            new(AppIdKey, "LaunchPermission", ComSecurity.LaunchPermissionSddl(target.UserSid), RegistryDataKind.SecurityDescriptor),
        };

        foreach (var verb in ShellVerbs.All)
        {
            var clsidKey = ClsidKey(verb.Clsid);
            // The class name has no access key: it shows in COM tools and error messages.
            values.Add(new(clsidKey, string.Empty, $"{AppInfo.Name} {verb.Label}"));
            values.Add(new(clsidKey, "AppID", FormatGuid(ShellVerbs.AppId)));
            values.Add(new(clsidKey + @"\LocalServer32", string.Empty, LocalServerCommand(exe)));

            foreach (var association in verb.Associations)
            {
                var verbKey = VerbKey(association, verb);
                values.Add(new(verbKey, "MUIVerb", verb.MenuLabel));
                values.Add(new(verbKey, "Icon", IconPath(exe, verb)));
                if (ShellVerbs.MultiSelectModelFor(verb, association) is { } model)
                {
                    values.Add(new(verbKey, "MultiSelectModel", model));
                }
                // DelegateExecute lives on the verb's "command" subkey, as in
                // Microsoft's ExecuteCommandVerb sample (RegisterExtension.cpp).
                values.Add(new(verbKey + @"\command", "DelegateExecute", FormatGuid(verb.Clsid)));
            }
        }

        values.Add(new(UninstallKey, "DisplayName", AppInfo.Name));
        values.Add(new(UninstallKey, "DisplayVersion", target.Version));
        values.Add(new(UninstallKey, "DisplayIcon", exe));
        values.Add(new(UninstallKey, "InstallLocation", WinPath.GetParent(exe)));
        values.Add(new(UninstallKey, "UninstallString", UninstallCommand(exe)));
        values.Add(new(UninstallKey, "NoModify", "1", RegistryDataKind.DWord));
        values.Add(new(UninstallKey, "NoRepair", "1", RegistryDataKind.DWord));

        if (target.StartWithWindows)
        {
            values.Add(RunValue(exe));
        }
        return values;
    }

    public static RegistryValue RunValue(string exePath) => new(RunKey, RunValueName, LocalServerCommand(exePath));

    /// <summary>
    /// Everything install could have written. The Run value is always listed because
    /// the user may have turned startWithWindows on after install.
    /// </summary>
    public static IReadOnlyList<RegistryRemoval> UninstallRemovals()
    {
        var removals = new List<RegistryRemoval> { new RemoveKeyTree(AppIdKey) };
        foreach (var verb in ShellVerbs.All)
        {
            removals.Add(new RemoveKeyTree(ClsidKey(verb.Clsid)));
            foreach (var association in verb.Associations)
            {
                removals.Add(new RemoveKeyTree(VerbKey(association, verb)));
            }
        }
        removals.Add(new RemoveKeyTree(UninstallKey));
        removals.Add(new RemoveValue(RunKey, RunValueName));
        return removals;
    }

    /// <summary>
    /// The autostart choice a (re)install applies: an explicit --autostart/--no-autostart
    /// wins and is also written to config.json; otherwise an existing config's setting is
    /// kept, so a plain reinstall never flips what the user chose in Settings.
    /// </summary>
    public static bool ResolveStartWithWindows(bool? explicitChoice, Settings? existingConfig) =>
        explicitChoice ?? existingConfig?.StartWithWindows ?? Settings.Default.StartWithWindows;

    /// <summary>REG_DWORD data as the host writes it.</summary>
    public static int DWordValue(RegistryValue value) =>
        value.Kind == RegistryDataKind.DWord
            ? int.Parse(value.Data, NumberStyles.None, CultureInfo.InvariantCulture)
            : throw new ArgumentException("Not a DWORD value.", nameof(value));
}
