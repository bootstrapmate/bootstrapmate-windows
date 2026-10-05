using Microsoft.Win32;

namespace BootstrapMate.Core;

/// <summary>
/// Configuration loader with fallback chain (highest → lowest priority):
///   1. CLI arguments
///   2. Intune CSP / Group Policy (HKLM\SOFTWARE\Policies\BootstrapMate)
///   3. Machine settings (HKLM\SOFTWARE\BootstrapMate\Settings) — written by the MSI,
///      by sysadmins, and by the GUI app when it runs elevated
///   4. DefaultManifestUrl baked into the binary
///
/// There is deliberately no per-user settings source: every setting lives where only an
/// administrator can change it.
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

    /// <summary>
    /// Settings that were removed because nothing ever implemented them. A value still
    /// set for one in policy or the registry is reported here so the run can say it is
    /// ignored, instead of dropping it silently as it always did.
    /// </summary>
    // A property, not a field: Instance is built by an earlier static initializer and
    // reads this while the class is still initializing.
    public static string[] RetiredSettingNames => ["FollowRedirects", "Reboot"];

    /// <summary>Retired settings still turned on somewhere, as "Name (where)". A saved false is ignored.</summary>
    public List<string> RetiredSettingsPresent { get; } = new();

    private ConfigManager()
    {
        LoadManagementAndMachineSettings();
    }

    public enum ConfigSource
    {
        Default,
        MachineSettings,
        Management,
        CliArgument
    }

    /// <summary>
    /// Apply CLI arguments (highest priority — overrides everything).
    /// </summary>
    public void ApplyCliArguments(
        string? manifestUrl = null,
        string? authorizationHeader = null,
        bool? silentMode = null,
        bool? verboseMode = null,
        bool? dryRun = null,
        bool? noDialog = null,
        string? dialogTitle = null,
        string? dialogMessage = null,
        int? networkTimeout = null,
        string? reportingUrl = null,
        string? reportingHeader = null,
        bool? verifyPackageSignatures = null,
        string? expectedPublisher = null,
        bool? allowUnsigned = null)
    {
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            Config.ManifestUrl = manifestUrl;
            ManifestUrlSource = ConfigSource.CliArgument;
        }
        if (!string.IsNullOrWhiteSpace(authorizationHeader))
            Config.AuthorizationHeader = authorizationHeader;
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
        if (!string.IsNullOrWhiteSpace(reportingUrl))
            Config.ReportingUrl = reportingUrl;
        if (!string.IsNullOrWhiteSpace(reportingHeader))
            Config.ReportingHeader = reportingHeader;
        if (verifyPackageSignatures.HasValue)
            Config.VerifyPackageSignatures = verifyPackageSignatures.Value;
        if (!string.IsNullOrWhiteSpace(expectedPublisher))
            Config.ExpectedPublisher = expectedPublisher;
        if (allowUnsigned.HasValue)
            Config.AllowUnsigned = allowUnsigned.Value;
    }

    /// <summary>Get the effective manifest URL from whatever source is active.</summary>
    public string? GetEffectiveManifestUrl() => Config.ManifestUrl;

    /// <summary>Get the installation path.</summary>
    public string GetInstallPath()
        => Config.CustomInstallPath ?? BootstrapMateConstants.DefaultInstallPath;

    /// <summary>Check if a minimum valid configuration exists (manifest URL set).</summary>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Config.ManifestUrl);

    /// <summary>
    /// Reload settings from management + machine registry. Call when waiting for
    /// Intune policy to land post-enrollment.
    /// Returns true if a valid manifest URL was found.
    /// </summary>
    public bool ReloadSettings()
    {
        Config.ManifestUrl = null;
        ManifestUrlSource = ConfigSource.Default;
        LoadManagementAndMachineSettings();
        return IsValid();
    }

    /// <summary>
    /// Save settings to HKLM\SOFTWARE\BootstrapMate\Settings.
    /// Skips any key that is already managed (set via Group Policy / Intune).
    /// Writing HKLM requires an elevated process; a non-elevated caller gets
    /// UnauthorizedAccessException and nothing is written.
    /// </summary>
    /// <param name="settings">Values to write.</param>
    /// <param name="onlyKeys">
    /// When given, only these keys are written, so a caller that edits a subset of settings
    /// (the GUI Prefs tab) never overwrites the rest of the machine settings with defaults.
    /// </param>
    public static void SaveMachineSettings(BootstrapMateConfig settings, IReadOnlyCollection<string>? onlyKeys = null)
    {
        var management = ManagementDetector.Instance;

        bool Skip(string key) => management.IsManaged(key) ||
            (onlyKeys is not null && !onlyKeys.Contains(key, StringComparer.OrdinalIgnoreCase));

        void WriteString(string key, string? value)
        {
            if (Skip(key)) return;
            WriteRegistryValue(key, value ?? string.Empty, RegistryValueKind.String);
        }

        void WriteBool(string key, bool value)
        {
            if (Skip(key)) return;
            WriteRegistryValue(key, value ? 1 : 0, RegistryValueKind.DWord);
        }

        void WriteInt(string key, int value)
        {
            if (Skip(key)) return;
            WriteRegistryValue(key, value, RegistryValueKind.DWord);
        }

        WriteString("ManifestUrl", settings.ManifestUrl);
        if (!string.IsNullOrEmpty(settings.AuthorizationHeader))
            WriteString("AuthorizationHeader", settings.AuthorizationHeader);
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
        WriteInt("BaselineMinIntervalHours", settings.BaselineMinIntervalHours);
        WriteString("ReportingUrl", settings.ReportingUrl);
        WriteString("ReportingHeader", settings.ReportingHeader);
        WriteBool("VerifyPackageSignatures", settings.VerifyPackageSignatures);
        WriteString("ExpectedPublisher", settings.ExpectedPublisher);
        WriteBool("AllowUnsigned", settings.AllowUnsigned);
    }

    // ── Private ──────────────────────────────────────────────────────

    private void LoadManagementAndMachineSettings()
    {
        var management = ManagementDetector.Instance;
        RetiredSettingsPresent.Clear();

        // Lowest priority first: baked-in default → HKLM machine → CSP policy.
        // Higher-priority sources overwrite earlier ones; CLI runs last from caller.
        if (string.IsNullOrWhiteSpace(Config.ManifestUrl) &&
            !string.IsNullOrWhiteSpace(BootstrapMateConstants.DefaultManifestUrl))
        {
            Config.ManifestUrl = BootstrapMateConstants.DefaultManifestUrl;
            ManifestUrlSource = ConfigSource.Default;
        }

        LoadFromMachineRegistry();
        LoadFromManagement(management);
    }

    private void LoadFromManagement(ManagementDetector management)
    {
        if (management.GetManagedString("ManifestUrl") is { Length: > 0 } url)
        {
            Config.ManifestUrl = url;
            ManifestUrlSource = ConfigSource.Management;
        }

        if (management.GetManagedString("AuthorizationHeader") is { Length: > 0 } auth)
            Config.AuthorizationHeader = auth;

        foreach (var retired in RetiredSettingNames)
        {
            if (management.GetManagedBool(retired) == true)
                RetiredSettingsPresent.Add($"{retired} (policy)");
        }

        if (management.GetManagedBool("SilentMode") is { } silent)
            Config.SilentMode = silent;

        if (management.GetManagedBool("VerboseMode") is { } verbose)
            Config.VerboseMode = verbose;

        if (management.GetManagedBool("DryRun") is { } dryRun)
            Config.DryRun = dryRun;

        if (management.GetManagedBool("EnableDialog") is { } enableDialog)
            Config.EnableDialog = enableDialog;

        if (management.GetManagedBool("NoDialog") is { } noDialog)
            Config.NoDialog = noDialog;

        if (management.GetManagedString("DialogTitle") is { Length: > 0 } title)
            Config.DialogTitle = title;

        if (management.GetManagedString("DialogMessage") is { Length: > 0 } msg)
            Config.DialogMessage = msg;

        if (management.GetManagedString("DialogIcon") is { Length: > 0 } icon)
            Config.DialogIcon = icon;

        if (management.GetManagedBool("BlurScreen") is { } blur)
            Config.BlurScreen = blur;

        if (management.GetManagedString("CustomInstallPath") is { Length: > 0 } path)
            Config.CustomInstallPath = path;

        if (management.GetManagedInt("NetworkTimeout") is { } timeout)
            Config.NetworkTimeout = timeout;

        if (management.GetManagedInt("BaselineMinIntervalHours") is { } baselineHours)
            Config.BaselineMinIntervalHours = baselineHours;

        if (management.GetManagedString("ReportingUrl") is { Length: > 0 } reportingUrl)
            Config.ReportingUrl = reportingUrl;

        if (management.GetManagedString("ReportingHeader") is { Length: > 0 } reportingHeader)
            Config.ReportingHeader = reportingHeader;
        if (management.GetManagedBool("VerifyPackageSignatures") is { } verifySig)
            Config.VerifyPackageSignatures = verifySig;

        if (management.GetManagedString("ExpectedPublisher") is { Length: > 0 } publisher)
            Config.ExpectedPublisher = publisher;

        if (management.GetManagedBool("AllowUnsigned") is { } allowUnsigned)
            Config.AllowUnsigned = allowUnsigned;
    }

    private void LoadFromMachineRegistry()
    {
        // HKLM\SOFTWARE\BootstrapMate\Settings — read from both registry views so
        // a 32-bit MSI write is visible to the 64-bit binary and vice-versa.
        LoadFromHive(RegistryHive.LocalMachine, ConfigSource.MachineSettings, RegistryView.Registry64);
        LoadFromHive(RegistryHive.LocalMachine, ConfigSource.MachineSettings, RegistryView.Registry32);
    }

    private void LoadFromHive(RegistryHive hive, ConfigSource source, RegistryView view = RegistryView.Default)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var settingsKey = baseKey.OpenSubKey(BootstrapMateConstants.MachineSettingsRegistryPath);
            if (settingsKey is null) return;

            var url = ReadString(settingsKey, "ManifestUrl");
            if (!string.IsNullOrWhiteSpace(url))
            {
                Config.ManifestUrl = url;
                if (ManifestUrlSource == ConfigSource.Default)
                    ManifestUrlSource = source;
            }

            Config.AuthorizationHeader = ReadString(settingsKey, "AuthorizationHeader") ?? Config.AuthorizationHeader;
            foreach (var retired in RetiredSettingNames)
            {
                if (ReadBool(settingsKey, retired) == true)
                    RetiredSettingsPresent.Add($"{retired} ({hive} settings)");
            }
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
            Config.BaselineMinIntervalHours = ReadInt(settingsKey, "BaselineMinIntervalHours") ?? Config.BaselineMinIntervalHours;
            Config.ReportingUrl = ReadString(settingsKey, "ReportingUrl") ?? Config.ReportingUrl;
            Config.ReportingHeader = ReadString(settingsKey, "ReportingHeader") ?? Config.ReportingHeader;
            Config.VerifyPackageSignatures = ReadBool(settingsKey, "VerifyPackageSignatures") ?? Config.VerifyPackageSignatures;
            Config.ExpectedPublisher = ReadString(settingsKey, "ExpectedPublisher") ?? Config.ExpectedPublisher;
            Config.AllowUnsigned = ReadBool(settingsKey, "AllowUnsigned") ?? Config.AllowUnsigned;
        }
        catch
        {
            // Registry unavailable — fall through to defaults / next source
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
        // HKLM, 64-bit view: the location the MSI writes and every SYSTEM run reads.
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var settingsKey = baseKey.CreateSubKey(BootstrapMateConstants.MachineSettingsRegistryPath, true);
        settingsKey.SetValue(name, value, kind);
    }
}
