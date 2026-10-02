namespace RoboRightClick.Core;

/// <summary>What kind of storage a path lives on, as far as parallel copying is concerned.</summary>
public enum DriveMedium
{
    /// <summary>The host could not tell (spanned volume, virtual disk, query refused).</summary>
    Unknown,

    /// <summary>No seek penalty: NVMe and SATA SSDs, most USB flash.</summary>
    SolidState,

    /// <summary>Incurs a seek penalty: spinning disks, including most external USB hard drives.</summary>
    Rotational,

    /// <summary>A network share (UNC path or mapped drive).</summary>
    Network,
}

/// <summary>
/// Picks robocopy's /MT thread count for one run from the drives at both ends.
/// </summary>
/// <remarks>
/// Many threads hide per-file latency on SSDs and network shares, where requests overlap
/// well. On a spinning disk every extra concurrent file adds head seeks, so a high count can
/// be slower than a low one. The slower end of a copy sets the pace, so the run uses the
/// lower of the two counts, and a copy within one spinning disk (reads and writes competing
/// for the same head) goes lower still.
/// </remarks>
public static class ThreadPolicy
{
    public const int SolidState = 32;
    public const int Network = 32;
    public const int Rotational = 8;
    public const int SameRotationalDisk = 4;

    /// <summary>Used when a drive cannot be classified, and as the fixed default.</summary>
    public const int Fallback = 32;

    public static int For(DriveMedium medium) => medium switch
    {
        DriveMedium.SolidState => SolidState,
        DriveMedium.Network => Network,
        DriveMedium.Rotational => Rotational,
        _ => Fallback,
    };

    /// <summary>The /MT value for a run from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <param name="sameDisk">True when both ends are on the same physical disk (or the same volume).</param>
    public static int Resolve(Settings settings, DriveMedium source, DriveMedium destination, bool sameDisk)
    {
        if (!settings.AutoThreads)
        {
            return settings.Threads;
        }
        if (sameDisk && source == DriveMedium.Rotational && destination == DriveMedium.Rotational)
        {
            return SameRotationalDisk;
        }
        return Math.Min(For(source), For(destination));
    }
}
