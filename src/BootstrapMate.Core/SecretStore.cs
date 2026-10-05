using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace BootstrapMate.Core;

/// <summary>
/// Where the headers that carry credentials live: HKLM\SOFTWARE\BootstrapMate\Secrets, with
/// its own ACL (SYSTEM and Administrators full control, nobody else). The policy key and the
/// settings key are readable by every user, so a header that arrives there is moved here
/// and the readable copy blanked.
/// </summary>
/// <remarks>
/// Only an elevated process can read or write the store. A standard user's process reads
/// nothing and treats the header as unset. Values are never logged.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    public const string RegistryPath = @"SOFTWARE\BootstrapMate\Secrets";

    /// <summary>The settings that hold credentials.</summary>
    public static readonly string[] SecretNames = ["AuthorizationHeader", "ReportingHeader"];

    public static bool IsSecret(string name) => SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The stored value, or null when unset or this process may not read it.</summary>
    public static string? Read(string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryPath);
            return key?.GetValue(name) is string s && s.Length > 0 ? s : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/>, or removes the entry when it is empty. Must run elevated.</summary>
    public static void Write(string name, string? value)
    {
        using var key = OpenProtected();
        if (string.IsNullOrEmpty(value))
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>
    /// Moves every credential header found in a user-readable place (the policy key under
    /// any of its aliases, the settings key) into the store and blanks the readable copy.
    /// A policy value is set to empty rather than deleted, so the setting still shows as
    /// managed. Returns one line per move, naming the setting and place, never the value.
    /// Must run elevated, before the configuration is loaded.
    /// </summary>
    public static List<string> MigrateReadableCopies()
    {
        var notes = new List<string>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (OpenProtected()) { }

            foreach (var name in SecretNames)
            {
                // Settings first, policy last: policy wins, as it does everywhere else.
                using (var settings = baseKey.OpenSubKey(BootstrapMateConstants.MachineSettingsRegistryPath, writable: true))
                {
                    if (settings?.GetValue(name) is string s && s.Length > 0)
                    {
                        Write(name, s);
                        settings.DeleteValue(name, throwOnMissingValue: false);
                        notes.Add($"Moved {name} from HKLM\\{BootstrapMateConstants.MachineSettingsRegistryPath} to the protected store");
                    }
                }

                using var policy = baseKey.OpenSubKey(BootstrapMateConstants.PolicyRegistryPath, writable: true);
                if (policy is null) continue;
                foreach (var alias in ManagementDetector.AliasesFor(name))
                {
                    if (policy.GetValue(alias) is string p && p.Length > 0)
                    {
                        Write(name, p);
                        policy.SetValue(alias, string.Empty, RegistryValueKind.String);
                        notes.Add($"Moved {name} from HKLM\\{BootstrapMateConstants.PolicyRegistryPath}\\{alias} to the protected store and blanked the policy copy");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Could not move credential headers to the protected store: {ex.Message}");
        }
        return notes;
    }

    /// <summary>Opens the store for writing, creating it, and resets its ACL every time.</summary>
    private static RegistryKey OpenProtected()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var key = baseKey.CreateSubKey(RegistryPath, writable: true);
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null),
                RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        key.SetAccessControl(security);
        return key;
    }
}
