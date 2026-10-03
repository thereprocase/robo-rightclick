using System.Globalization;

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

    /// <summary>
    /// The /MT count for one robocopy run. <paramref name="classify"/> asks the host about
    /// both drives and is called only when the count is automatic.
    /// </summary>
    /// <remarks>
    /// Classification can only tune a copy, never stop one: whatever <paramref name="classify"/>
    /// throws, the run goes ahead with <see cref="Fallback"/>, the count every run used before
    /// per-drive detection existed.
    /// </remarks>
    public static ThreadChoice Choose(Settings settings, Func<DrivePair> classify)
    {
        if (!settings.AutoThreads)
        {
            return new ThreadChoice(settings.Threads, Drives: null, ClassificationFailed: false);
        }
        DrivePair drives;
        try
        {
            drives = classify();
        }
        catch (Exception)
        {
            return new ThreadChoice(Fallback, Drives: null, ClassificationFailed: true);
        }
        return new ThreadChoice(Resolve(settings, drives.Source, drives.Destination, drives.SameDisk), drives, ClassificationFailed: false);
    }

    /// <summary>A medium as the job log names it.</summary>
    public static string Describe(DriveMedium medium) => medium switch
    {
        DriveMedium.SolidState => "solid-state",
        DriveMedium.Rotational => "rotational",
        DriveMedium.Network => "network",
        _ => "unknown",
    };
}

/// <summary>Both ends of one robocopy run, as the host classified them.</summary>
/// <param name="SameDisk">Both ends are on one physical disk or one volume.</param>
public readonly record struct DrivePair(DriveMedium Source, DriveMedium Destination, bool SameDisk);

/// <summary>The /MT count picked for one robocopy run, and what it was based on.</summary>
/// <param name="Drives">The classified drives; null when the count is fixed in settings or classification failed.</param>
/// <param name="ClassificationFailed">Classification threw, so the run uses <see cref="ThreadPolicy.Fallback"/>.</param>
public sealed record ThreadChoice(int Threads, DrivePair? Drives, bool ClassificationFailed)
{
    /// <summary>
    /// The settings one run's arguments are built from: the chosen count, fixed, so that
    /// <see cref="RobocopyArgs.Build"/> writes exactly this /MT value.
    /// </summary>
    public Settings ApplyTo(Settings settings) => settings with { AutoThreads = false, Threads = Threads };

    /// <summary>
    /// One line for the job's robocopy.log, written after the command line. It names media
    /// only, never a path or a drive letter, so it adds nothing a path-scrubbed log would need
    /// to remove.
    /// </summary>
    public string LogLine
    {
        get
        {
            var basis = this switch
            {
                { Drives: { } d } =>
                    $"auto: source {ThreadPolicy.Describe(d.Source)}, destination {ThreadPolicy.Describe(d.Destination)}"
                    + (d.SameDisk ? ", same disk" : ""),
                { ClassificationFailed: true } => "auto: drives could not be classified, default used",
                _ => "fixed in settings",
            };
            return $"# threads /MT:{Threads.ToString(CultureInfo.InvariantCulture)} ({basis})";
        }
    }
}
