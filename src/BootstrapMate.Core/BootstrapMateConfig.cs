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

    // Behavior. DryRun is honoured by refusing to run: there is no simulated install.
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
    /// <summary>File holding the plain csharpDialog authorisation key; null means the default path.</summary>
    public string? DialogAuthKeyPath { get; set; }

    // Reporting: vendor-neutral run-summary POST
    public string? ReportingUrl { get; set; }
    public string? ReportingHeader { get; set; }
    // Security: installer signature verification
    public bool VerifyPackageSignatures { get; set; } = true;
    public string? ExpectedPublisher { get; set; }
    public bool AllowUnsigned { get; set; }

    // Advanced
    public string? CustomInstallPath { get; set; }
    /// <summary>
    /// Seconds: the manifest request's timeout, and how long a package download may go
    /// without receiving data before the attempt fails. Clamped to 10-600.
    /// </summary>
    public int NetworkTimeout { get; set; } = BootstrapMateConstants.DefaultNetworkTimeout;

    /// <summary>
    /// Minimum hours between completed baseline runs; a failed baseline retries after 24.
    /// See <see cref="BaselineThrottle"/>.
    /// </summary>
    public int BaselineMinIntervalHours { get; set; } = BaselineThrottle.DefaultMinIntervalHours;

    /// <summary>Creates a deep copy of this configuration.</summary>
    public BootstrapMateConfig Clone() => (BootstrapMateConfig)MemberwiseClone();
}
