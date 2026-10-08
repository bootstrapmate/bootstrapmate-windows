namespace BootstrapMate.Core;

/// <summary>
/// Which policy values lock each setting the GUI Prefs tab shows. Most fields are locked by a
/// policy value of the same name; the "Show progress dialog" switch is locked by either
/// <c>EnableDialog</c> or <c>NoDialog</c>, because both turn the dialog off.
/// </summary>
public static class PrefsPolicy
{
    /// <summary>Prefs tab field → the policy value names (canonical keys) that manage it.</summary>
    public static IReadOnlyDictionary<string, string[]> FieldKeys { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ManifestUrl"] = ["ManifestUrl"],
            ["AuthorizationHeader"] = ["AuthorizationHeader"],
            ["SilentMode"] = ["SilentMode"],
            ["VerboseMode"] = ["VerboseMode"],
            ["DryRun"] = ["DryRun"],
            ["EnableDialog"] = ["EnableDialog", "NoDialog"],
            ["DialogTitle"] = ["DialogTitle"],
            ["DialogMessage"] = ["DialogMessage"],
            ["DialogIcon"] = ["DialogIcon"],
            ["BlurScreen"] = ["BlurScreen"],
            ["CustomInstallPath"] = ["CustomInstallPath"],
            ["NetworkTimeout"] = ["NetworkTimeout"],
        };

    /// <summary>True when policy manages the Prefs tab field <paramref name="field"/>.</summary>
    public static bool IsLocked(string field, IReadOnlySet<string> managedKeys)
    {
        var keys = FieldKeys.TryGetValue(field, out var k) ? k : [field];
        return keys.Any(managedKeys.Contains);
    }

    /// <summary>
    /// Whether the progress dialog is shown: <c>EnableDialog</c> false or <c>NoDialog</c> true
    /// turns it off, wherever each comes from.
    /// </summary>
    public static bool IsDialogShown(BootstrapMateConfig config) => config.EnableDialog && !config.NoDialog;
}
