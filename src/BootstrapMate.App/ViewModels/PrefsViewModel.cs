using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using BootstrapMate.Core;

namespace BootstrapMate.App.ViewModels;

/// <summary>
/// ViewModel for the Prefs tab. Mirrors macOS SettingsViewModel.
/// Loads from ConfigManager and shows management lock state. Every setting policy does not manage
/// is editable; edits stay pending until Save writes them to HKLM, directly when this process is
/// elevated and otherwise through an elevated, headless copy of the app (UAC at save time).
/// </summary>
public partial class PrefsViewModel : ObservableObject
{
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

    public enum SaveState { Idle, Saving, Saved }

    partial void OnSaveStatusChanged(SaveState value)
    {
        OnPropertyChanged(nameof(SaveStatusMessage));
        OnPropertyChanged(nameof(IsSaveStatusVisible));
        OnPropertyChanged(nameof(IsSaveEnabled));
        OnPropertyChanged(nameof(IsSaveBarVisible));
    }

    // ── Management State ─────────────────────────────────────────────

    private HashSet<string> _managedKeys = [];

    public bool IsManaged(string key) => _managedKeys.Contains(key);

    /// <summary>True when any value comes from the policy key; shows the policy note on the tab.</summary>
    public bool HasPolicySettings => _managedKeys.Count > 0;

    // ── Services ─────────────────────────────────────────────────

    /// <summary>The services and scheduled tasks the installer creates, with their live state.</summary>
    [ObservableProperty] private IReadOnlyList<FootprintStatus> _services = [];

    [ObservableProperty] private bool _isServicesLoading;

    /// <summary>Queries the installer's services and tasks off the UI thread.</summary>
    public async Task LoadServicesAsync()
    {
        if (IsServicesLoading)
            return;
        IsServicesLoading = true;
        try
        {
            Services = await Task.Run(InstallFootprint.QueryAll);
        }
        finally
        {
            IsServicesLoading = false;
        }
    }

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

    // ── Pending Changes and Save ────────────────────────────────

    /// <summary>True when this process can write HKLM directly (elevated administrator token).</summary>
    private static readonly bool IsElevated = PrefsElevation.IsProcessElevated();

    /// <summary>True once a setting is edited and until it is saved or discarded.</summary>
    [ObservableProperty] private bool _hasPendingChanges;

    /// <summary>Why the last Save did not write, as "Not saved: …"; empty otherwise.</summary>
    [ObservableProperty] private string _saveError = "";

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    partial void OnSaveErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasSaveError));
        OnPropertyChanged(nameof(IsSaveBarVisible));
    }

    partial void OnHasPendingChangesChanged(bool value) => OnPropertyChanged(nameof(IsSaveBarVisible));

    /// <summary>The save bar shows while there is anything to save or report.</summary>
    public bool IsSaveBarVisible => HasPendingChanges || HasSaveError || IsSaveStatusVisible;

    /// <summary>
    /// Writes the pending values to the machine settings, skipping anything policy manages.
    /// Elevated, it writes directly; otherwise it raises UAC and runs this app's own executable
    /// headless, sending the values over an ACL'd named pipe. A declined prompt keeps the edits pending.
    /// </summary>
    public async Task SaveAsync()
    {
        if (!HasPendingChanges || SaveStatus == SaveState.Saving)
            return;

        var payload = BuildPayload();
        if (PrefsSettingsWriter.Validate(payload) is { } invalid)
        {
            SaveError = $"Not saved: {invalid}";
            return;
        }

        SaveError = "";
        SaveStatus = SaveState.Saving;
        string? error;
        try
        {
            error = IsElevated
                ? await Task.Run(() => { PrefsSettingsWriter.Save(payload, _managedKeys); return (string?)null; })
                : await SaveElevatedAsync(payload);
        }
        catch (Exception ex)
        {
            error = $"Not saved: {ex.Message}";
        }

        if (error is not null)
        {
            SaveStatus = SaveState.Idle;
            SaveError = error;
            return;
        }

        Load();
        SaveStatus = SaveState.Saved;
    }

    /// <summary>Drops the pending edits and shows the saved values again.</summary>
    public void Discard()
    {
        SaveError = "";
        SaveStatus = SaveState.Idle;
        Load();
    }

    private static async Task<string?> SaveElevatedAsync(PrefsSettingsPayload payload)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return "Not saved: could not find the app's own executable.";

        // The values travel over a pipe only this user, Administrators and SYSTEM can open,
        // never through a file: they can include a credential header.
        var pipeName = PrefsSettingsWriter.NewPipeName();
        using var pipe = PrefsSettingsWriter.CreatePipe(pipeName);

        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(PrefsElevation.BuildElevatedApply(exe, pipeName));
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            return "Not saved: administrator approval was declined.";
        }
        if (process is null)
            return "Not saved: the elevated writer did not start.";

        using (process)
        {
            using var timeout = new CancellationTokenSource(PrefsSettingsWriter.ConnectTimeout);
            var send = PrefsSettingsWriter.SendAsync(pipe, payload, timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAny(send, exited);

            if (!send.IsCompletedSuccessfully)
            {
                timeout.Cancel();
                try { await send; } catch { }
                return process.HasExited
                    ? $"Not saved: {PrefsSettingsWriter.DescribeExitCode(process.ExitCode)}"
                    : "Not saved: the elevated writer did not connect in time.";
            }

            using var finish = new CancellationTokenSource(PrefsSettingsWriter.ConnectTimeout);
            try
            {
                await process.WaitForExitAsync(finish.Token);
            }
            catch (OperationCanceledException)
            {
                return "Not saved: the elevated writer did not finish in time.";
            }

            return process.ExitCode == PrefsSettingsWriter.ExitSaved
                ? null
                : $"Not saved: {PrefsSettingsWriter.DescribeExitCode(process.ExitCode)}";
        }
    }

    // ── Editability (only policy locks a setting) ───────────────

    private bool CanEdit(string field) => PrefsPolicy.CanEdit(field, _managedKeys);

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

    // ── Save Status Display (for x:Bind) ────────────────────────

    public string SaveStatusMessage => SaveStatus switch
    {
        SaveState.Saving => "Saving...",
        SaveState.Saved  => "Saved",
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
        OnPropertyChanged(nameof(HasPolicySettings));

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

        HasPendingChanges = false;
        _isLoading = false;
    }

    // ── Auto-Save ─────────────────────────────────────────────────

    private static readonly HashSet<string> _nonSettingProperties =
    [
        nameof(SaveStatus), nameof(SaveStatusMessage), nameof(HasPendingChanges),
        nameof(SaveError), nameof(HasSaveError), nameof(IsSaveBarVisible),
        nameof(IsSaveStatusVisible), nameof(IsSaveEnabled), nameof(HasExistingAuth),
        nameof(AuthPlaceholderText), nameof(NetworkTimeoutValue), nameof(VersionDisplay),
        nameof(IsManifestPreviewLoading), nameof(ManifestPreviewContent), nameof(ManifestPreviewError),
        nameof(HasManifestPreviewContent), nameof(HasManifestPreviewError),
        nameof(HasPolicySettings), nameof(Services), nameof(IsServicesLoading),
        "", // string.Empty from Load's bulk notify
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // A setting changed by the user: hold it until Save or Discard.
        if (_isLoading || _nonSettingProperties.Contains(e.PropertyName ?? ""))
            return;

        HasPendingChanges = true;
        if (SaveStatus == SaveState.Saved)
            SaveStatus = SaveState.Idle;
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

    private PrefsSettingsPayload BuildPayload() => new()
    {
        ManifestUrl = ManifestUrl,
        AuthorizationHeader = string.IsNullOrWhiteSpace(AuthorizationHeader) ? null : AuthorizationHeader,
        SilentMode = SilentMode,
        VerboseMode = VerboseMode,
        DryRun = DryRun,
        EnableDialog = EnableDialog,
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
