using RoboRightClick.Core;

namespace RoboRightClick.Install;

/// <summary>
/// Applies Core's registry footprint (<see cref="Registration"/>) to HKEY_CURRENT_USER
/// with Microsoft.Win32.Registry. This class decides nothing about which keys exist;
/// it only executes the lists Core produces, so install and uninstall cannot drift.
/// </summary>
internal static class RegistryWriter
{
    /// <summary>Creates keys as needed and writes each value as REG_SZ.</summary>
    public static void Write(IEnumerable<RegistryValue> values) => throw new NotImplementedException();

    /// <summary>DeleteSubKeyTree / DeleteValue for each removal; already-absent is success.</summary>
    public static void Remove(IEnumerable<RegistryRemoval> removals) => throw new NotImplementedException();

    /// <summary>Writes or removes only the Run value (Settings → start with Windows).</summary>
    public static void SetStartWithWindows(bool enabled, string exePath) => throw new NotImplementedException();
}
