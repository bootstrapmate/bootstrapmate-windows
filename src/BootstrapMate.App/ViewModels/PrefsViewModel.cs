using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using BootstrapMate.Core;

namespace BootstrapMate.App.ViewModels;

/// <summary>
/// ViewModel for the Prefs tab. Mirrors macOS SettingsViewModel.
/// Loads from ConfigManager and shows management lock state. Settings live in HKLM, so the tab is
/// read-only unless this process is elevated; Unlock relaunches the app elevated on this tab.
/// When elevated, non-managed settings auto-save to HKLM through ConfigManager.SaveMachineSettings.
/// </summary>
public partial class PrefsViewModel : ObservableObject
{
    private System.Threading.Timer? _autoSaveTimer;
    private bool _isLoading;

    // ── Connection ───────────────────────────────────────────────

    [ObservableProperty] private string _manifestUrl = "";
    [ObservableProperty] private string _authorizationHeader = "";
    [ObservableProperty] private bool _hasExistingAuth;

    // ── Behavior ─────────────────────────────────────────────────

    [ObservableProperty] private bool _silentMode;
    [ObservableProperty] private bool _verboseMode;
    [ObservableProperty] private bool _dryRun;

    // ── Dialog ───────────────────────────────────────────────────

    [ObservableProperty] private bool _enableDialog = true;
    [ObservableProperty] private string _dialogTitle = BootstrapMateConstants.DefaultDialogTitle;
    [ObservableProperty] private string _dialogMessage = BootstrapMateConstants.DefaultDialogMessage;
    [ObservableProperty] private string _dialogIcon = "";
    [ObservableProperty] private bool _blurScreen;

    // ── Advanced ─────────────────────────────────────────────────

    [ObservableProperty] private string _customInstallPath = "";
    [ObservableProperty] private int _networkTimeout = BootstrapMateConstants.DefaultNetworkTimeout;
    // ── Manifest Preview ─────────────────────────────────────

    [ObservableProperty] private bool _isManifestPreviewLoading;
    [ObservableProperty] private string _manifestPreviewContent = "";
    [ObservableProperty] private string _manifestPreviewError = "";

    public bool HasManifestPreviewContent => !string.IsNullOrEmpty(ManifestPreviewContent);
    public bool HasManifestPreviewError   => !string.IsNullOrEmpty(ManifestPreviewError);

    partial void OnManifestPreviewContentChanged(string value) => OnPropertyChanged(nameof(HasManifestPreviewContent));
    partial void OnManifestPreviewErrorChanged(string value)   => OnPropertyChanged(nameof(HasManifestPreviewError));
    // ── Save Status ──────────────────────────────────────────────

    [ObservableProperty] private SaveState _saveStatus = SaveState.Idle;

    public enum SaveState { Idle, Saving, Saved, Failed }

    partial void OnSaveStatusChanged(SaveState value)
    {
        OnPropertyChanged(nameof(SaveStatusGlyph));
        OnPropertyChanged(nameof(SaveStatusMessage));
        OnPropertyChanged(nameof(IsSaveStatusVisible));
        OnPropertyChanged(nameof(IsSaveEnabled));
    }

    // ── Management State ─────────────────────────────────────────────

    private HashSet<string> _managedKeys = [];

    public bool IsManaged(string key) => _managedKeys.Contains(key);

    // ── Binding-Friendly Display Properties ──────────────────────

    public string VersionDisplay => $"Version {AppVersion}";

    public string AuthPlaceholderText => HasExistingAuth
        ? "Token saved — enter new to replace"
        : "Bearer token or Basic auth";

    partial void OnHasExistingAuthChanged(bool value) => OnPropertyChanged(nameof(AuthPlaceholderText));

    // NumberBox.Value is double — expose a compatible wrapper
    public double NetworkTimeoutValue
    {
        get => NetworkTimeout;
        set => NetworkTimeout = (int)value;
    }

    partial void OnNetworkTimeoutChanged(int value) => OnPropertyChanged(nameof(NetworkTimeoutValue));

    // ── Policy Lock Properties (for x:Bind) ─────────────────────

    // ── Elevation State ─────────────────────────────────────────

    /// <summary>True when this process can write HKLM (elevated administrator token).</summary>
    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    public bool IsReadOnly => !IsElevated;

    [ObservableProperty] private string _unlockError = "";

    public bool HasUnlockError => !string.IsNullOrEmpty(UnlockError);

    partial void OnUnlockErrorChanged(string value) => OnPropertyChanged(nameof(HasUnlockError));

    /// <summary>
    /// Relaunches this app elevated through UAC, opening on the Prefs tab. Returns true when the
    /// elevated instance started, so the caller can close this one. A cancelled UAC prompt
    /// returns false quietly and leaves the tab read-only.
    /// </summary>
    public bool TryRelaunchElevated()
    {
        UnlockError = "";
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                UnlockError = "Could not find the app's own executable to relaunch.";
                return false;
            }

            using var process = System.Diagnostics.Process.Start(PrefsElevation.BuildElevatedRelaunch(exe));
            return process is not null;
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            UnlockError = $"Could not relaunch as administrator: {ex.Message}";
            return false;
        }
    }

    // ── Editability (elevated and not managed by policy) ────────

    private bool CanEdit(string field) => PrefsElevation.CanEdit(IsElevated, IsLocked(field));

    private bool IsLocked(string field) => PrefsPolicy.IsLocked(field, _managedKeys);

    public bool CanEditManifestUrl => CanEdit("ManifestUrl");
    public bool CanEditAuthHeader => CanEdit("AuthorizationHeader");
    public bool CanEditSilentMode => CanEdit("SilentMode");
    public bool CanEditVerboseMode => CanEdit("VerboseMode");
    public bool CanEditDryRun => CanEdit("DryRun");
    public bool CanEditEnableDialog => CanEdit("EnableDialog");
    public bool CanEditDialogTitle => CanEdit("DialogTitle");
    public bool CanEditDialogMessage => CanEdit("DialogMessage");
    public bool CanEditDialogIcon => CanEdit("DialogIcon");
    public bool CanEditBlurScreen => CanEdit("BlurScreen");
    public bool CanEditCustomInstallPath => CanEdit("CustomInstallPath");
    public bool CanEditNetworkTimeout => CanEdit("NetworkTimeout");

    public bool IsManifestUrlLocked => IsLocked("ManifestUrl");
    public bool IsAuthHeaderLocked => IsLocked("AuthorizationHeader");
    public bool IsSilentModeLocked => IsLocked("SilentMode");
    public bool IsVerboseModeLocked => IsLocked("VerboseMode");
    public bool IsDryRunLocked => IsLocked("DryRun");
    public bool IsEnableDialogLocked => IsLocked("EnableDialog");
    public bool IsDialogTitleLocked => IsLocked("DialogTitle");
    public bool IsDialogMessageLocked => IsLocked("DialogMessage");
    public bool IsDialogIconLocked => IsLocked("DialogIcon");
    public bool IsBlurScreenLocked => IsLocked("BlurScreen");
    public bool IsCustomInstallPathLocked => IsLocked("CustomInstallPath");
    public bool IsNetworkTimeoutLocked => IsLocked("NetworkTimeout");

    // ── Save Status Display (for x:Bind) ────────────────────────

    public string SaveStatusGlyph => SaveStatus switch
    {
        SaveState.Saving => "\uE895",
        SaveState.Saved  => "\uE73E",
        SaveState.Failed => "\uE783",
        _ => ""
    };

    public string SaveStatusMessage => SaveStatus switch
    {
        SaveState.Saving => "Saving...",
        SaveState.Saved  => "Saved",
        SaveState.Failed => "Save failed — check permissions",
        _ => ""
    };

    public bool IsSaveStatusVisible => SaveStatus != SaveState.Idle;
    public bool IsSaveEnabled => SaveStatus != SaveState.Saving;

    // ── Version Info ─────────────────────────────────────────────

    public string AppVersion => BootstrapMateConstants.Version;

    // ── Load ─────────────────────────────────────────────────────

    public void Load()
    {
        _isLoading = true;

        var configMgr = ConfigManager.Instance;
        configMgr.ReloadSettings();
        var config = configMgr.Config;

        _managedKeys = ManagementDetector.Instance.AllManagedKeys();

        ManifestUrl = config.ManifestUrl ?? "";
        HasExistingAuth = !string.IsNullOrEmpty(config.AuthorizationHeader);
        AuthorizationHeader = "";  // Don't display existing token — match Mac behavior
        SilentMode = config.SilentMode;
        VerboseMode = config.VerboseMode;
        DryRun = config.DryRun;
        EnableDialog = PrefsPolicy.IsDialogShown(config);
        DialogTitle = config.DialogTitle;
        DialogMessage = config.DialogMessage;
        DialogIcon = config.DialogIcon ?? "";
        BlurScreen = config.BlurScreen;
        CustomInstallPath = config.CustomInstallPath ?? "";
        NetworkTimeout = config.NetworkTimeout;

        // Notify all bindings (management locks, computed display properties)
        OnPropertyChanged(string.Empty);

        _isLoading = false;
    }

    // ── Auto-Save ─────────────────────────────────────────────────

    private static readonly HashSet<string> _nonSettingProperties =
    [
        nameof(SaveStatus), nameof(SaveStatusGlyph), nameof(SaveStatusMessage),
        nameof(IsSaveStatusVisible), nameof(IsSaveEnabled), nameof(HasExistingAuth),
        nameof(AuthPlaceholderText), nameof(NetworkTimeoutValue), nameof(VersionDisplay),
        nameof(IsManifestPreviewLoading), nameof(ManifestPreviewContent), nameof(ManifestPreviewError),
        nameof(HasManifestPreviewContent), nameof(HasManifestPreviewError),
        nameof(UnlockError), nameof(HasUnlockError),
        "", // string.Empty from Load's bulk notify
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Not elevated: HKLM is not writable and the tab is read-only, so never save.
        if (_isLoading || !IsElevated || _nonSettingProperties.Contains(e.PropertyName ?? ""))
            return;

        _autoSaveTimer?.Dispose();
        _autoSaveTimer = new System.Threading.Timer(_ =>
        {
            try { ConfigManager.SaveMachineSettings(BuildConfig(), PageKeys); } catch { }
        }, null, 500, System.Threading.Timeout.Infinite);
    }

    // ── Build Run Arguments ──────────────────────────────────────

    /// <summary>Builds CLI arguments from current settings, mirroring macOS buildRunArguments().</summary>
    public List<string> BuildRunArguments()
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(ManifestUrl))
        {
            args.Add("--url");
            args.Add(ManifestUrl);
        }

        // No --headers: the header is saved to the protected store, where the elevated run
        // reads it, and never travels on a command line.

        if (DryRun) args.Add("--dry-run");
        if (SilentMode) args.Add("--silent");
        if (VerboseMode) args.Add("--verbose");
        if (!EnableDialog) args.Add("--no-dialog");
        if (BlurScreen) args.Add("--blur-screen");

        if (DialogTitle != BootstrapMateConstants.DefaultDialogTitle && !string.IsNullOrWhiteSpace(DialogTitle))
        {
            args.Add("--dialog-title");
            args.Add(DialogTitle);
        }

        if (DialogMessage != BootstrapMateConstants.DefaultDialogMessage && !string.IsNullOrWhiteSpace(DialogMessage))
        {
            args.Add("--dialog-message");
            args.Add(DialogMessage);
        }

        return args;
    }

    // ── Private ──────────────────────────────────────────────────

    /// <summary>
    /// The machine settings this tab edits. Saving writes only these, so values the tab does not
    /// show (reporting, signature checks, baseline interval) keep whatever HKLM already holds.
    /// </summary>
    private static readonly string[] PageKeys =
    [
        "ManifestUrl", "AuthorizationHeader", "SilentMode", "VerboseMode", "DryRun",
        "EnableDialog", "NoDialog", "DialogTitle", "DialogMessage", "DialogIcon", "BlurScreen",
        "CustomInstallPath", "NetworkTimeout",
    ];

    private BootstrapMateConfig BuildConfig() => new()
    {
        ManifestUrl = ManifestUrl,
        AuthorizationHeader = string.IsNullOrWhiteSpace(AuthorizationHeader) ? null : AuthorizationHeader,
        SilentMode = SilentMode,
        VerboseMode = VerboseMode,
        DryRun = DryRun,
        EnableDialog = EnableDialog,
        NoDialog = !EnableDialog,
        DialogTitle = DialogTitle,
        DialogMessage = DialogMessage,
        DialogIcon = string.IsNullOrWhiteSpace(DialogIcon) ? null : DialogIcon,
        BlurScreen = BlurScreen,
        CustomInstallPath = string.IsNullOrWhiteSpace(CustomInstallPath) ? null : CustomInstallPath,
        NetworkTimeout = NetworkTimeout,
    };

    // ── Manifest Preview Fetch ───────────────────────────────

    /// <summary>
    /// Fetches the configured manifest URL (with auth) and stores the raw content for display.
    /// </summary>
    public async Task FetchManifestPreviewAsync()
    {
        IsManifestPreviewLoading = true;
        ManifestPreviewContent = "";
        ManifestPreviewError = "";

        var url = ManifestUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            ManifestPreviewError = "No manifest URL is configured.";
            IsManifestPreviewLoading = false;
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(NetworkTimeout > 0 ? NetworkTimeout : 30);

            // Use the value the user has currently typed; fall back to the saved token.
            var effectiveAuth = !string.IsNullOrWhiteSpace(AuthorizationHeader)
                ? AuthorizationHeader
                : ConfigManager.Instance.Config.AuthorizationHeader;

            if (!string.IsNullOrWhiteSpace(effectiveAuth))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", effectiveAuth);

            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            ManifestPreviewContent = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            ManifestPreviewError = ex.Message;
        }
        finally
        {
            IsManifestPreviewLoading = false;
        }
    }

    private static string? FindCliExecutable()
    {
        // Use OSArchitecture so an x64 binary running under ARM64 emulation reports ARM64
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => "x64"
        };

        var candidates = new[]
        {
            Path.Combine(BootstrapMateConstants.DefaultInstallPath, BootstrapMateConstants.CliExecutableName),
            Path.Combine(AppContext.BaseDirectory, BootstrapMateConstants.CliExecutableName),
            Path.Combine(AppContext.BaseDirectory, "..", "BootstrapMate.CLI", BootstrapMateConstants.CliExecutableName),
            // Dev/publish layout: app at publish/app/<arch>/, CLI at publish/executables/<arch>/
            Path.Combine(AppContext.BaseDirectory, "..", "..", "executables", arch, BootstrapMateConstants.CliExecutableName),
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
