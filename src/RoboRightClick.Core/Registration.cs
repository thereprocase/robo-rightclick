namespace RoboRightClick.Core;

/// <summary>A REG_SZ value under HKEY_CURRENT_USER. An empty <paramref name="Name"/> is the key's default value.</summary>
public sealed record RegistryValue(string Key, string Name, string Data);

/// <summary>Something uninstall deletes under HKEY_CURRENT_USER.</summary>
public abstract record RegistryRemoval(string Key);

/// <summary>Deletes a key and everything below it. Only ever a key install itself created.</summary>
public sealed record RemoveKeyTree(string Key) : RegistryRemoval(Key);

/// <summary>Deletes one value, leaving its (shared) key in place.</summary>
public sealed record RemoveValue(string Key, string Name) : RegistryRemoval(Key);

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

    public static IReadOnlyList<RegistryValue> InstallValues(string exePath, bool startWithWindows)
    {
        var values = new List<RegistryValue>
        {
            new(AppIdKey, string.Empty, AppInfo.Name),
        };

        foreach (var verb in ShellVerbs.All)
        {
            var clsidKey = ClsidKey(verb.Clsid);
            values.Add(new(clsidKey, string.Empty, $"{AppInfo.Name} {verb.Label}"));
            values.Add(new(clsidKey, "AppID", FormatGuid(ShellVerbs.AppId)));
            values.Add(new(clsidKey + @"\LocalServer32", string.Empty, LocalServerCommand(exePath)));

            foreach (var association in verb.Associations)
            {
                var verbKey = VerbKey(association, verb);
                values.Add(new(verbKey, "MUIVerb", verb.Label));
                // Player: Explorer hands the whole selection to one Execute call
                // instead of one call per item (and no 15-item cap).
                values.Add(new(verbKey, "MultiSelectModel", "Player"));
                // DelegateExecute lives on the verb's "command" subkey, as in
                // Microsoft's ExecuteCommandVerb sample (RegisterExtension.cpp).
                values.Add(new(verbKey + @"\command", "DelegateExecute", FormatGuid(verb.Clsid)));
            }
        }

        if (startWithWindows)
        {
            values.Add(RunValue(exePath));
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
        removals.Add(new RemoveValue(RunKey, RunValueName));
        return removals;
    }
}
