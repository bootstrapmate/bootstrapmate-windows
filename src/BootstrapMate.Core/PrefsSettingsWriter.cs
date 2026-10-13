using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
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
/// Saving the Prefs tab. Settings live in HKLM, so an unelevated GUI relaunches its own executable
/// elevated with <see cref="ApplyArgument"/> and a pipe name, then sends the pending values as JSON
/// over that named pipe; nothing touches disk, since the values can include a credential header.
/// The pipe admits only the current user, Administrators and SYSTEM, so an over-the-shoulder admin
/// account can read it. The headless writer re-reads policy itself, never writes a policy-managed
/// value, and exits without a window.
/// </summary>
public static class PrefsSettingsWriter
{
    /// <summary>Command-line switch, followed by the pipe name, for the headless writer.</summary>
    public const string ApplyArgument = "--apply-settings";

    public const int ExitSaved = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitInvalid = 3;

    /// <summary>How long either side waits for the other; covers the UAC prompt.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    public const int MinNetworkTimeout = 10;
    public const int MaxNetworkTimeout = 600;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>True when the command line asks for the headless writer.</summary>
    public static bool IsApplyRequest(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a?.Trim(), ApplyArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pipe name that follows <see cref="ApplyArgument"/>, or null when missing.</summary>
    public static string? ApplyPipeName(IReadOnlyList<string> args)
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

    /// <summary>A new, unguessable pipe name for one save.</summary>
    public static string NewPipeName() => $"BootstrapMate-settings-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates the one-instance outbound pipe the GUI sends the payload on. Only the current
    /// user, BUILTIN\Administrators and SYSTEM may open it.
    /// </summary>
    public static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        }
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
        }
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Waits for the writer to connect, sends the payload and closes the pipe.</summary>
    public static async Task SendAsync(NamedPipeServerStream pipe, PrefsSettingsPayload payload, CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(Serialize(payload));
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        pipe.WaitForPipeDrain();
        pipe.Disconnect();
    }

    /// <summary>Connects to the GUI's pipe and reads the payload JSON to the end.</summary>
    public static string Receive(string pipeName, TimeSpan timeout)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
        client.Connect((int)timeout.TotalMilliseconds);
        using var reader = new StreamReader(client, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The headless writer: reads the payload from the pipe, re-reads policy, validates and saves.
    /// Never creates a window. Returns one of the Exit* codes.
    /// </summary>
    public static int Apply(string pipeName) => Apply(pipeName, ConnectTimeout);

    public static int Apply(string pipeName, TimeSpan timeout)
    {
        PrefsSettingsPayload? payload;
        try
        {
            payload = Deserialize(Receive(pipeName, timeout));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
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
        ExitUsage => "the settings could not be passed to the elevated writer.",
        ExitInvalid => "a value is not valid.",
        _ => $"the machine settings could not be written (exit code {code}).",
    };
}
