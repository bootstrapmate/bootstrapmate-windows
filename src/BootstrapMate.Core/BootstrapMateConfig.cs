namespace BootstrapMate.Core;

/// <summary>
/// All configuration settings for BootstrapMate.
/// Mirrors macOS BootstrapMateConfig struct for cross-platform parity.
/// </summary>
public sealed class BootstrapMateConfig
{
    // Connection
    public string? ManifestUrl { get; set; }
    public string? AuthorizationHeader { get; set; }
    public bool FollowRedirects { get; set; }

    // Behavior
    public bool Reboot { get; set; }
    public bool SilentMode { get; set; }
    public bool VerboseMode { get; set; }
    public bool DryRun { get; set; }

    // Dialog / UI
    public bool EnableDialog { get; set; } = true;
    public string DialogTitle { get; set; } = BootstrapMateConstants.DefaultDialogTitle;
    public string DialogMessage { get; set; } = BootstrapMateConstants.DefaultDialogMessage;
    public string? DialogIcon { get; set; }
    public bool BlurScreen { get; set; }
    public bool NoDialog { get; set; }

    // Advanced
    public string? CustomInstallPath { get; set; }
    public int NetworkTimeout { get; set; } = BootstrapMateConstants.DefaultNetworkTimeout;

    /// <summary>Creates a deep copy of this configuration.</summary>
    public BootstrapMateConfig Clone() => (BootstrapMateConfig)MemberwiseClone();
}
