using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace BootstrapMate.Core;

/// <summary>The values the GUI Prefs tab edits, as handed to the elevated writer.</summary>
public sealed class PrefsSettingsPayload
{
    public string? ManifestUrl { get; set; }
    /// <summary>A new header to save; null or empty keeps the saved one.</summary>
    public string? AuthorizationHeader { get; set; }
    public bool SilentMode { get; set; }
    public bool VerboseMode { get; set; }
    public bool DryRun { get; set; }
    public bool EnableDialog { get; set; } = true;
    public string DialogTitle { get; set; } = BootstrapMateConstants.DefaultDialogTitle;
    public string DialogMessage { get; set; } = BootstrapMateConstants.DefaultDialogMessage;
    public string? DialogIcon { get; set; }
    public bool BlurScreen { get; set; }
    public string? CustomInstallPath { get; set; }
    public int NetworkTimeout { get; set; } = BootstrapMateConstants.DefaultNetworkTimeout;
}

/// <summary>
/// Saving the Prefs tab. Settings live in HKLM, so an unelevated GUI writes its pending values to
/// a JSON file in the user's temp folder and relaunches its own executable elevated with
/// <see cref="ApplyArgument"/>; that headless instance writes them and exits without a window.
/// The writer re-reads policy itself and never writes a policy-managed value.
/// </summary>
public static class PrefsSettingsFile
{
    /// <summary>Command-line switch, followed by the payload path, for the headless writer.</summary>
    public const string ApplyArgument = "--apply-settings";

    public const int ExitSaved = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitInvalid = 3;

    public const int MinNetworkTimeout = 10;
    public const int MaxNetworkTimeout = 600;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>True when the command line asks for the headless writer.</summary>
    public static bool IsApplyRequest(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a?.Trim(), ApplyArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The payload path that follows <see cref="ApplyArgument"/>, or null when missing.</summary>
    public static string? ApplyPath(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i]?.Trim(), ApplyArgument, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        }
        return null;
    }

    public static string Serialize(PrefsSettingsPayload payload) => JsonSerializer.Serialize(payload, JsonOptions);

    public static PrefsSettingsPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<PrefsSettingsPayload>(json, JsonOptions);

    /// <summary>The GUI's own rules; null when the payload is valid, otherwise why it is not.</summary>
    public static string? Validate(PrefsSettingsPayload payload)
    {
        if (payload.NetworkTimeout is < MinNetworkTimeout or > MaxNetworkTimeout)
            return $"Network timeout must be between {MinNetworkTimeout} and {MaxNetworkTimeout} seconds.";
        return null;
    }

    /// <summary>
    /// The machine-settings keys the Prefs tab writes, minus every key whose field policy
    /// manages. A blank header is left out so the saved one is kept.
    /// </summary>
    public static IReadOnlyList<string> WritableKeys(PrefsSettingsPayload payload, IReadOnlySet<string> managedKeys)
    {
        var keys = new List<string>();
        foreach (var field in PrefsPolicy.FieldKeys.Keys)
        {
            if (PrefsPolicy.IsLocked(field, managedKeys))
                continue;
            if (field == "AuthorizationHeader" && string.IsNullOrEmpty(payload.AuthorizationHeader))
                continue;
            keys.Add(field);
            // The "Show progress dialog" switch is stored as both EnableDialog and NoDialog.
            if (field == "EnableDialog")
                keys.Add("NoDialog");
        }
        return keys;
    }

    public static BootstrapMateConfig ToConfig(PrefsSettingsPayload payload) => new()
    {
        ManifestUrl = payload.ManifestUrl,
        AuthorizationHeader = string.IsNullOrWhiteSpace(payload.AuthorizationHeader) ? null : payload.AuthorizationHeader,
        SilentMode = payload.SilentMode,
        VerboseMode = payload.VerboseMode,
        DryRun = payload.DryRun,
        EnableDialog = payload.EnableDialog,
        NoDialog = !payload.EnableDialog,
        DialogTitle = payload.DialogTitle,
        DialogMessage = payload.DialogMessage,
        DialogIcon = string.IsNullOrWhiteSpace(payload.DialogIcon) ? null : payload.DialogIcon,
        BlurScreen = payload.BlurScreen,
        CustomInstallPath = string.IsNullOrWhiteSpace(payload.CustomInstallPath) ? null : payload.CustomInstallPath,
        NetworkTimeout = payload.NetworkTimeout,
    };

    /// <summary>Writes the payload where the current policy allows. Needs an elevated process.</summary>
    public static void Save(PrefsSettingsPayload payload, IReadOnlySet<string> managedKeys) =>
        ConfigManager.SaveMachineSettings(ToConfig(payload), WritableKeys(payload, managedKeys));

    /// <summary>
    /// Writes the payload to a new file in the user's temp folder, readable by the user and
    /// by Administrators (an over-the-shoulder admin elevates as a different account).
    /// </summary>
    public static string WriteTempFile(PrefsSettingsPayload payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"BootstrapMate-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Serialize(payload));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.Read | FileSystemRights.Delete, AccessControlType.Allow));
                file.SetAccessControl(security);
            }
            catch
            {
                // Same-account elevation still reads it through the owner's access.
            }
        }
        return path;
    }

    /// <summary>
    /// The headless writer: reads the payload, re-reads policy, validates and saves. Never
    /// creates a window. Returns one of the Exit* codes.
    /// </summary>
    public static int Apply(string path)
    {
        PrefsSettingsPayload? payload;
        try
        {
            payload = Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ExitUsage;
        }

        if (payload is null || Validate(payload) is not null)
            return ExitInvalid;

        try
        {
            Save(payload, ManagementDetector.Instance.AllManagedKeys());
            return ExitSaved;
        }
        catch
        {
            return ExitFailed;
        }
    }

    /// <summary>Why the headless writer did not save, for the "Not saved: …" caption.</summary>
    public static string DescribeExitCode(int code) => code switch
    {
        ExitSaved => "saved",
        ExitUsage => "the settings file could not be read.",
        ExitInvalid => "a value is not valid.",
        _ => $"the machine settings could not be written (exit code {code}).",
    };
}
