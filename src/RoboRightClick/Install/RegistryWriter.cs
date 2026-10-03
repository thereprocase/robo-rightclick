using System.Runtime.InteropServices;
using Microsoft.Win32;
using RoboRightClick.Com;
using RoboRightClick.Core;

namespace RoboRightClick.Install;

/// <summary>
/// Applies Core's registry footprint (<see cref="Registration"/>) to HKEY_CURRENT_USER
/// with Microsoft.Win32.Registry. This class decides nothing about which keys exist;
/// it only executes the lists Core produces, so install and uninstall cannot drift.
/// </summary>
internal static class RegistryWriter
{
    /// <summary>
    /// Creates keys as needed and writes each value by its <see cref="RegistryDataKind"/>:
    /// String as REG_SZ, DWORD as REG_DWORD (<see cref="Registration.DWordValue"/>),
    /// SecurityDescriptor as REG_BINARY from the SDDL (self-relative).
    /// </summary>
    /// <remarks>
    /// The binary form comes from ConvertStringSecurityDescriptorToSecurityDescriptor, the
    /// same call the process uses for CoInitializeSecurity, not from RawSecurityDescriptor:
    /// the descriptor's mandatory-label ACE is what refuses low-integrity callers, and a
    /// managed parse-and-reserialize could drop or reorder it without notice.
    /// </remarks>
    public static void Write(IEnumerable<RegistryValue> values)
    {
        foreach (var value in values)
        {
            RequireRelativeKey(value.Key);
            using var key = Registry.CurrentUser.CreateSubKey(value.Key, writable: true)
                ?? throw new IOException($"Could not create HKEY_CURRENT_USER\\{value.Key}.");
            switch (value.Kind)
            {
                case RegistryDataKind.String:
                    key.SetValue(value.Name, value.Data, RegistryValueKind.String);
                    break;
                case RegistryDataKind.DWord:
                    key.SetValue(value.Name, Registration.DWordValue(value), RegistryValueKind.DWord);
                    break;
                case RegistryDataKind.SecurityDescriptor:
                    key.SetValue(value.Name, ToBinary(value.Data), RegistryValueKind.Binary);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown registry data kind {value.Kind}.");
            }
        }
    }

    /// <summary>DeleteSubKeyTree / DeleteValue for each removal; already-absent is success.</summary>
    public static void Remove(IEnumerable<RegistryRemoval> removals)
    {
        foreach (var removal in removals)
        {
            RequireRelativeKey(removal.Key);
            switch (removal)
            {
                case RemoveKeyTree tree:
                    Registry.CurrentUser.DeleteSubKeyTree(tree.Key, throwOnMissingSubKey: false);
                    break;
                case RemoveValue single:
                    using (var key = Registry.CurrentUser.OpenSubKey(single.Key, writable: true))
                    {
                        key?.DeleteValue(single.Name, throwOnMissingValue: false);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unknown registry removal {removal.GetType().Name}.");
            }
        }
    }

    /// <summary>Writes or removes only the Run value (Settings → start with Windows).</summary>
    public static void SetStartWithWindows(bool enabled, string exePath)
    {
        if (enabled)
        {
            Write([Registration.RunValue(exePath)]);
        }
        else
        {
            Remove([new RemoveValue(Registration.RunKey, Registration.RunValueName)]);
        }
    }

    /// <summary>
    /// What the install's own registry entries say: whether the Uninstall key exists and its
    /// DisplayVersion (null when absent or not a string).
    /// </summary>
    public static (bool KeyExists, string? DisplayVersion) ReadInstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Registration.UninstallKey);
        return key is null ? (false, null) : (true, key.GetValue("DisplayVersion") as string);
    }

    /// <summary>The LocalServer32 command of the RoboCopy CLSID, or null when not registered (install-state check).</summary>
    public static string? RegisteredServerCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Registration.ClsidKey(ShellVerbs.RoboCopy.Clsid) + @"\LocalServer32");
        return key?.GetValue(string.Empty) as string;
    }

    /// <summary>
    /// Every key comes from Core, but this class is the one place that can delete from the
    /// registry, so it refuses a key that is empty or a bare top-level name (a tree removal
    /// of "Software" would be catastrophic).
    /// </summary>
    private static void RequireRelativeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('\\') || key.StartsWith('\\') || key.EndsWith('\\'))
        {
            throw new ArgumentException("Refusing an unexpected registry key.", nameof(key));
        }
    }

    private static byte[] ToBinary(string sddl)
    {
        if (!ComNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, ComNative.SDDL_REVISION_1, out var descriptor, out _))
        {
            throw new IOException($"A security descriptor could not be built (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
        try
        {
            var bytes = new byte[ComNative.GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            ComNative.LocalFree(descriptor);
        }
    }
}
