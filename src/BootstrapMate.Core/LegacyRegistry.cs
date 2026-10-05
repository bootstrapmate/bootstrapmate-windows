using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BootstrapMate.Core;

/// <summary>
/// Settings and policy are read from the 64-bit registry view only. Older builds also read
/// the 32-bit view (WOW6432Node), where stale copies outranked what the GUI and the MSI
/// write. Those copies are removed once, and each removal is reported.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LegacyRegistry
{
    /// <summary>Set under HKLM\SOFTWARE\BootstrapMate once the 32-bit copies are gone.</summary>
    public const string DoneMarker = "Wow64SettingsRemoved";

    /// <summary>
    /// 32-bit view keys that used to be read as settings. Only the settings key: the
    /// policy key (SOFTWARE\Policies) is shared between the two views, so its "32-bit copy"
    /// is the live policy itself and must never be removed.
    /// </summary>
    public static readonly string[] StaleKeys = [BootstrapMateConstants.MachineSettingsRegistryPath];

    /// <summary>Removes the stale 32-bit keys once. Must run elevated. Returns what it removed.</summary>
    public static List<string> RemoveStaleWow64Settings()
    {
        var notes = new List<string>();
        try
        {
            using var base64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var tool = base64.CreateSubKey(@"SOFTWARE\BootstrapMate", writable: true);
            if (tool.GetValue(DoneMarker) is int done && done != 0) return notes;

            using var base32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            foreach (var path in StaleKeys)
            {
                using (var key = base32.OpenSubKey(path))
                {
                    if (key is null) continue;
                    // Names only: a value here may be a credential.
                    var names = key.GetValueNames().Where(n => n.Length > 0).ToArray();
                    var below = path[@"SOFTWARE\".Length..];
                    notes.Add($@"Removed stale HKLM\SOFTWARE\WOW6432Node\{below}" +
                              (names.Length > 0 ? $" ({string.Join(", ", names)})" : ""));
                }
                base32.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }

            tool.SetValue(DoneMarker, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            notes.Add($"Could not remove stale 32-bit settings: {ex.Message}");
        }
        return notes;
    }
}
