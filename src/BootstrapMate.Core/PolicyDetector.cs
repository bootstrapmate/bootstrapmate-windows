using Microsoft.Win32;

namespace BootstrapMate.Core;

/// <summary>
/// Detects which settings are managed by Intune CSP / Group Policy.
/// Windows equivalent of macOS MDMDetector — checks HKLM\SOFTWARE\Policies\BootstrapMate.
///
/// Intune writes here via OMA-URI:
///   ./Device/Vendor/MSFT/Policy/Config/BootstrapMate~Policy~BootstrapMate/{KeyName}
/// Group Policy writes here via ADMX-backed policies.
/// </summary>
public sealed class PolicyDetector
{
    public static PolicyDetector Instance { get; } = new();

    /// <summary>
    /// Known key aliases — maps canonical key to all variant names that may appear in policy.
    /// Mirrors macOS MDMDetector.keyAliases for consistent admin experience.
    /// </summary>
    private static readonly Dictionary<string, string[]> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ManifestUrl"]         = ["ManifestUrl", "url", "jsonUrl", "JsonUrl", "ConfigURL", "BootstrapUrl"],
        ["AuthorizationHeader"] = ["AuthorizationHeader", "headers", "Headers"],
        ["FollowRedirects"]     = ["FollowRedirects"],
        ["SilentMode"]          = ["SilentMode", "silent"],
        ["VerboseMode"]         = ["VerboseMode", "verbose"],
        ["Reboot"]              = ["Reboot"],
        ["DryRun"]              = ["DryRun"],
        ["EnableDialog"]        = ["EnableDialog"],
        ["NoDialog"]            = ["NoDialog"],
        ["DialogTitle"]         = ["DialogTitle"],
        ["DialogMessage"]       = ["DialogMessage"],
        ["DialogIcon"]          = ["DialogIcon"],
        ["BlurScreen"]          = ["BlurScreen"],
        ["CustomInstallPath"]   = ["CustomInstallPath", "InstallPath", "iapath"],
        ["NetworkTimeout"]      = ["NetworkTimeout"],
    };

    private PolicyDetector() { }

    /// <summary>Returns true if the canonical key is present in the Policies registry hive.</summary>
    public bool IsManagedByPolicy(string canonicalKey)
    {
        var aliases = GetAliases(canonicalKey);
        return FindRegistryValue(aliases) is not null;
    }

    /// <summary>Returns the policy-managed value for a canonical key, or null.</summary>
    public object? GetManagedValue(string canonicalKey)
    {
        var aliases = GetAliases(canonicalKey);
        return FindRegistryValue(aliases);
    }

    /// <summary>Returns a string policy value, or null.</summary>
    public string? GetManagedString(string canonicalKey)
        => GetManagedValue(canonicalKey)?.ToString();

    /// <summary>Returns a bool policy value, or null. Reads DWORD 0/1.</summary>
    public bool? GetManagedBool(string canonicalKey)
    {
        var value = GetManagedValue(canonicalKey);
        return value switch
        {
            int i => i != 0,
            string s when bool.TryParse(s, out var b) => b,
            string s when int.TryParse(s, out var i) => i != 0,
            _ => null
        };
    }

    /// <summary>Returns an int policy value, or null.</summary>
    public int? GetManagedInt(string canonicalKey)
    {
        var value = GetManagedValue(canonicalKey);
        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out var i) => i,
            _ => null
        };
    }

    /// <summary>Returns the set of canonical keys that are currently policy-managed.</summary>
    public HashSet<string> AllManagedKeys()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliases) in KeyAliases)
        {
            if (FindRegistryValue(aliases) is not null)
                result.Add(canonical);
        }
        return result;
    }

    private static string[] GetAliases(string canonicalKey)
        => KeyAliases.TryGetValue(canonicalKey, out var aliases) ? aliases : [canonicalKey];

    private static object? FindRegistryValue(string[] keyNames)
    {
        // Check both 64-bit and 32-bit registry views
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var policyKey = baseKey.OpenSubKey(BootstrapMateConstants.PolicyRegistryPath);
                if (policyKey is null) continue;

                foreach (var name in keyNames)
                {
                    var value = policyKey.GetValue(name);
                    if (value is not null) return value;
                }
            }
            catch
            {
                // Registry access may fail without elevation — silently skip
            }
        }
        return null;
    }
}
