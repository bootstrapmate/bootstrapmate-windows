using Microsoft.Win32;

namespace BootstrapMate.Core;

/// <summary>
/// Configuration loader with fallback chain (highest → lowest priority):
///   1. CLI arguments
///   2. Intune CSP / Group Policy (HKLM\SOFTWARE\Policies\BootstrapMate)
///   3. User-configured settings (HKLM\SOFTWARE\BootstrapMate\Settings)
///   4. Default values
///
/// Mirrors macOS ConfigManager for cross-platform parity.
/// </summary>
public sealed class ConfigManager
{
    public static ConfigManager Instance { get; } = new();

    /// <summary>Current active configuration.</summary>
    public BootstrapMateConfig Config { get; private set; } = new();

    /// <summary>Source that provided the manifest URL.</summary>
    public ConfigSource ManifestUrlSource { get; private set; } = ConfigSource.Default;

    private ConfigManager()
    {
        LoadPolicyAndUserSettings();
    }

    public enum ConfigSource
    {
        Default,
        UserSettings,
        Policy,
        CliArgument
    }

    /// <summary>
    /// Apply CLI arguments (highest priority — overrides everything).
    /// </summary>
    public void ApplyCliArguments(
        string? manifestUrl = null,
        string? authorizationHeader = null,
        bool? followRedirects = null,
        bool? reboot = null,
        bool? silentMode = null,
        bool? verboseMode = null,
        bool? dryRun = null,
        bool? noDialog = null,
        string? dialogTitle = null,
        string? dialogMessage = null,
        int? networkTimeout = null)
    {
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            Config.ManifestUrl = manifestUrl;
            ManifestUrlSource = ConfigSource.CliArgument;
        }
        if (!string.IsNullOrWhiteSpace(authorizationHeader))
            Config.AuthorizationHeader = authorizationHeader;
        if (followRedirects.HasValue)
            Config.FollowRedirects = followRedirects.Value;
        if (reboot.HasValue)
            Config.Reboot = reboot.Value;
        if (silentMode.HasValue)
            Config.SilentMode = silentMode.Value;
        if (verboseMode.HasValue)
            Config.VerboseMode = verboseMode.Value;
        if (dryRun.HasValue)
            Config.DryRun = dryRun.Value;
        if (noDialog.HasValue)
            Config.NoDialog = noDialog.Value;
        if (!string.IsNullOrWhiteSpace(dialogTitle))
            Config.DialogTitle = dialogTitle;
        if (!string.IsNullOrWhiteSpace(dialogMessage))
            Config.DialogMessage = dialogMessage;
        if (networkTimeout.HasValue)
            Config.NetworkTimeout = networkTimeout.Value;
    }

    /// <summary>Get the effective manifest URL from whatever source is active.</summary>
    public string? GetEffectiveManifestUrl() => Config.ManifestUrl;

    /// <summary>Get the installation path.</summary>
    public string GetInstallPath()
        => Config.CustomInstallPath ?? BootstrapMateConstants.DefaultInstallPath;

    /// <summary>Check if a minimum valid configuration exists (manifest URL set).</summary>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Config.ManifestUrl);

    /// <summary>
    /// Reload settings from policy + user registry. Call when waiting for
    /// Intune policy to land post-enrollment.
    /// Returns true if a valid manifest URL was found.
    /// </summary>
    public bool ReloadSettings()
    {
        Config.ManifestUrl = null;
        ManifestUrlSource = ConfigSource.Default;
        LoadPolicyAndUserSettings();
        return IsValid();
    }

    /// <summary>
    /// Save user-configured settings to HKLM\SOFTWARE\BootstrapMate\Settings.
    /// Skips any key that is already policy-managed.
    /// Requires elevation.
    /// </summary>
    public static void SaveUserSettings(BootstrapMateConfig settings)
    {
        var policy = PolicyDetector.Instance;

        void WriteString(string key, string? value)
        {
            if (policy.IsManagedByPolicy(key)) return;
            WriteRegistryValue(key, value ?? string.Empty, RegistryValueKind.String);
        }

        void WriteBool(string key, bool value)
        {
            if (policy.IsManagedByPolicy(key)) return;
            WriteRegistryValue(key, value ? 1 : 0, RegistryValueKind.DWord);
        }

        void WriteInt(string key, int value)
        {
            if (policy.IsManagedByPolicy(key)) return;
            WriteRegistryValue(key, value, RegistryValueKind.DWord);
        }

        WriteString("ManifestUrl", settings.ManifestUrl);
        if (!string.IsNullOrEmpty(settings.AuthorizationHeader))
            WriteString("AuthorizationHeader", settings.AuthorizationHeader);
        WriteBool("FollowRedirects", settings.FollowRedirects);
        WriteBool("Reboot", settings.Reboot);
        WriteBool("SilentMode", settings.SilentMode);
        WriteBool("VerboseMode", settings.VerboseMode);
        WriteBool("DryRun", settings.DryRun);
        WriteBool("EnableDialog", settings.EnableDialog);
        WriteBool("NoDialog", settings.NoDialog);
        WriteString("DialogTitle", settings.DialogTitle);
        WriteString("DialogMessage", settings.DialogMessage);
        WriteString("DialogIcon", settings.DialogIcon);
        WriteBool("BlurScreen", settings.BlurScreen);
        WriteString("CustomInstallPath", settings.CustomInstallPath);
        WriteInt("NetworkTimeout", settings.NetworkTimeout);
    }

    // ── Private ──────────────────────────────────────────────────────

    private void LoadPolicyAndUserSettings()
    {
        var policy = PolicyDetector.Instance;

        // Load from user settings first (lower priority)
        LoadFromUserRegistry();

        // Policy overrides user settings
        LoadFromPolicy(policy);
    }

    private void LoadFromPolicy(PolicyDetector policy)
    {
        if (policy.GetManagedString("ManifestUrl") is { Length: > 0 } url)
        {
            Config.ManifestUrl = url;
            ManifestUrlSource = ConfigSource.Policy;
        }

        if (policy.GetManagedString("AuthorizationHeader") is { Length: > 0 } auth)
            Config.AuthorizationHeader = auth;

        if (policy.GetManagedBool("FollowRedirects") is { } followRedirects)
            Config.FollowRedirects = followRedirects;

        if (policy.GetManagedBool("Reboot") is { } reboot)
            Config.Reboot = reboot;

        if (policy.GetManagedBool("SilentMode") is { } silent)
            Config.SilentMode = silent;

        if (policy.GetManagedBool("VerboseMode") is { } verbose)
            Config.VerboseMode = verbose;

        if (policy.GetManagedBool("DryRun") is { } dryRun)
            Config.DryRun = dryRun;

        if (policy.GetManagedBool("EnableDialog") is { } enableDialog)
            Config.EnableDialog = enableDialog;

        if (policy.GetManagedBool("NoDialog") is { } noDialog)
            Config.NoDialog = noDialog;

        if (policy.GetManagedString("DialogTitle") is { Length: > 0 } title)
            Config.DialogTitle = title;

        if (policy.GetManagedString("DialogMessage") is { Length: > 0 } msg)
            Config.DialogMessage = msg;

        if (policy.GetManagedString("DialogIcon") is { Length: > 0 } icon)
            Config.DialogIcon = icon;

        if (policy.GetManagedBool("BlurScreen") is { } blur)
            Config.BlurScreen = blur;

        if (policy.GetManagedString("CustomInstallPath") is { Length: > 0 } path)
            Config.CustomInstallPath = path;

        if (policy.GetManagedInt("NetworkTimeout") is { } timeout)
            Config.NetworkTimeout = timeout;
    }

    private void LoadFromUserRegistry()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var settingsKey = baseKey.OpenSubKey(BootstrapMateConstants.SettingsRegistryPath);
            if (settingsKey is null) return;

            Config.ManifestUrl = ReadString(settingsKey, "ManifestUrl") ?? Config.ManifestUrl;
            if (!string.IsNullOrWhiteSpace(Config.ManifestUrl) && ManifestUrlSource == ConfigSource.Default)
                ManifestUrlSource = ConfigSource.UserSettings;

            Config.AuthorizationHeader = ReadString(settingsKey, "AuthorizationHeader") ?? Config.AuthorizationHeader;
            Config.FollowRedirects = ReadBool(settingsKey, "FollowRedirects") ?? Config.FollowRedirects;
            Config.Reboot = ReadBool(settingsKey, "Reboot") ?? Config.Reboot;
            Config.SilentMode = ReadBool(settingsKey, "SilentMode") ?? Config.SilentMode;
            Config.VerboseMode = ReadBool(settingsKey, "VerboseMode") ?? Config.VerboseMode;
            Config.DryRun = ReadBool(settingsKey, "DryRun") ?? Config.DryRun;
            Config.EnableDialog = ReadBool(settingsKey, "EnableDialog") ?? Config.EnableDialog;
            Config.NoDialog = ReadBool(settingsKey, "NoDialog") ?? Config.NoDialog;
            Config.DialogTitle = ReadString(settingsKey, "DialogTitle") ?? Config.DialogTitle;
            Config.DialogMessage = ReadString(settingsKey, "DialogMessage") ?? Config.DialogMessage;
            Config.DialogIcon = ReadString(settingsKey, "DialogIcon") ?? Config.DialogIcon;
            Config.BlurScreen = ReadBool(settingsKey, "BlurScreen") ?? Config.BlurScreen;
            Config.CustomInstallPath = ReadString(settingsKey, "CustomInstallPath") ?? Config.CustomInstallPath;
            Config.NetworkTimeout = ReadInt(settingsKey, "NetworkTimeout") ?? Config.NetworkTimeout;
        }
        catch
        {
            // Registry unavailable — use defaults
        }
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value is string s && s.Length > 0 ? s : null;
    }

    private static bool? ReadBool(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int i => i != 0,
            string s when bool.TryParse(s, out var b) => b,
            _ => null
        };
    }

    private static int? ReadInt(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out var i) => i,
            _ => null
        };
    }

    private static void WriteRegistryValue(string name, object value, RegistryValueKind kind)
    {
        // Write to HKCU — no elevation required for user-owned settings
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var settingsKey = baseKey.CreateSubKey(BootstrapMateConstants.SettingsRegistryPath, true);
        settingsKey.SetValue(name, value, kind);
    }
}
