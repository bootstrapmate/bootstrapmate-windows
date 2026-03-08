namespace BootstrapMate.Core;

/// <summary>
/// Shared constants for BootstrapMate, mirroring macOS BootstrapMateConstants.
/// </summary>
public static class BootstrapMateConstants
{
    /// <summary>Registry path where Intune CSP / Group Policy writes managed settings.</summary>
    public const string PolicyRegistryPath = @"SOFTWARE\Policies\BootstrapMate";

    /// <summary>Registry path for user-configured settings (written by the GUI app).</summary>
    public const string SettingsRegistryPath = @"SOFTWARE\BootstrapMate\Settings";

    /// <summary>Registry path for status tracking.</summary>
    public const string StatusRegistryPath = @"SOFTWARE\Cimian\BootstrapMate\Status";

    /// <summary>Registry path for version/completion info.</summary>
    public const string VersionRegistryPath = @"SOFTWARE\Cimian\BootstrapMate";

    /// <summary>Default installation directory.</summary>
    public const string DefaultInstallPath = @"C:\Program Files\BootstrapMate";

    /// <summary>Log file directory.</summary>
    public const string LogDirectory = @"C:\ProgramData\ManagedBootstrap\logs";

    /// <summary>Cache directory for downloaded packages.</summary>
    public const string CacheDirectory = @"C:\ProgramData\ManagedBootstrap\cache";

    /// <summary>CLI executable name.</summary>
    public const string CliExecutableName = "installapplications.exe";

    /// <summary>Default network timeout in seconds.</summary>
    public const int DefaultNetworkTimeout = 120;

    /// <summary>Default dialog title.</summary>
    public const string DefaultDialogTitle = "Setting Up Your Device";

    /// <summary>Default dialog message.</summary>
    public const string DefaultDialogMessage = "Please wait while we install required software...";

    /// <summary>Named pipe prefix for GUI ↔ CLI communication.</summary>
    public const string PipeNamePrefix = "BootstrapMate_";

    /// <summary>Version string — injected at build time.</summary>
    public static readonly string Version = GetBuildVersion();

    private static string GetBuildVersion()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly()
            ?? System.Reflection.Assembly.GetExecutingAssembly();
        var attribute = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp");
        return attribute?.Value ?? "dev.build";
    }
}
