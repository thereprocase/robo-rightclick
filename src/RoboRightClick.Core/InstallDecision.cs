namespace RoboRightClick.Core;

/// <summary>What the host found of an existing install, as data.</summary>
/// <param name="Present">Any trace of an install: the installed exe, or the Uninstall key.</param>
/// <param name="ExeVersion">The installed exe's product version; null when there is no exe or it has none.</param>
/// <param name="RegistryVersion">DisplayVersion from the HKCU Uninstall key; null when absent.</param>
public sealed record InstalledFacts(bool Present, string? ExeVersion, string? RegistryVersion)
{
    public static InstalledFacts None { get; } = new(false, null, null);
}

/// <summary>
/// What an install of this build should do. Pure: the host reads <see cref="InstalledFacts"/>,
/// asks <see cref="Decide"/>, then acts. A downgrade is never silent: it needs --force.
/// </summary>
public abstract record InstallDecision
{
    /// <summary>Nothing installed.</summary>
    public sealed record FreshInstall(string To) : InstallDecision;

    /// <summary>
    /// Replace an installed version. <paramref name="From"/> is null when the installed version
    /// is missing or garbled (treated as older). <paramref name="IsDowngrade"/> is true only
    /// under --force.
    /// </summary>
    public sealed record Update(string? From, string To, bool IsDowngrade = false) : InstallDecision;

    /// <summary>The same version is installed: put its files and keys back.</summary>
    public sealed record Repair(string Version) : InstallDecision;

    /// <summary>A newer version is installed and --force was not given: change nothing.</summary>
    public sealed record RefuseDowngrade(string Installed, string This) : InstallDecision;

    /// <summary>
    /// The installed version: the exe's when it can be read (the exe is what runs, the registry
    /// is only a label that a failed or manual change can leave stale), else the registry's.
    /// Null when neither can be read.
    /// </summary>
    public static AppVersion? ResolveInstalled(InstalledFacts facts) =>
        AppVersion.TryParse(facts.ExeVersion) ?? AppVersion.TryParse(facts.RegistryVersion);

    /// <summary>
    /// The version to write back to DisplayVersion when the registry has one that disagrees
    /// with the exe's; null when nothing needs repairing. Used when an install is refused, so
    /// the Installed apps list still tells the truth.
    /// </summary>
    public static string? StaleRegistryVersion(InstalledFacts facts)
    {
        var exe = AppVersion.TryParse(facts.ExeVersion);
        if (exe is null || facts.RegistryVersion is null)
        {
            return null;
        }
        var registry = AppVersion.TryParse(facts.RegistryVersion);
        return registry is not null && registry.CompareTo(exe) == 0 ? null : exe.Text;
    }

    /// <param name="thisVersion">This build's informational version.</param>
    public static InstallDecision Decide(InstalledFacts installed, string thisVersion, bool force)
    {
        var to = AppVersion.TryParse(thisVersion);
        var toText = to?.Text ?? thisVersion;
        if (!installed.Present)
        {
            return new FreshInstall(toText);
        }

        var from = ResolveInstalled(installed);
        if (from is null)
        {
            // Unreadable counts as older: an exe or key with no usable version is replaced.
            return new Update(null, toText);
        }
        if (to is null)
        {
            // This build cannot say what it is, so it cannot be proven older or newer.
            return new Update(from.Text, toText);
        }

        var order = to.CompareTo(from);
        if (order == 0)
        {
            return new Repair(from.Text);
        }
        if (order > 0)
        {
            return new Update(from.Text, to.Text);
        }
        return force
            ? new Update(from.Text, to.Text, IsDowngrade: true)
            : new RefuseDowngrade(from.Text, to.Text);
    }
}
