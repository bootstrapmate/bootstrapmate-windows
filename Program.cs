using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Win32;
using BootstrapMate.Core;

namespace BootstrapMate
{
    class Program
    {
        // Windows API calls for suppressing system sounds
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Beep(uint dwFreq, uint dwDuration);
        
        [DllImport("user32.dll")]
        static extern bool MessageBeep(uint uType);
        
        [DllImport("winmm.dll")]
        static extern int waveOutSetVolume(int hwo, uint dwVolume);
        
        [DllImport("winmm.dll")]
        static extern int waveOutGetVolume(int hwo, out uint dwVolume);
        
        // System sound suppression
        private static uint originalVolume = 0;
        private static bool soundsSuppressed = false;

        // Named pipe writer for GUI output streaming
        private static StreamWriter? _pipeWriter;
        
        private static string LogDirectory = @"C:\ProgramData\ManagedBootstrap\logs";
        private static string CacheDirectory = @"C:\ProgramData\ManagedBootstrap\cache";
        
        // Version in YYYY.MM.DD.HHMM format - injected at build time via MSBuild
        private static readonly string Version = GetBuildVersion();

        // Exit codes. Callers (Intune, scheduled tasks, wrapper scripts) judge a
        // run by these, so each distinct outcome needs its own value:
        //   0 success
        //   1 usage error, or the run could not fetch/process the manifest
        //   2 the run completed but one or more packages failed
        //   3 elevation is required and was not obtained - a configuration
        //     mistake, not a failed install attempt
        private const int ExitSuccess = 0;
        private const int ExitFailure = 1;
        private const int ExitElevationRequired = 3;

        // Host of the manifest URL actually used this run. The policy
        // AuthorizationHeader exists to authenticate against the manifest
        // server; it must be scoped to that host only. Sending it cross-host
        // both leaks the credential to whatever hosts package URLs point at
        // and breaks Azure blob storage, which returns 403 for public blobs
        // when a request carries an Authorization header it can't validate
        // (the fleet-visible symptom: every package "Download failed:
        // Forbidden" on devices that receive the header via CSP).
        private static string? _activeManifestHost;

        private static bool ShouldAttachAuthHeader(string requestUrl)
        {
            return _activeManifestHost != null
                && Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
                && string.Equals(uri.Host, _activeManifestHost, StringComparison.OrdinalIgnoreCase);
        }
        
        private static string GetBuildVersion()
        {
            // Get version from assembly metadata (injected by MSBuild at compile time)
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var attribute = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestamp");
            
            return attribute?.Value ?? "dev.build";
        }

        static string GetCacheDirectory()
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                return CacheDirectory;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not create cache directory {CacheDirectory}: {ex.Message}");
                Logger.Debug("Falling back to temp directory for cache");
                
                // Fallback to temp directory if we can't create the ProgramData cache
                string fallbackDir = Path.Combine(Path.GetTempPath(), "BootstrapMate");
                Directory.CreateDirectory(fallbackDir);
                return fallbackDir;
            }
        }



        /// <summary>
        /// Temporarily suppress system sounds to prevent alert sounds during silent installations
        /// </summary>
        static void SuppressSystemSounds()
        {
            try
            {
                // Get current system volume for restoration later
                waveOutGetVolume(0, out originalVolume);
                // Set system volume to 0 (mute system sounds)
                waveOutSetVolume(0, 0);
                soundsSuppressed = true;
                Logger.Debug("System sounds suppressed for silent installation");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not suppress system sounds: {ex.Message}");
                // Continue anyway - this is not critical
            }
        }
        
        /// <summary>
        /// Restore system sounds to original levels
        /// </summary>
        static void RestoreSystemSounds()
        {
            try
            {
                if (soundsSuppressed)
                {
                    // Restore original system volume
                    waveOutSetVolume(0, originalVolume);
                    soundsSuppressed = false;
                    Logger.Debug("System sounds restored to original levels");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not restore system sounds: {ex.Message}");
                // Continue anyway - this is not critical
            }
        }

        static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        static bool TryRestartAsAdministrator(string[] args)
        {
            try
            {
                // Check if we're in silent mode - if so, don't try to restart with GUI
                bool silentMode = args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
                
                if (silentMode)
                {
                    Logger.Error("Running in silent mode but not elevated - cannot show UAC prompt");
                    return false;
                }
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "managedbootstrapinstall.exe"),
                    Arguments = string.Join(" ", args),
                    UseShellExecute = true,
                    Verb = "runas",  // Request elevation
                    CreateNoWindow = true,  // Don't create console window
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Logger.Info("BootstrapMate requires administrator privileges. Requesting elevation...");
                Console.WriteLine("BootstrapMate requires administrator privileges. Requesting elevation...");
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    Logger.Info($"Elevated process started with PID: {process.Id}");
                    Console.WriteLine("Elevated process started. This instance will now exit.");
                    return true;
                }
                else
                {
                    Logger.Warning("Failed to start elevated process - user may have denied elevation");
                    Console.WriteLine("Failed to start elevated process. User may have denied elevation.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error attempting to restart as administrator: {ex.Message}");
                Console.WriteLine($"Error attempting to restart as administrator: {ex.Message}");
                return false;
            }
        }

        // Legacy WriteLog method for compatibility with StatusManager
        static void WriteLog(string message)
        {
            Logger.Debug(message);
        }

        // -V and --version print the version; lowercase -v is verbose. The two
        // short forms differ only by case, so this comparison must be ordinal.
        static bool IsVersionSwitch(string arg) =>
            arg.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-V", StringComparison.Ordinal);

        static int Main(string[] args)
        {
            // Handle version request immediately without admin check or verbose logging.
            // Only --version and -V mean version; lowercase -v is the verbose switch.
            // This is checked across every argument, not just args[0]: position must
            // never decide whether a switch prints a string or provisions the machine.
            // --last-run is read by a remediation script: one line, no elevation, no
            // session directory, and always exit 0 so the script decides the result.
            if (args.Any(arg => arg.Equals("--last-run", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine(LastRunFile.FormatLine(LastRunFile.Read()));
                return ExitSuccess;
            }

            if (args.Any(IsVersionSwitch))
            {
                Console.WriteLine(Version);
                return ExitSuccess;
            }

            // --status only reads HKLM\SOFTWARE\BootstrapMate and status.json, which any
            // user can read. Like --last-run it needs no elevation, opens no session and
            // never prompts, so a script, a remote shell or a monitoring agent can call it.
            if (args.Any(arg => arg.Equals("--status", StringComparison.OrdinalIgnoreCase)))
            {
                return ShowStatus();
            }

            // Check for silent mode - suppress all console output
            bool silentMode = args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            
            // Check for verbose mode
            bool verboseMode = args.Any(arg => arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || 
                                              arg.Equals("-v", StringComparison.Ordinal));
            
            // Policy or saved settings can turn either on; a CLI switch cannot turn them off.
            silentMode |= ConfigManager.Instance.Config.SilentMode;
            verboseMode |= ConfigManager.Instance.Config.VerboseMode;

            Logger.Initialize(LogDirectory, Version, verboseMode, silentMode);
            Logger.Debug("Main() called with arguments: " + string.Join(" ", args));
            foreach (var retired in ConfigManager.Instance.RetiredSettingsPresent)
            {
                Logger.Warning($"Setting {retired} is ignored: it was never implemented and has been removed");
            }
            
            // Check if running as administrator
            if (!IsRunningAsAdministrator())
            {
                if (!silentMode)
                {
                    // Immediate clear message without logger initialization noise
                    Console.WriteLine();
                    Console.WriteLine("ERROR: BootstrapMate must be run as Administrator");
                    Console.WriteLine();
                    Console.WriteLine("   BootstrapMate requires elevated privileges to:");
                    Console.WriteLine("   • Install packages to Program Files");
                    Console.WriteLine("   • Write to HKLM registry keys");

                    Console.WriteLine("   • Manage system components");
                    Console.WriteLine();
                    Console.WriteLine("   Please run BootstrapMate as Administrator, or use:");
                    Console.WriteLine($"   sudo {Path.GetFileName(Environment.ProcessPath ?? "managedbootstrapinstall.exe")} {string.Join(" ", args)}");
                    Console.WriteLine();
                }
                
                Logger.Info("BootstrapMate is not running as Administrator");

                // A caller with no console to answer from (a script, a remote shell, a
                // redirected stdin) would hang on the prompt below. Fail fast instead.
                if (!silentMode && (Console.IsInputRedirected || !Environment.UserInteractive))
                {
                    Logger.Error("Administrator privileges are required and there is no interactive console to ask. " +
                                 $"Re-run from an elevated context. Exiting with code {ExitElevationRequired}.");
                    return ExitElevationRequired;
                }

                if (!silentMode)
                {
                    // Ask user if they want to restart as admin
                    Console.Write("   Would you like to restart as Administrator? (y/n): ");
                    var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                    
                    if (response == "y" || response == "yes")
                    {
                        Console.WriteLine("   Attempting to restart with administrator privileges...");
                        
                        // Attempt to restart as administrator
                        if (TryRestartAsAdministrator(args))
                        {
                            Logger.Info("Successfully launched elevated process. Exiting current instance.");
                            return 0; // Success - elevated process will handle the work
                        }
                        else
                        {
                            Logger.Error("Failed to obtain administrator privileges. Cannot continue.");
                            Console.WriteLine("   ERROR: Failed to restart with administrator privileges.");
                            Console.WriteLine("   Please manually run as Administrator or use sudo.");
                            return ExitElevationRequired; // Elevation needed and not obtained
                        }
                    }
                    else
                    {
                        Logger.Error("User declined to restart as administrator. Cannot continue.");
                        Console.WriteLine("   Operation cancelled. BootstrapMate requires administrator privileges.");
                        return ExitElevationRequired; // Elevation needed and declined
                    }
                }
                else
                {
                    // In silent mode, just attempt to restart as administrator automatically
                    if (TryRestartAsAdministrator(args))
                    {
                        Logger.Info("Successfully launched elevated process in silent mode. Exiting current instance.");
                        return ExitSuccess; // Elevated process will handle the work
                    }
                    else
                    {
                        // Silent mode cannot show a UAC prompt, so this is not a failed
                        // install - nothing was attempted. Say so, and exit with a code
                        // the caller can tell apart from a run that tried and broke.
                        Logger.Error("BootstrapMate requires administrator privileges and --silent cannot prompt for elevation. " +
                                     "Nothing was installed. Re-run from an elevated context (SYSTEM, or an elevated shell). " +
                                     $"Exiting with code {ExitElevationRequired} (elevation required).");
                        return ExitElevationRequired;
                    }
                }
            }
            
            Logger.Debug("Running with administrator privileges");
            if (!silentMode)
            {
                Console.WriteLine("[+] Running with administrator privileges");
                Console.WriteLine();
            }

            // Single-instance guard. Two concurrent sessions (e.g. an Intune-triggered
            // run racing an interactive one) share the same cache directory and the
            // Windows Installer mutex, so they corrupt each other: 1618s, cache files
            // deleted mid-install, "file in use" failures. Serialize instead - the
            // second session waits for the first to finish, then runs normally.
            using var instanceMutex = new Mutex(initiallyOwned: false, @"Global\BootstrapMate.SingleInstance");
            bool ownsInstanceMutex = false;
            try
            {
                try { ownsInstanceMutex = instanceMutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { ownsInstanceMutex = true; }

                if (!ownsInstanceMutex)
                {
                    Logger.Info("Another BootstrapMate instance is already running - waiting for it to finish");
                    if (!silentMode)
                    {
                        Console.WriteLine("[i] Another BootstrapMate instance is already running - waiting for it to finish...");
                    }

                    try { ownsInstanceMutex = instanceMutex.WaitOne(TimeSpan.FromMinutes(30)); }
                    catch (AbandonedMutexException) { ownsInstanceMutex = true; }

                    if (!ownsInstanceMutex)
                    {
                        Logger.Error("Timed out after 30 minutes waiting for another BootstrapMate instance to finish");
                        return 1;
                    }

                    Logger.Info("Previous BootstrapMate instance finished - continuing");
                }

                return MainAsync(args).GetAwaiter().GetResult();
            }
            finally
            {
                if (ownsInstanceMutex) instanceMutex.ReleaseMutex();
            }
        }
        
        static async Task<int> MainAsync(string[] args)
        {
            // Every process BootstrapMate starts inherits this, so a preflight can tell
            // this build understands baseline mode before it returns that exit code.
            Environment.SetEnvironmentVariable(Preflight.BaselineExitCodeVariable,
                Preflight.BaselineExitCode.ToString());

            // Check for silent mode flag
            bool silentMode = args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            
            if (!silentMode)
            {
                Logger.WriteHeader($"BootstrapMate for Windows v{Version}");
                Console.WriteLine("MDM-agnostic bootstrapping tool for Windows");
                Console.WriteLine("Windows Admins Open Source 2025");
            }
            
            // Clean up old statuses (older than 24 hours) on startup
            try
            {
                StatusManager.CleanupOldStatuses(TimeSpan.FromHours(24));
                Logger.Debug("Cleaned up old installation statuses");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to cleanup old statuses: {ex.Message}");
            }
            
            // Clean up all cached packages on startup (cache only contains failed installations for inspection)
            try
            {
                CleanupOldCache(TimeSpan.Zero);
                Logger.Debug("Cleaned up cached files from previous runs (failed installations only)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to cleanup old cache files: {ex.Message}");
            }
            
            // Parse command line arguments
            bool forceDownload = false;
            bool noDialog = false;
            bool blurScreen = false;
            string dialogTitle = "Setting Up Your Device";
            string dialogMessage = "Please wait while we install required software...";
            string manifestUrl = "";
            string? pipeName = null;
            
            if (args.Length == 0)
            {
                // No args: try to load manifest URL from CSP policy or user settings
                var config = ConfigManager.Instance;
                var effectiveUrl = config.GetEffectiveManifestUrl();
                if (!string.IsNullOrEmpty(effectiveUrl))
                {
                    manifestUrl = effectiveUrl;
                    Logger.Info($"Settings loaded from: {config.ManifestUrlSource}");
                    if (!silentMode)
                        Console.WriteLine($"[i] Manifest URL loaded from {config.ManifestUrlSource}: {manifestUrl}");
                    
                    // Apply other settings from config
                    noDialog = config.Config.NoDialog;
                    blurScreen = config.Config.BlurScreen;
                    dialogTitle = config.Config.DialogTitle;
                    dialogMessage = config.Config.DialogMessage;
                }
                else
                {
                    if (!silentMode)
                    {
                        Console.WriteLine("Usage:");
                        Console.WriteLine("  managedbootstrapinstall.exe --url <manifest-url>");
                        Console.WriteLine("  managedbootstrapinstall.exe --help");
                        Console.WriteLine("  managedbootstrapinstall.exe --version");
                        Console.WriteLine("  managedbootstrapinstall.exe --status");
                        Console.WriteLine("  managedbootstrapinstall.exe --clear-cache");
                        Console.WriteLine("  managedbootstrapinstall.exe --reset-chocolatey");
                        Console.WriteLine("  managedbootstrapinstall.exe --url <manifest-url> --force");
                        Console.WriteLine("  managedbootstrapinstall.exe --url <manifest-url> --verbose");
                        Console.WriteLine("  managedbootstrapinstall.exe --url <manifest-url> --silent");
                        Console.WriteLine();
                        Console.WriteLine("Options:");
                        Console.WriteLine("  --url <url>     URL to the bootstrapmate.json manifest");
                        Console.WriteLine("  --headers <value>  Authorization header value for manifest/package downloads");
                        Console.WriteLine("  --force         (Deprecated - downloads are always fresh. Cache is for inspection only)");
                        Console.WriteLine("  --verbose, -v   Show detailed logging output");
                        Console.WriteLine("  --silent        Run completely silently (no console output)");
                        Console.WriteLine("  --no-dialog     Disable progress dialog (csharpdialog)");
                        Console.WriteLine("  --dialog-title  Custom title for progress dialog");
                        Console.WriteLine("  --dialog-message  Custom message for progress dialog");
                        Console.WriteLine("  --pipe <name>   Named pipe for GUI output streaming");
                        Console.WriteLine("  --save-settings Save GUI settings to registry");
                        Console.WriteLine("  --save-settings-file <path>  Save settings from JSON file to registry");
                        Console.WriteLine("  --help          Show this help message");
                        Console.WriteLine("  --version, -V   Show version information");
                        Console.WriteLine("  --status        Show current installation status");
                        Console.WriteLine("  --last-run      Print a one-line summary of the last run (always exits 0)");
                        Console.WriteLine("  --clear-status  Clear all installation status data");
                        Console.WriteLine("  --clear-cache   Clear all caches including failed installation files (BootstrapMate + Chocolatey)");
                        Console.WriteLine("  --reset-chocolatey  Complete Chocolatey reset (removes corrupted lib folder)");
                        Console.WriteLine();
                        Console.WriteLine("Accepted but NOT implemented (see issue #27) - passing these logs a warning:");
                        Console.WriteLine("  --follow-redirects  Ignored");
                        Console.WriteLine("  --reboot            Ignored - the machine is never restarted");
                        Console.WriteLine("  --dry-run           Refused - the run exits rather than installing for real");
                        Console.WriteLine();
                        Console.WriteLine("Exit codes:");
                        Console.WriteLine("  0  Success");
                        Console.WriteLine("  1  Usage error, or the manifest could not be fetched/processed");
                        Console.WriteLine("  2  Run completed but one or more packages failed");
                        Console.WriteLine("  3  Administrator privileges required and not obtained");
                    }
                    return ExitSuccess;
                }
            }
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--version":
                        // Also handled ahead of the elevation check in Main(); kept
                        // here so the switch table is honest about what it accepts.
                        Console.WriteLine(Version);
                        return ExitSuccess;

                    case "--help":
                    case "-h":
                        Console.WriteLine("BootstrapMate Help");
                        Console.WriteLine("========================");
                        Console.WriteLine();
                        Console.WriteLine("This tool downloads and processes a bootstrapmate.json manifest file");
                        Console.WriteLine("to automatically install packages during Windows OOBE or setup scenarios.");
                        Console.WriteLine();
                        Console.WriteLine("Usage Examples:");
                        Console.WriteLine("  managedbootstrapinstall.exe --url https://example.com/bootstrap/bootstrapmate.json");
                        Console.WriteLine();
                        Console.WriteLine("Features:");
                        Console.WriteLine("  - Supports multiple package types: MSI, EXE, PowerShell, Chocolatey (.nupkg), sbin-installer (.pkg)");
                        Console.WriteLine("  - Handles setupassistant (OOBE) and userland installation phases");
                        Console.WriteLine("  - Admin privilege escalation for elevated packages");
                        Console.WriteLine("  - Architecture-specific conditional installation");
                        Console.WriteLine("  - Registry-based status tracking for detection scripts");
                        Console.WriteLine("  - Primary installer: sbin-installer (lightweight, fast)");
                        Console.WriteLine();
                        Console.WriteLine("Exit codes:");
                        Console.WriteLine("  0  Success");
                        Console.WriteLine("  1  Usage error, or the manifest could not be fetched/processed");
                        Console.WriteLine("  2  Run completed but one or more packages failed");
                        Console.WriteLine("  3  Administrator privileges required and not obtained");
                        return ExitSuccess;

                    case "--status":
                        return ShowStatus();

                    case "--clear-status":
                        return ClearStatus();

                    case "--clear-cache":
                        return ClearCache();

                    case "--reset-chocolatey":
                        return ResetChocolatey(silentMode);

                    case "--force":
                        forceDownload = true;
                        break;

                    case "--verbose":
                    case "-v":
                        // Verbose mode is already handled in Main()
                        break;
                        
                    case "--silent":
                        // Silent mode is already handled in Main()
                        break;
                        
                    case "--url":
                        if (i + 1 < args.Length)
                        {
                            manifestUrl = args[i + 1];
                            i++; // Skip the next argument since we consumed it
                        }
                        else
                        {
                            Console.WriteLine("ERROR: --url requires a URL parameter");
                            return 1;
                        }
                        break;
                        
                    case "--no-dialog":
                        noDialog = true;
                        break;

                    case "--blur-screen":
                        blurScreen = true;
                        break;
                        
                    case "--dialog-title":
                        if (i + 1 < args.Length)
                        {
                            dialogTitle = args[i + 1];
                            i++;
                        }
                        break;
                        
                    case "--dialog-message":
                        if (i + 1 < args.Length)
                        {
                            dialogMessage = args[i + 1];
                            i++;
                        }
                        break;

                    case "--pipe":
                        if (i + 1 < args.Length)
                        {
                            pipeName = args[i + 1];
                            i++;
                        }
                        break;

                    case "--save-settings":
                        return SaveSettingsFromArgs(args);

                    case "--save-settings-file":
                        if (i + 1 < args.Length)
                            return SaveSettingsFromFile(args[++i]);
                        Console.WriteLine("ERROR: --save-settings-file requires a file path");
                        return ExitFailure;

                    case "--headers":
                        // The GUI passes the authorization header this way. Consume the
                        // value - leaving it in place made the header string itself get
                        // parsed as the next switch - and feed it through the same
                        // ConfigManager path the policy/registry header uses, so
                        // ShouldAttachAuthHeader() still governs which hosts see it.
                        if (i + 1 < args.Length)
                        {
                            ConfigManager.Instance.ApplyCliArguments(authorizationHeader: args[i + 1]);
                            Logger.Debug("Authorization header set from --headers");
                            i++;
                        }
                        else
                        {
                            Console.WriteLine("ERROR: --headers requires a header value");
                            Logger.Error("--headers requires a header value");
                            return ExitFailure;
                        }
                        break;

                    // The next three switches are accepted because the GUI emits them,
                    // but nothing in this CLI implements the behaviour behind them (see
                    // issue #27). Say so out loud rather than appearing to honour them.
                    case "--follow-redirects":
                        Logger.Warning("--follow-redirects is not implemented in the CLI and is being ignored; redirects are handled by the default HttpClient behaviour");
                        if (!silentMode)
                            Console.WriteLine("WARNING: --follow-redirects is not implemented and is being ignored.");
                        break;

                    case "--reboot":
                        Logger.Warning("--reboot is not implemented in the CLI and is being ignored; the machine will NOT be restarted when the run finishes");
                        if (!silentMode)
                            Console.WriteLine("WARNING: --reboot is not implemented and is being ignored. The machine will not be restarted.");
                        break;

                    case "--dry-run":
                        // A dry run has no implementation, so continuing would install
                        // for real - the exact opposite of what was asked. Refuse.
                        Logger.Error("--dry-run is not implemented in the CLI. Refusing to run, because continuing would perform a real installation.");
                        Console.WriteLine("ERROR: --dry-run is not implemented. Refusing to run - continuing would install for real.");
                        return ExitFailure;

                    default:
                        // No silent skipping. An unrecognised argument means the caller
                        // and this parser have drifted apart, which is how a GUI dry run
                        // came to perform a real installation.
                        Logger.Error($"Unrecognised argument: {args[i]}");
                        Console.WriteLine($"ERROR: Unrecognised argument: {args[i]}");
                        Console.WriteLine("Run with --help to see the supported options.");
                        return ExitFailure;
                }
            }

            // If no --url was provided via CLI, try ConfigManager (CSP policy / user settings)
            if (string.IsNullOrEmpty(manifestUrl))
            {
                var config = ConfigManager.Instance;
                config.ApplyCliArguments(noDialog: noDialog);
                var effectiveUrl = config.GetEffectiveManifestUrl();
                if (!string.IsNullOrEmpty(effectiveUrl))
                {
                    manifestUrl = effectiveUrl;
                    Logger.Info($"Settings loaded from: {config.ManifestUrlSource}");
                    if (!silentMode)
                        Console.WriteLine($"[i] Manifest URL loaded from {config.ManifestUrlSource}: {manifestUrl}");
                    
                    // Apply config-derived settings (CLI flags override)
                    if (!noDialog) noDialog = config.Config.NoDialog;
                    if (!blurScreen) blurScreen = config.Config.BlurScreen;
                    dialogTitle = config.Config.DialogTitle;
                    dialogMessage = config.Config.DialogMessage;
                }
            }

            // Connect named pipe for GUI streaming (if requested)
            if (!string.IsNullOrEmpty(pipeName))
            {
                try
                {
                    var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                    pipeClient.Connect(timeout: 5000);
                    _pipeWriter = new StreamWriter(pipeClient) { AutoFlush = true };
                    Logger.SetPipeWriter(_pipeWriter);
                    Logger.Debug($"Connected to GUI pipe: {pipeName}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not connect to GUI pipe: {ex.Message}");
                }
            }

            // DryRun from policy or saved settings is honoured the way --dry-run is: there
            // is no simulated install, so the only safe reading is to refuse to run.
            if (ConfigManager.Instance.Config.DryRun)
            {
                Logger.Error("DryRun is set in policy or settings. BootstrapMate has no simulated install, so it is refusing to run rather than install for real.");
                if (!silentMode)
                    Console.WriteLine("ERROR: DryRun is set. Refusing to run - continuing would install for real.");
                return ExitFailure;
            }

            // EnableDialog = false turns the dialog off just as NoDialog does.
            if (!ConfigManager.Instance.Config.EnableDialog)
            {
                noDialog = true;
            }

            // Process manifest if URL was provided
            if (!string.IsNullOrEmpty(manifestUrl))
            {
                return await ProcessManifest(manifestUrl, forceDownload, noDialog, blurScreen, dialogTitle, dialogMessage);
            }
            
            Console.WriteLine("ERROR: No manifest URL provided. Use --url <url> or configure via CSP policy / registry settings.");
            return 1;
        }

        /// <summary>
        /// Saves settings from a JSON file. Called by the GUI app via elevated process.
        /// </summary>
        private static int SaveSettingsFromFile(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var config = System.Text.Json.JsonSerializer.Deserialize<BootstrapMateConfig>(json);
                if (config is null)
                {
                    Logger.Error("Failed to deserialize settings file.");
                    return 1;
                }
                ConfigManager.SaveUserSettings(config);
                Logger.Info("Settings saved from file successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save settings from file: {ex.Message}");
                return 1;
            }
            finally
            {
                try { File.Delete(filePath); } catch { }
            }
        }

        /// <summary>
        /// Saves settings from CLI args to user registry. Called by the GUI app via elevated process.
        /// Expected format: --save-settings --url X --dialog-title Y ...
        /// </summary>
        private static int SaveSettingsFromArgs(string[] args)
        {
            var config = new BootstrapMateConfig();
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--url" when i + 1 < args.Length:
                        config.ManifestUrl = args[++i];
                        break;
                    case "--no-dialog":
                        config.NoDialog = true;
                        break;
                    case "--dialog-title" when i + 1 < args.Length:
                        config.DialogTitle = args[++i];
                        break;
                    case "--dialog-message" when i + 1 < args.Length:
                        config.DialogMessage = args[++i];
                        break;
                    case "--dialog-icon" when i + 1 < args.Length:
                        config.DialogIcon = args[++i];
                        break;
                    case "--silent":
                        config.SilentMode = true;
                        break;
                    case "--verbose":
                    case "-v":
                        config.VerboseMode = true;
                        break;
                    case "--force":
                        // Not persisted — runtime flag only
                        break;
                }
            }

            try
            {
                ConfigManager.SaveUserSettings(config);
                Logger.Info("Settings saved to registry successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save settings: {ex.Message}");
                return 1;
            }
        }
        
        static async Task<int> ProcessManifest(string manifestUrl, bool forceDownload = false, bool noDialog = false, bool blurScreen = false, string dialogTitle = "Setting Up Your Device", string dialogMessage = "Please wait while we install required software...")
        {
            // Captured before the try so both the success and failure paths can
            // report an accurate duration.
            DateTime runStartUtc = DateTime.UtcNow;
            try
            {
                // Clear cache if force download is requested
                if (forceDownload)
                {
                    Logger.Debug("Force download requested - aggressively clearing all caches");
                    Logger.Info("Force download requested - aggressively clearing all caches");
                    ClearAllCachesAggressive();
                }

                // Initialize status tracking
                StatusManager.Initialize(manifestUrl, Version);

                // Looked at before the preflight, which consumes it: the throttle only looks.
                bool forced = File.Exists(BaselineThrottle.ForceFilePath);

                RunReport.Start(Version);
                Logger.Debug($"Initialized status tracking with RunId: {StatusManager.GetCurrentRunId()}");

                Logger.Info($"Downloading manifest from: {manifestUrl}");

                // Record the manifest host so the policy Authorization header can be
                // scoped to it - packages hosted elsewhere (Azure blob) must never
                // receive it (see ShouldAttachAuthHeader).
                _activeManifestHost = Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri)
                    ? manifestUri.Host
                    : null;

                using var httpClient = new HttpClient { Timeout = NetworkTimeout };
                httpClient.DefaultRequestHeaders.Add("User-Agent", $"BootstrapMate/{Version}");
                var authHeader = ConfigManager.Instance.Config.AuthorizationHeader;
                if (!string.IsNullOrEmpty(authHeader))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
                    Logger.Debug("Authorization header attached from policy");
                }

                string manifestContent = await httpClient.GetStringAsync(manifestUrl);
                Logger.Debug("Manifest downloaded successfully");
                
                // Parse manifest (auto-detects JSON or YAML)
                using var doc = ManifestParser.Parse(manifestContent, manifestUrl);
                var root = doc.RootElement;
                
                // Count total packages for dialog progress
                int totalPackages = 0;
                if (root.TryGetProperty("setupassistant", out var setupCount))
                {
                    totalPackages += setupCount.GetArrayLength();
                }
                if (root.TryGetProperty("userland", out var userlandCount))
                {
                    totalPackages += userlandCount.GetArrayLength();
                }
                
                // Preflight runs before the dialog exists and before system sounds are
                // muted: only a machine that is actually being provisioned gets a
                // window, so a skipped or baseline run never puts one in front of a
                // working user.
                var decision = PreflightDecision.Provision;
                if (root.TryGetProperty("preflight", out var preflightItems) &&
                    preflightItems.ValueKind == JsonValueKind.Array &&
                    preflightItems.GetArrayLength() > 0)
                {
                    decision = await RunPreflightStage(preflightItems);
                }
                else
                {
                    StatusManager.SetPhaseStatus(InstallationPhase.Preflight, InstallationStage.Skipped);
                }
                RunReport.SetRunType(RunTypes.For(decision));

                if (decision == PreflightDecision.Skip)
                {
                    Logger.WriteCompletion("Preflight chose Skip - nothing to do on this machine");
                    StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Skipped);
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Skipped);
                    StatusManager.WriteSuccessfulCompletionRegistry();
                    await ReportManager.SendRunSummaryAsync(true, runStartUtc, Version, manifestUrl);
                    RunReport.Finish();
                    return ExitSuccess;
                }

                if (decision == PreflightDecision.Failed)
                {
                    // A failed preflight gates every later stage, as on macOS.
                    Logger.Error("Preflight failed - setupassistant and userland are skipped");
                    Logger.WriteCompletion("BootstrapMate stopped: preflight failed");
                    StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Failed, "Preflight failed", 1);
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Skipped);
                    await ReportManager.SendRunSummaryAsync(false, runStartUtc, Version, manifestUrl);
                    RunReport.Finish(RunStatuses.Failed);
                    return ExitFailure;
                }

                bool baseline = decision == PreflightDecision.Baseline;
                if (baseline)
                {
                    // Applied only once the preflight has chosen baseline, so provisioning is
                    // never limited. The manifest and preflight are small; a throttled run
                    // downloads no item.
                    var throttle = BaselineThrottle.Evaluate(BaselineThrottle.Read(), DateTimeOffset.Now,
                        ConfigManager.Instance.Config.BaselineMinIntervalHours, forced);
                    if (throttle.Skip)
                    {
                        Logger.WriteSkipped($"Baseline throttle: skipping this run: {throttle.Reason}");
                        RunReport.SetRunType(RunTypes.Skip);
                        StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Skipped);
                        StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Skipped);
                        Logger.WriteCompletion("Nothing downloaded: baseline throttled");
                        StatusManager.WriteSuccessfulCompletionRegistry();
                        await ReportManager.SendRunSummaryAsync(true, runStartUtc, Version, manifestUrl);
                        RunReport.Finish();
                        return ExitSuccess;
                    }
                    Logger.Info($"Baseline throttle: running ({throttle.Reason})");
                    Logger.Info("Baseline mode: refreshing setupassistant items without a dialog; userland is skipped.");
                }
                else
                {
                    // Suppress system sounds for silent installation experience
                    SuppressSystemSounds();
                }

                // Initialize dialog (gracefully degrades if not available)
                if (!noDialog && !baseline)
                {
                    DialogManager.Instance.Initialize(
                        dialogTitle,
                        dialogMessage,
                        totalPackages,
                        icon: ConfigManager.Instance.Config.DialogIcon,
                        fullScreen: blurScreen,
                        kioskMode: false
                    );
                    
                    // Add list items for all packages
                    if (root.TryGetProperty("setupassistant", out var setupPkgs))
                    {
                        foreach (var pkg in setupPkgs.EnumerateArray())
                        {
                            var name = pkg.GetProperty("name").GetString() ?? "Unknown";
                            DialogManager.Instance.AddListItem(name, DialogStatus.Pending);
                        }
                    }
                    if (root.TryGetProperty("userland", out var userlandPkgs))
                    {
                        foreach (var pkg in userlandPkgs.EnumerateArray())
                        {
                            var name = pkg.GetProperty("name").GetString() ?? "Unknown";
                            DialogManager.Instance.AddListItem(name, DialogStatus.Pending);
                        }
                    }
                }
                
                var failedPackages = new List<string>();

                // Process setupassistant packages first
                if (root.TryGetProperty("setupassistant", out var setupAssistant))
                {
                    StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Starting);
                    Logger.WriteSection("Processing Setup Assistant packages");
                    DialogManager.Instance.NotifyPhaseStarted("Setup Assistant");
                    StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Running);
                    
                    try
                    {
                        var failed = await ProcessPackages(setupAssistant, "setupassistant", forceDownload, baseline);
                        failedPackages.AddRange(failed);
                        if (failed.Count > 0)
                        {
                            // Completed-with-failures is still a failed phase. Reporting
                            // it as Completed is what let a device sit for weeks with
                            // every package refused and nothing anywhere saying so.
                            StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Failed,
                                $"{failed.Count} package(s) failed: {string.Join(", ", failed)}", 1);
                        }
                        else
                        {
                            StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Completed);
                        }
                        Logger.Debug("Setup Assistant packages completed successfully");
                    }
                    catch (Exception ex)
                    {
                        StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Failed, ex.Message, 1);
                        throw; // Re-throw to maintain existing error handling
                    }
                }
                else
                {
                    // Mark as skipped if no setupassistant packages
                    StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Skipped);
                    Logger.Debug("No Setup Assistant packages found - marked as skipped");
                }
                
                // Process userland packages. A baseline run lands on a machine someone
                // is using, so the user-facing stage never runs there.
                if (baseline)
                {
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Skipped);
                    Logger.WriteSkipped("Userland stage (baseline mode)");
                }
                else if (root.TryGetProperty("userland", out var userland))
                {
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Starting);
                    Logger.Debug("Processing Userland packages...");
                    Logger.WriteSection("Processing Userland packages");
                    DialogManager.Instance.NotifyPhaseStarted("Userland");
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Running);
                    
                    try
                    {
                        var failed = await ProcessPackages(userland, "userland", forceDownload);
                        failedPackages.AddRange(failed);
                        if (failed.Count > 0)
                        {
                            // Completed-with-failures is still a failed phase. Reporting
                            // it as Completed is what let a device sit for weeks with
                            // every package refused and nothing anywhere saying so.
                            StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Failed,
                                $"{failed.Count} package(s) failed: {string.Join(", ", failed)}", 1);
                        }
                        else
                        {
                            StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Completed);
                        }
                        Logger.Debug("Userland packages completed successfully");
                    }
                    catch (Exception ex)
                    {
                        StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Failed, ex.Message, 1);
                        throw; // Re-throw to maintain existing error handling
                    }
                }
                else
                {
                    // Mark as skipped if no userland packages
                    StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Skipped);
                    Logger.Debug("No Userland packages found - marked as skipped");
                }

                var succeeded = failedPackages.Count == 0;

                if (succeeded)
                {
                    Logger.Debug("BootstrapMate completed successfully!");
                    Logger.WriteCompletion("BootstrapMate completed successfully!");
                }
                else
                {
                    Logger.Error($"BootstrapMate completed with {failedPackages.Count} failed package(s): {string.Join(", ", failedPackages)}");
                    Logger.WriteCompletion($"BootstrapMate completed with {failedPackages.Count} failed package(s)");
                }
                
                // Mark dialog as complete and close it
                DialogManager.Instance.Complete("Setup Complete!");
                await Task.Delay(2000); // Give user time to see completion message
                DialogManager.Instance.Close();
                
                // Write successful completion to registry for Intune detection.
                // Only on a genuinely clean run: LastRunVersion is the detection
                // key, so stamping it after failures tells Intune the work is
                // done and stops it ever retrying the packages that failed.
                if (succeeded)
                {
                    StatusManager.WriteSuccessfulCompletionRegistry();
                }
                else
                {
                    Logger.Debug("Skipping LastRunVersion registry write - run had package failures, so Intune should retry");
                }
                
                // Restore system sounds before completion
                RestoreSystemSounds();

                // Post a vendor-neutral run summary to the optional reporting endpoint.
                await ReportManager.SendRunSummaryAsync(succeeded, runStartUtc, Version, manifestUrl);

                // Close out the session log so session.json records how the run ended
                // rather than staying at "running" forever.
                RunReport.Finish();

                // Exit non-zero when packages failed. The registry already records the
                // failure; without this the layer above - an Intune Win32 app result, a
                // wrapping script, the scheduled task's Last Run Result - still reads green.
                if (!succeeded)
                {
                    Logger.Warning("Run completed with package failures; exiting 2");
                    return 2;
                }

                return 0;
            }
            catch (Exception ex)
            {
                // Ensure sounds are restored even on error
                RestoreSystemSounds();
                
                // Close dialog on error
                try
                {
                    DialogManager.Instance.UpdateProgressText("Setup failed - please contact IT support");
                    DialogManager.Instance.TerminateDialog();
                }
                catch { }
                
                Logger.Error($"Error processing manifest: {ex.Message}");
                Logger.Debug($"Stack trace: {ex.StackTrace}");
                Logger.WriteError($"Error processing manifest: {ex.Message}");
                
                // Ensure status is marked as failed on any unhandled exception
                try
                {
                    // Try to determine which phase failed based on current state
                    var setupStatus = StatusManager.GetPhaseStatus(InstallationPhase.SetupAssistant);
                    var userlandStatus = StatusManager.GetPhaseStatus(InstallationPhase.Userland);
                    
                    if (setupStatus.Stage == InstallationStage.Running)
                    {
                        StatusManager.SetPhaseStatus(InstallationPhase.SetupAssistant, InstallationStage.Failed, ex.Message, 1);
                    }
                    else if (userlandStatus.Stage == InstallationStage.Running)
                    {
                        StatusManager.SetPhaseStatus(InstallationPhase.Userland, InstallationStage.Failed, ex.Message, 1);
                    }
                }
                catch
                {
                    // Don't let status update failures mask the original error
                }

                // Report the failed run too, so the fleet view reflects failures.
                await ReportManager.SendRunSummaryAsync(false, runStartUtc, Version, manifestUrl);

                RunReport.Finish(RunStatuses.Failed);

                return 1;
            }
        }
        
        static int GetPackageProcessingPriority(JsonElement package)
        {
            try
            {
                var type = package.GetProperty("type").GetString()?.ToLowerInvariant() ?? "";
                var name = package.GetProperty("name").GetString()?.ToLowerInvariant() ?? "";
                
                // Priority levels (lower number = higher priority = processed first):
                
                // Priority 1: Essential system installers that other packages might depend on
                if (type == "msi" || type == "exe")
                {
                    return 1;
                }
                
                // Priority 2: Chocolatey packages (.nupkg) - these install Chocolatey if needed
                if (type == "nupkg")
                {
                    return 2;
                }
                
                // Priority 3: General PowerShell scripts (but not cleanup scripts)
                if (type == "powershell" || type == "ps1")
                {
                    // Check if this is a cleanup/maintenance script - these should run last
                    if (name.Contains("cleanup") || name.Contains("clean") || 
                        name.Contains("wipe") || name.Contains("remove") || 
                        name.Contains("delete") || name.Contains("purge") ||
                        name.Contains("nuclear") || name.Contains("maintenance"))
                    {
                        return 10; // Run cleanup scripts last
                    }
                    
                    return 3; // Regular PowerShell scripts
                }
                
                // Priority 5: Unknown or other types
                return 5;
            }
            catch
            {
                // If we can't determine priority, use default middle priority
                return 5;
            }
        }
        
        /// <summary>
        /// Installs every package for a phase and returns the ones that failed.
        ///
        /// Failures are collected rather than thrown: one bad package must not
        /// abort provisioning. They are returned rather than only logged
        /// because the caller marked the phase Completed regardless, so a run
        /// in which every MSI was refused still recorded a clean success and
        /// Intune never retried it.
        /// </summary>
        static async Task<List<string>> ProcessPackages(JsonElement packages, string phase, bool forceDownload = false, bool baseline = false)
        {
            var failures = new List<string>();
            Logger.Debug($"Processing packages for phase: {phase}");

            // Convert JsonElement array to list for sorting. Items marked
            // "baseline": false are left out of baseline runs.
            var packageList = new List<JsonElement>();
            foreach (var package in packages.EnumerateArray())
            {
                if (baseline && !Preflight.RunsInBaseline(package))
                {
                    Logger.WriteSkipped($"{PackageLabel(package)} (excluded from baseline)");
                    RunReport.Item(PackageLabel(package), phase, ItemResults.Skipped);
                    continue;
                }
                packageList.Add(package);
            }
            
            // Reorder packages to prevent race conditions:
            // 1. Install packages that might install tools (nupkg, msi, exe) first
            // 2. Run cleanup/maintenance scripts last to avoid breaking tools needed by other packages
            var sortedPackages = packageList.OrderBy(pkg => GetPackageProcessingPriority(pkg)).ToList();
            
            // Log reordering information if packages were reordered
            if (!packageList.SequenceEqual(sortedPackages))
            {
                Logger.Info("Optimizing package installation order to prevent dependency conflicts");
                Logger.Debug("Package processing order optimized to prevent dependency conflicts:");
                for (int i = 0; i < sortedPackages.Count; i++)
                {
                    var pkg = sortedPackages[i];
                    var name = pkg.GetProperty("name").GetString() ?? "Unknown";
                    var type = pkg.GetProperty("type").GetString() ?? "";
                    Logger.Debug($"  {i + 1}. {name} (Type: {type})");
                }
                Logger.Info("This ensures cleanup scripts run after packages that might need the tools being cleaned");
            }
            
            foreach (var package in sortedPackages)
            {
                string displayName = "Unknown Package"; // Default value for error handling
                try
                {
                    displayName = package.GetProperty("name").GetString() ?? "Unknown";
                    var url = package.GetProperty("url").GetString() ?? "";
                    var fileName = package.GetProperty("file").GetString() ?? "";
                    var type = package.GetProperty("type").GetString() ?? "";
                    
                    Logger.Debug($"Processing package: {displayName} (Type: {type}, File: {fileName})");
                    Logger.WriteProgress("Processing", displayName);
                    
                    if (SkipForArchitecture(package, displayName))
                    {
                        DialogManager.Instance.NotifyPackageSkipped(displayName, "Architecture mismatch");
                        RunReport.Item(displayName, phase, ItemResults.Skipped);
                        continue;
                    }

                    if (!await DownloadAndInstallPackage(displayName, url, fileName, type, package, forceDownload, baseline))
                    {
                        DialogManager.Instance.NotifyPackageSkipped(displayName, "Already installed");
                        RunReport.Item(displayName, phase, ItemResults.Skipped);
                        continue;
                    }
                    Logger.Debug($"Successfully completed package: {displayName}");
                    Logger.WriteSuccess($"{displayName} installed successfully");
                    DialogManager.Instance.NotifyPackageSuccess(displayName);
                    RunReport.Item(displayName, phase, ItemResults.Installed);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to install package {displayName}: {ex.Message}");
                    Logger.WriteError($"Failed to install package {displayName}: {ex.Message}");
                    DialogManager.Instance.NotifyPackageFailure(displayName, "Failed");
                    failures.Add(displayName);
                    RunReport.Item(displayName, phase, ItemResults.Failed, ex.Message);
                    // Continue with next package instead of stopping entire process
                    // Note: We don't re-throw because we want to continue with other packages
                }
            }

            if (failures.Count > 0)
            {
                Logger.Error($"{phase}: {failures.Count} of {sortedPackages.Count} package(s) failed: {string.Join(", ", failures)}");
            }

            return failures;
        }
        
        /// <summary>
        /// True when the item's "condition" names an architecture other than this machine's.
        /// </summary>
        static bool SkipForArchitecture(JsonElement package, string displayName)
        {
            if (!package.TryGetProperty("condition", out var condition)) return false;

            var conditionStr = condition.GetString() ?? "";
            Logger.Debug($"Checking condition: {conditionStr}");

            // Get actual OS architecture - use OSArchitecture (not ProcessArchitecture) so that
            // the x64 binary running under ARM64 emulation still correctly detects ARM64.
            string actualArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant();
            Logger.Debug($"Detected OS architecture: {actualArchitecture}");

            // Skip x64 packages on non-x64 systems
            // Note: RuntimeInformation reports "X64" for AMD64/Intel 64-bit, "ARM64" for ARM64
            if (conditionStr.Contains("architecture_x64") && actualArchitecture != "X64")
            {
                Logger.Debug($"Skipping {displayName} - x64 condition not met on {actualArchitecture} architecture");
                Logger.WriteSkipped($"Skipping - x64 condition not met on {actualArchitecture}");
                return true;
            }

            // Skip ARM64 packages on non-ARM64 systems
            if (conditionStr.Contains("architecture_arm64") && actualArchitecture != "ARM64")
            {
                Logger.Debug($"Skipping {displayName} - ARM64 condition not met on {actualArchitecture} architecture");
                Logger.WriteSkipped($"Skipping - ARM64 condition not met on {actualArchitecture}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Runs the manifest's "preflight" scripts in manifest order, ahead of every other
        /// stage and of the type-based reordering the later stages use. The first script
        /// that returns Skip, Baseline or Failed decides the run; Provision moves on to the
        /// next script, and Provision is the answer when every script returns it.
        /// </summary>
        static async Task<PreflightDecision> RunPreflightStage(JsonElement items)
        {
            Logger.WriteSection("Preflight Stage");
            StatusManager.SetPhaseStatus(InstallationPhase.Preflight, InstallationStage.Running);

            var (decision, exitCode, error) = await RunPreflightScripts(items);

            // The Preflight record carries the deciding script's own exit code, so a
            // reader can see which mode was chosen; Failed records it as a failure.
            if (decision == PreflightDecision.Failed)
                StatusManager.SetPhaseStatus(InstallationPhase.Preflight, InstallationStage.Failed, error, exitCode == 0 ? 1 : exitCode);
            else
                StatusManager.SetPhaseStatus(InstallationPhase.Preflight, InstallationStage.Completed, "", exitCode);

            return decision;
        }

        static async Task<(PreflightDecision Decision, int ExitCode, string Error)> RunPreflightScripts(JsonElement items)
        {
            int lastExitCode = 1;
            foreach (var item in items.EnumerateArray())
            {
                var displayName = PackageLabel(item);
                var type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                // Preflight runs PowerShell scripts only. Name anything else rather than
                // dropping it silently, so a mistyped manifest is visible in the log.
                if (!type.Equals("ps1", StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning($"Preflight item {displayName} has type '{type}'; only ps1 runs in preflight - ignored");
                    continue;
                }

                if (SkipForArchitecture(item, displayName)) continue;

                Logger.WriteProgress("Running preflight script", displayName);
                string localPath;
                try
                {
                    var url = item.GetProperty("url").GetString() ?? "";
                    var fileName = item.GetProperty("file").GetString() ?? "";
                    localPath = Path.Combine(GetCacheDirectory(), fileName);
                    await DownloadFile(displayName, url, localPath);
                    VerifyPayloadHash(displayName, item, InstallLedger.ComputeSha256(localPath));
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to download preflight script {displayName}: {ex.Message}");
                    return (PreflightDecision.Failed, 1, $"Download failed: {ex.Message}");
                }

                int exitCode;
                try
                {
                    exitCode = await RunPowerShellScriptForExitCode(localPath, item);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Preflight script {displayName} could not run: {ex.Message}");
                    return (PreflightDecision.Failed, 1, ex.Message);
                }
                finally
                {
                    try { File.Delete(localPath); } catch { }
                }

                switch (Preflight.Decide(exitCode))
                {
                    case PreflightDecision.Skip:
                        Logger.WriteSuccess($"Preflight script {displayName} exited 0 - skipping bootstrap");
                        return (PreflightDecision.Skip, exitCode, "");
                    case PreflightDecision.Baseline:
                        Logger.WriteSuccess($"Preflight script {displayName} exited {exitCode} - running in baseline mode");
                        return (PreflightDecision.Baseline, exitCode, "");
                    case PreflightDecision.Provision:
                        Logger.Info($"Preflight script {displayName} exited {exitCode} - continuing with bootstrap");
                        lastExitCode = exitCode;
                        break;
                    default:
                        Logger.Error($"Preflight script {displayName} failed with exit code {exitCode}");
                        return (PreflightDecision.Failed, exitCode, $"Exit code: {exitCode}");
                }
            }

            return (PreflightDecision.Provision, lastExitCode, "");
        }

        const int DownloadAttempts = 3;

        // No bytes for this long means the transfer is dead, however large the file.
        // Set by the NetworkTimeout setting (default 120 seconds).
        static TimeSpan DownloadStallTimeout => NetworkTimeout;

        /// <summary>The NetworkTimeout setting, clamped to the ADMX's 10-600 second range.</summary>
        static TimeSpan NetworkTimeout =>
            TimeSpan.FromSeconds(Math.Clamp(ConfigManager.Instance.Config.NetworkTimeout, 10, 600));

        // Ceiling for one attempt, so a trickle that never quite stalls still ends.
        static readonly TimeSpan DownloadAttemptCap = TimeSpan.FromMinutes(30);

        sealed class DownloadStalledException : Exception
        {
            public DownloadStalledException(string message) : base(message) { }
        }

        /// <summary>
        /// One attempt: streams the body to disk, failing when no bytes arrive for
        /// <see cref="DownloadStallTimeout"/> or the attempt exceeds <see cref="DownloadAttemptCap"/>.
        /// </summary>
        static async Task DownloadOnce(HttpClient httpClient, string url, string localPath)
        {
            using var overall = new CancellationTokenSource(DownloadAttemptCap);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            stall.CancelAfter(DownloadStallTimeout);

            try
            {
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Download failed: {response.StatusCode}", null, response.StatusCode);
                }

                await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
                // Ensure the file stream is completely closed before returning
                await using var fileStream = File.Create(localPath);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, stall.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    stall.CancelAfter(DownloadStallTimeout);
                }
                await fileStream.FlushAsync();
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                throw new DownloadStalledException($"Download did not finish within {DownloadAttemptCap.TotalMinutes:F0} minutes");
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested)
            {
                throw new DownloadStalledException($"Download stalled: no data for {DownloadStallTimeout.TotalSeconds:F0} seconds");
            }
        }

        /// <summary>
        /// Network trouble and server-side errors are worth another attempt; a 4xx is not.
        /// </summary>
        static bool IsTransientDownloadFailure(Exception ex) => ex switch
        {
            DownloadStalledException => true,
            HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code == System.Net.HttpStatusCode.RequestTimeout,
            HttpRequestException => true,
            IOException => true,
            _ => false
        };

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="localPath"/>, replacing any
        /// cached copy, with the policy Authorization header scoped to the manifest host.
        /// </summary>
        static async Task DownloadFile(string displayName, string url, string localPath)
        {
            // Always download fresh files - cache is only for inspection/debugging
            // Delete existing cached file if present to ensure fresh download
            if (File.Exists(localPath))
            {
                try
                {
                    File.Delete(localPath);
                    Logger.Debug($"Deleted old cached file: {localPath}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not delete old cached file {localPath}: {ex.Message}");
                }
            }

            Logger.Debug($"Downloading {displayName} from: {url}");
            Logger.WriteSubProgress("Downloading from", url);

            // HttpClient's default 100-second Timeout covers the whole body, so a large
            // MSI on a slow link was cancelled while it was still arriving. Bound the
            // download by a stall timer and an overall cap instead, and retry a
            // transient failure.
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"BootstrapMate/{Version}");
            var authHeader = ConfigManager.Instance.Config.AuthorizationHeader;
            if (!string.IsNullOrEmpty(authHeader))
            {
                // Scope the policy credential to the manifest host. Azure blob
                // storage 403s public-blob requests carrying a foreign
                // Authorization header, and third-party hosts must not see the
                // org's token at all.
                if (ShouldAttachAuthHeader(url))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
                else
                    Logger.Debug($"Authorization header withheld for cross-host download: {url}");
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadOnce(httpClient, url, localPath);
                    break;
                }
                catch (Exception ex) when (attempt < DownloadAttempts && IsTransientDownloadFailure(ex))
                {
                    var delay = TimeSpan.FromSeconds(10 * attempt);
                    Logger.Warning($"Download of {displayName} failed (attempt {attempt}/{DownloadAttempts}): {ex.Message} - retrying in {delay.TotalSeconds:F0}s");
                    try { File.Delete(localPath); } catch { }
                    await Task.Delay(delay);
                }
            }

            // Add a small delay to ensure file handle is released
            await Task.Delay(100);

            var fileInfo = new FileInfo(localPath);
            var sizeText = fileInfo.Length < 1024 * 1024
                ? $"{fileInfo.Length / 1024.0:F1} KB"
                : $"{fileInfo.Length / 1024.0 / 1024:F1} MB";
            Logger.Debug($"Downloaded {displayName} to: {localPath} (Size: {sizeText})");
            Logger.WriteSubProgress("Downloaded", sizeText);
        }

        /// <summary>
        /// The same checks as <see cref="BaselineSkipReason"/>, from manifest fields only:
        /// the item's <c>hash</c> is in the install ledger, or an MSI item's
        /// <c>productCode</c>/<c>upgradeCode</c> is installed at its <c>version</c> or newer.
        /// Null when the manifest does not say enough, and the file is downloaded and
        /// checked after.
        /// </summary>
        static string? BaselineSkipReasonBeforeDownload(JsonElement packageInfo, string type)
        {
            string? Field(string name) =>
                packageInfo.ValueKind == JsonValueKind.Object &&
                packageInfo.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;

            var hash = Field("hash");
            if (!string.IsNullOrWhiteSpace(hash))
            {
                var bare = hash.Trim();
                if (bare.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) bare = bare[7..];
                if (new InstallLedger().Contains(bare))
                    return "this file was already installed by BootstrapMate";
            }

            var version = Field("version");
            var productCode = Field("productCode");
            var upgradeCode = Field("upgradeCode");
            if (type.Equals("msi", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(version) &&
                (!string.IsNullOrWhiteSpace(productCode) || !string.IsNullOrWhiteSpace(upgradeCode)))
            {
                var product = new MsiProduct.Info(productCode ?? "", version!, upgradeCode, PackageLabel(packageInfo));
                if (MsiProduct.IsInstalledAtOrAbove(product, out var installed))
                    return $"version {installed} is installed (manifest version {version})";
            }

            return null;
        }

        /// <summary>
        /// Why a baseline run can leave this file alone, or null when it must install it:
        /// the same file is in the install ledger, or its MSI product is already
        /// installed at this version or newer.
        /// </summary>
        static string? BaselineSkipReason(string localPath, string fileHash, string type)
        {
            if (new InstallLedger().Contains(fileHash))
                return "this file was already installed by BootstrapMate";

            if (type.Equals("msi", StringComparison.OrdinalIgnoreCase))
            {
                var product = MsiProduct.Read(localPath);
                if (product is not null && MsiProduct.IsInstalledAtOrAbove(product, out var installed))
                    return $"version {installed} is installed (package is {product.ProductVersion})";
            }

            return null;
        }

        /// <summary>
        /// Downloads and installs one item. Returns false when a baseline run found it
        /// already in place and left it alone.
        /// </summary>
        static async Task<bool> DownloadAndInstallPackage(string displayName, string url, string fileName, string type, JsonElement packageInfo, bool forceDownload = false, bool baseline = false)
        {
            // Create cache download directory (keeps failed installations for inspection)
            string cacheDir = GetCacheDirectory();
            string localPath = Path.Combine(cacheDir, fileName);
            
            try
            {
                // Baseline: decide from the manifest alone when possible, so an item that is
                // already in place costs no download.
                if (baseline && BaselineSkipReasonBeforeDownload(packageInfo, type) is { } earlySkip)
                {
                    Logger.WriteSkipped($"{displayName} - {earlySkip} (not downloaded)");
                    return false;
                }

                DialogManager.Instance.NotifyDownloadStarted(displayName);
                await DownloadFile(displayName, url, localPath);

                var fileHash = InstallLedger.ComputeSha256(localPath);
                VerifyPayloadHash(displayName, packageInfo, fileHash);

                // Baseline repeats on a machine in use: never reinstall what is already there.
                if (baseline && BaselineSkipReason(localPath, fileHash, type) is { } skipReason)
                {
                    Logger.WriteSkipped($"{displayName} - {skipReason}");
                    try { File.Delete(localPath); } catch { }
                    return false;
                }

                // Install based on type
                Logger.Debug($"Installing {displayName} using {type} installer...");
                DialogManager.Instance.NotifyInstallStarted(displayName);
                await InstallPackage(localPath, type, packageInfo);
                
                Logger.Debug($"Successfully installed: {displayName}");
                new InstallLedger().Record(fileHash, displayName);

                // Delete the cached file after successful installation
                try
                {
                    if (File.Exists(localPath))
                    {
                        File.Delete(localPath);
                        Logger.Debug($"Deleted cached file after successful installation: {localPath}");
                    }
                }
                catch (Exception deleteEx)
                {
                    Logger.Warning($"Could not delete cached file after successful installation: {deleteEx.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install {displayName}: {ex.Message}");
                Logger.Warning($"Keeping cached file for inspection: {localPath}");
                // Re-throw the exception so the caller knows the installation failed
                throw;
            }
        }
        
        static async Task InstallPackage(string filePath, string type, JsonElement packageInfo)
        {
            Logger.Debug($"Installing package: {filePath} (Type: {type})");

            // Provenance gate for binary installers that run elevated. The download
            // only proves where the bytes came from, not who produced them; verify
            // the Authenticode signature before executing as an elevated process.
            if (!VerifyInstallerSignature(filePath, type, packageInfo))
            {
                throw new Exception($"Refusing to install {Path.GetFileName(filePath)}: signature verification failed");
            }

            switch (type.ToLowerInvariant())
            {
                case "powershell":
                case "ps1":
                    await RunPowerShellScript(filePath, packageInfo);
                    break;
                    
                case "msi":
                    // Cimian-built MSIs: prefer sbin-installer (handles embedded scripts natively)
                    // Third-party MSIs: always use msiexec.exe directly
                    // (see InstallerRouting for why arguments and the sbin-installer
                    // package itself always go to msiexec).
                    bool isCimianMsi = IsCimianBuiltMsi(filePath);
                    bool sbinAvailable = isCimianMsi && IsSbinInstallerAvailable();
                    var route = InstallerRouting.Choose(isCimianMsi, sbinAvailable, filePath,
                        PackageLabel(packageInfo), GetArguments(packageInfo).Count);

                    if (route == MsiInstaller.SbinInstaller)
                    {
                        Logger.Info($"Using sbin-installer for Cimian MSI: {Path.GetFileName(filePath)}");
                        await RunSbinInstall(filePath, packageInfo);
                    }
                    else
                    {
                        if (isCimianMsi)
                            Logger.Debug($"Using msiexec for Cimian MSI {Path.GetFileName(filePath)}: " +
                                (!sbinAvailable ? "sbin-installer not available"
                                 : InstallerRouting.IsSbinInstallerPackage(filePath, PackageLabel(packageInfo)) ? "it is sbin-installer itself"
                                 : "the item carries msiexec arguments"));
                        await RunMsiInstaller(filePath, packageInfo);
                    }
                    break;
                    
                case "exe":
                    await RunExecutable(filePath, packageInfo);
                    break;
                    
                case "nupkg":
                    // Try sbin-installer first, fallback to Chocolatey
                    if (IsSbinInstallerAvailable())
                    {
                        await RunSbinInstall(filePath, packageInfo);
                    }
                    else
                    {
                        Logger.WriteSubProgress("sbin-installer not found", "Using Chocolatey fallback");
                        await RunChocolateyInstall(filePath, packageInfo);
                    }
                    break;
                    
                // TODO(pkg-sunset): Remove .pkg installation case
                case "pkg":
                    // pkg format is native to sbin-installer
                    if (!IsSbinInstallerAvailable())
                    {
                        throw new Exception("sbin-installer is required for .pkg packages but was not found. Please install sbin-installer first.");
                    }
                    Logger.WriteSubProgress("Processing .pkg with sbin-installer");
                    await RunSbinInstall(filePath, packageInfo);
                    break;
                    
                default:
                    Logger.Warning($"Unknown package type: {type}");
                    Logger.WriteWarning($"Unknown package type: {type}");
                    throw new Exception($"Unsupported package type: {type}. Supported types are: msi, exe, ps1, nupkg, pkg");
            }
        }

        /// <summary>
        /// Verify the Authenticode signature of a binary installer (msi/exe) before
        /// it runs elevated. Returns true when the install may proceed.
        ///
        /// Only msi/exe are gated — those are PE/MSI files Authenticode can validate.
        /// Per-item manifest fields (expectedPublisher, allowUnsigned) override the
        /// global managed config.
        /// </summary>
        static bool VerifyInstallerSignature(string filePath, string type, JsonElement packageInfo)
        {
            var config = ConfigManager.Instance.Config;
            if (!config.VerifyPackageSignatures)
                return true;

            string normalizedType = type.ToLowerInvariant();
            if (normalizedType != "msi" && normalizedType != "exe")
                return true; // nupkg/pkg/ps1 are not Authenticode-gated here

            string? expectedPublisher = config.ExpectedPublisher;
            if (packageInfo.TryGetProperty("expectedPublisher", out var pubProp) &&
                pubProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(pubProp.GetString()))
            {
                expectedPublisher = pubProp.GetString();
            }

            bool allowUnsigned = config.AllowUnsigned;
            if (packageInfo.TryGetProperty("allowUnsigned", out var allowProp) &&
                (allowProp.ValueKind == JsonValueKind.True || allowProp.ValueKind == JsonValueKind.False))
            {
                allowUnsigned = allowProp.GetBoolean();
            }

            var result = SignatureVerifier.VerifyFile(filePath, expectedPublisher);
            var (decision, reason) = SignatureVerifier.Decide(result, allowUnsigned);

            if (decision == SignatureVerifier.Decision.Allow)
            {
                Logger.Debug($"Signature check passed for {Path.GetFileName(filePath)}: {reason}");
                return true;
            }

            Logger.Error($"Signature check failed for {Path.GetFileName(filePath)}: {reason}");
            Logger.WriteWarning($"Refusing to install {Path.GetFileName(filePath)} — {reason}");
            return false;
        }
        
        static string PackageLabel(JsonElement packageInfo)
        {
            if (packageInfo.ValueKind == JsonValueKind.Object &&
                packageInfo.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                var name = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            return "package";
        }

        static async Task RunPowerShellScript(string scriptPath, JsonElement packageInfo)
        {
            int exitCode = await RunPowerShellScriptForExitCode(scriptPath, packageInfo);
            if (exitCode != 0)
            {
                throw new Exception($"PowerShell script failed with exit code: {exitCode}");
            }
        }

        static async Task<int> RunPowerShellScriptForExitCode(string scriptPath, JsonElement packageInfo)
        {
            var args = GetArguments(packageInfo);
            string arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\" {string.Join(" ", args)}";
            
            // Since BootstrapMate is already running as admin, all PowerShell scripts should inherit admin privileges
            // This ensures chocolatey and other system installers work properly
            bool needsElevation = true; // Always run elevated since we're in an admin context
            
            WriteLog($"Running PowerShell script: {scriptPath}");
            WriteLog($"Arguments: {arguments}");
            WriteLog($"Elevated: {needsElevation}");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false, // Use CreateProcess to inherit admin privileges
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            Console.WriteLine($"     [>] Running PowerShell: {arguments}");
            
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                // Capture output for debugging
                if (startInfo.RedirectStandardOutput)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Logger.WriteCapturedOutput(PackageLabel(packageInfo), output);
                    }
                }
                
                if (startInfo.RedirectStandardError)
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Logger.WriteCapturedOutput(PackageLabel(packageInfo), error, isError: true);
                    }
                }
                
                WriteLog($"PowerShell script completed with exit code: {process.ExitCode}");
                return process.ExitCode;
            }

            throw new Exception($"Could not start powershell.exe for {PackageLabel(packageInfo)}");
        }
        
        static bool RequiresElevation(string scriptPath, JsonElement packageInfo)
        {
            // Get the script filename to check for known patterns
            string scriptFileName = Path.GetFileName(scriptPath).ToLowerInvariant();
            
            // Get package name/ID for specific package checks
            string packageName = "";
            if (packageInfo.TryGetProperty("name", out var nameProp))
            {
                packageName = nameProp.GetString()?.ToLowerInvariant() ?? "";
            }
            
            string packageId = "";
            if (packageInfo.TryGetProperty("packageid", out var idProp))
            {
                packageId = idProp.GetString()?.ToLowerInvariant() ?? "";
            }
            
            // Scripts that definitely need elevation
            if (scriptFileName.Contains("chocolatey") || 
                scriptFileName.Contains("install-chocolatey") ||
                packageName.Contains("chocolatey") ||
                packageId.Contains("chocolatey"))
            {
                return true;
            }
            
            // Any script that installs system-wide components needs elevation
            if (scriptFileName.Contains("install") && 
                (scriptFileName.Contains("system") || scriptFileName.Contains("global")))
            {
                return true;
            }
            
            // Package manager installers typically need elevation
            if (packageName.Contains("package manager") || 
                packageId.Contains("package-manager"))
            {
                return true;
            }
            
            return false;
        }
        
        static async Task RunMsiInstaller(string msiPath, JsonElement packageInfo)
        {
            var args = GetArguments(packageInfo);
            string arguments = $"/i \"{msiPath}\" /qn /norestart {string.Join(" ", args)}";
            
            // Detect if this is a critical sbin-installer package
            bool isSbinInstaller = false;
            string packageName = "";
            if (packageInfo.TryGetProperty("name", out var nameProp))
            {
                packageName = nameProp.GetString() ?? "";
                isSbinInstaller = packageName.Contains("System Binary Installer", StringComparison.OrdinalIgnoreCase) ||
                                 packageName.Contains("sbin-installer", StringComparison.OrdinalIgnoreCase) ||
                                 Path.GetFileName(msiPath).Contains("sbin-installer", StringComparison.OrdinalIgnoreCase);
            }
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = arguments,
                UseShellExecute = false, // Inherit admin privileges from parent process
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            WriteLog($"Running MSI installer: {arguments}");
            Console.WriteLine($"     [>] Running MSI installer: {arguments}");
            
            if (isSbinInstaller)
            {
                Logger.Info($"CRITICAL PACKAGE: {packageName} - using aggressive retry strategy");
                WriteLog($"CRITICAL PACKAGE: {packageName} - using aggressive retry strategy");
            }
            
            // CRITICAL: sbin-installer MUST succeed - everything depends on it
            // Use ultra-aggressive retry for sbin-installer, standard retry for others.
            // NOTE: 1618 (another install already running) is NOT a package failure and
            // is never counted against this budget - see the collision handling below.
            int maxRetries = isSbinInstaller ? 10 : 5;
            int retryDelaySeconds = isSbinInstaller ? 15 : 10;
            bool retryOnAnyError = isSbinInstaller; // sbin-installer retries on ANY error

            // Upper bound on how long we wait out a concurrent installer (notably the
            // Intune Management Extension during ESP, which drives msiexec in parallel)
            // before each launch. Generous on purpose: a large Win32 app can hold the
            // global installer for several minutes.
            const int installerIdleWaitSeconds = 600;
            // Guard against an installer that never releases the mutex: cap how many
            // un-counted 1618 collisions we will absorb before giving up.
            const int maxCollisions = 30;
            // Backoff between collision/launch-failure retries. WaitForWindowsInstallerIdle
            // returns immediately when the mutex can't be opened (e.g. ACL denied), so
            // without this a 1618 collision would busy-spin straight to maxCollisions.
            const int collisionBackoffMs = 3000;
            int collisions = 0;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                // Serialize behind any in-progress MSI transaction so we don't collide
                // with 1618 ERROR_INSTALL_ALREADY_RUNNING in the first place. This is
                // the "brute force" part: BootstrapMate waits its turn instead of failing.
                if (!WaitForWindowsInstallerIdle(installerIdleWaitSeconds))
                {
                    // Still busy after the cap. We launch anyway (a resulting 1618 is
                    // handled below as an un-counted collision), but surface the stall.
                    WriteLog($"Windows Installer still busy after {installerIdleWaitSeconds}s; launching anyway (a 1618 will be retried, not failed)");
                }

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    // Process.Start should not return null for a valid exe path, but if it
                    // does, don't silently burn a retry with no diagnostics: log, back off,
                    // and retry without consuming the functional budget.
                    // Consume the attempt (bounded by maxRetries) rather than decrementing,
                    // so a persistent launch failure can't spin forever.
                    WriteLog("Failed to start msiexec.exe (Process.Start returned null); backing off and retrying");
                    await Task.Delay(collisionBackoffMs);
                    continue;
                }

                await process.WaitForExitAsync();
                WriteLog($"MSI installer completed with exit code: {process.ExitCode}");

                if (process.ExitCode == 0)
                {
                    // Success
                    if (isSbinInstaller)
                    {
                        Logger.Info($"CRITICAL PACKAGE INSTALLED: {packageName}");
                        WriteLog($"CRITICAL PACKAGE INSTALLED: {packageName}");
                    }
                    return;
                }

                if (process.ExitCode == 1618)
                {
                    // ERROR_INSTALL_ALREADY_RUNNING. This is a scheduling collision, not
                    // a package failure - another installer grabbed the global
                    // _MSIExecute mutex between our idle check and our launch. Wait for
                    // it to finish and retry WITHOUT consuming the functional retry
                    // budget, and log at debug only (never warn or fail on it).
                    collisions++;
                    if (collisions > maxCollisions)
                    {
                        throw new Exception($"MSI installer for {packageName} could not start: another Windows Installer transaction held the system across {collisions} collisions.");
                    }
                    WriteLog($"MSI already running (1618) - waiting for the active installer to finish, then retrying (collision {collisions}, not counted as a failed attempt)");
                    WaitForWindowsInstallerIdle(installerIdleWaitSeconds);
                    // Backoff so we don't busy-spin when the mutex can't be opened (ACL
                    // denied) and WaitForWindowsInstallerIdle returns immediately.
                    await Task.Delay(collisionBackoffMs);
                    attempt--; // do not count a scheduling collision against maxRetries
                    continue;
                }

                // Genuine install failure (non-zero, non-1618).
                // For sbin-installer, retry on ANY error. For others, only retry on specific codes.
                bool shouldRetry = retryOnAnyError || IsRetryableErrorCode(process.ExitCode);
                if (shouldRetry && attempt < maxRetries)
                {
                    string reason = isSbinInstaller ? "CRITICAL PACKAGE - retrying on any error" : "retryable error code";
                    WriteLog($"MSI installer failed with exit code {process.ExitCode} ({reason}), retrying in {retryDelaySeconds} seconds... (attempt {attempt}/{maxRetries})");
                    Logger.Warning($"MSI error {process.ExitCode}, retrying in {retryDelaySeconds} seconds... (attempt {attempt}/{maxRetries})");
                    await Task.Delay(retryDelaySeconds * 1000);
                    continue;
                }

                // No more retries or non-retryable error
                string errorMsg = isSbinInstaller
                    ? $"CRITICAL: {packageName} failed with exit code {process.ExitCode} after {attempt} attempts. System cannot continue without this package."
                    : $"MSI installer failed with exit code: {process.ExitCode}";
                throw new Exception(errorMsg);
            }

            // Reaching here means every attempt ended in a launch failure (Process.Start
            // returned null) rather than an installer exit code. Falling out quietly would
            // report the package as installed when msiexec never ran.
            throw new Exception(
                $"MSI installer for {packageName} was never started: msiexec.exe could not be launched in {maxRetries} attempts.");
        }

        /// <summary>
        /// Blocks until the global Windows Installer execute mutex (Global\_MSIExecute)
        /// is free - i.e. no other MSI transaction is mid-execution - or until
        /// maxWaitSeconds elapses. Windows Installer holds this mutex for the duration
        /// of a package's InstallExecuteSequence, so waiting on it lets BootstrapMate
        /// serialize behind concurrent installers (e.g. the Intune Management Extension
        /// during ESP) instead of failing with 1618 ERROR_INSTALL_ALREADY_RUNNING.
        ///
        /// Best-effort by design: if the mutex can't be opened (absent, or access
        /// denied by its ACL) we treat the installer as idle and return immediately,
        /// so a permissions quirk never blocks a bootstrap. Returns true once idle,
        /// false if the wait timed out.
        /// </summary>
        static bool WaitForWindowsInstallerIdle(int maxWaitSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            bool logged = false;
            while (true)
            {
                try
                {
                    if (!Mutex.TryOpenExisting(@"Global\_MSIExecute", out var mutex))
                    {
                        // Mutex does not exist => no install is executing.
                        return true;
                    }
                    using (mutex)
                    {
                        bool acquired = false;
                        try
                        {
                            acquired = mutex.WaitOne(0);
                            if (acquired) return true; // installer idle
                        }
                        catch (AbandonedMutexException)
                        {
                            return true; // previous owner died; the installer is idle
                        }
                        finally
                        {
                            if (acquired) mutex.ReleaseMutex();
                        }
                    }
                }
                catch (Exception)
                {
                    // Can't open/inspect the mutex (access denied, invalid name, etc.).
                    // Don't block a bootstrap on it - treat as idle and let msiexec run.
                    return true;
                }

                if (DateTime.UtcNow >= deadline) return false;
                if (!logged)
                {
                    WriteLog("Windows Installer busy (another MSI transaction active) - waiting for it to finish before installing");
                    logged = true;
                }
                Thread.Sleep(2000);
            }
        }

        static bool IsRetryableErrorCode(int exitCode)
        {
            // Common retryable MSI error codes
            // 1618: ERROR_INSTALL_ALREADY_RUNNING (handled separately above)
            // 1603: ERROR_INSTALL_FAILURE (generic, sometimes transient)
            // 1619: ERROR_INSTALL_PACKAGE_OPEN_FAILED (file locked)
            // 1620: ERROR_INSTALL_PACKAGE_INVALID (corrupted, retry might help if re-downloaded)
            // 1612: ERROR_INSTALL_SOURCE_ABSENT (network issues)
            return exitCode == 1603 || exitCode == 1619 || exitCode == 1620 || exitCode == 1612;
        }
        
        static async Task RunExecutable(string exePath, JsonElement packageInfo)
        {
            var args = GetArguments(packageInfo);
            string arguments = string.Join(" ", args);
            
            WriteLog($"Running executable: {exePath} {arguments}");
            Console.WriteLine($"     [>] Running executable: {exePath} {arguments}");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,  // Inherit admin privileges from parent process
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                // Capture output for debugging
                if (startInfo.RedirectStandardOutput)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Logger.WriteCapturedOutput(PackageLabel(packageInfo), output);
                    }
                }
                
                if (startInfo.RedirectStandardError)
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Logger.WriteCapturedOutput(PackageLabel(packageInfo), error, isError: true);
                    }
                }
                
                WriteLog($"Executable completed with exit code: {process.ExitCode}");
                
                if (process.ExitCode != 0)
                {
                    throw new Exception($"Executable failed with exit code: {process.ExitCode}");
                }
            }
        }
        
        static string? FindChocolateyExecutable()
        {
            // Try common Chocolatey installation paths in order of preference
            string[] candidatePaths = {
                // Check environment variable first
                Environment.GetEnvironmentVariable("ChocolateyInstall") + @"\bin\choco.exe",
                // Standard installation paths
                @"C:\ProgramData\chocolatey\bin\choco.exe",
                @"C:\Chocolatey\bin\choco.exe",
                @"C:\tools\chocolatey\bin\choco.exe"
            };
            
            foreach (string candidatePath in candidatePaths)
            {
                if (!string.IsNullOrEmpty(candidatePath) && File.Exists(candidatePath))
                {
                    Logger.Debug($"Found Chocolatey executable at: {candidatePath}");
                    return candidatePath;
                }
            }
            
            // Fallback to PATH resolution
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "choco.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000); // 5 second timeout
                    if (process.ExitCode == 0)
                    {
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        if (!string.IsNullOrEmpty(output))
                        {
                            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0 && File.Exists(lines[0]))
                            {
                                Logger.Debug($"Found Chocolatey executable via WHERE command: {lines[0]}");
                                return lines[0];
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"WHERE command failed: {ex.Message}");
            }
            
            // Instead of falling back to "choco.exe", return null to indicate no executable found
            Logger.Warning("Could not locate Chocolatey executable anywhere on the system");
            return null!; // Fixed nullable warning
        }

        static string? FindSbinInstaller()
        {
            // Primary installation path for sbin-installer
            string primaryPath = @"C:\Program Files\sbin\installer.exe";
            
            if (File.Exists(primaryPath))
            {
                Logger.Debug($"Found sbin-installer at primary location: {primaryPath}");
                return primaryPath;
            }
            
            // Secondary common installation paths
            var alternatePaths = new[]
            {
                @"C:\Program Files (x86)\sbin\installer.exe",
                @"C:\sbin\installer.exe",
                @"C:\Tools\sbin\installer.exe"
            };
            
            foreach (var path in alternatePaths)
            {
                if (File.Exists(path))
                {
                    Logger.Debug($"Found sbin-installer at alternate location: {path}");
                    return path;
                }
            }
            
            // Try PATH resolution - but only accept paths containing "sbin" to avoid Chocolatey conflicts
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "installer.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000); // 5 second timeout
                    if (process.ExitCode == 0)
                    {
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        if (!string.IsNullOrEmpty(output))
                        {
                            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                // Only accept paths that contain "sbin" to avoid Chocolatey conflicts
                                if (File.Exists(line) && (line.Contains("sbin", StringComparison.OrdinalIgnoreCase) || 
                                    line.Contains("System Binary", StringComparison.OrdinalIgnoreCase)))
                                {
                                    Logger.Debug($"Found sbin-installer via WHERE command: {line}");
                                    return line;
                                }
                                else if (File.Exists(line))
                                {
                                    Logger.Debug($"Skipping installer.exe at {line} - not sbin-installer (likely Chocolatey conflict)");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"WHERE command failed for sbin-installer: {ex.Message}");
            }
            
            Logger.Debug("sbin-installer not found on the system. Install it from https://github.com/windowsadmins/sbin-installer");
            Logger.Debug("Note: Always use full path 'C:\\Program Files\\sbin\\installer.exe' to avoid Chocolatey conflicts");
            return null;
        }

        static bool IsSbinInstallerAvailable()
        {
            return FindSbinInstaller() != null;
        }

        static bool IsCimianBuiltMsi(string msiPath)
        {
            // Check if the MSI was built by cimipkg by scanning for the
            // CIMIAN_PKG_BUILD_INFO property marker in the binary.
            // Exclude known third-party MSIs that might contain the string
            // as a false positive from binary content.
            var fileName = Path.GetFileName(msiPath);
            if (InstallerRouting.IsSbinInstallerPackage(fileName, null))
            {
                Logger.Debug($"Skipping Cimian check for known third-party MSI: {fileName}");
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(msiPath);
                var content = System.Text.Encoding.ASCII.GetString(bytes);
                return content.Contains("CIMIAN_PKG_BUILD_INFO");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not check MSI for Cimian marker: {ex.Message}");
                return false;
            }
        }
        
        static bool IsBrokenChocolateyInstallation()
        {
            // Check if we have a broken Chocolatey installation:
            // - Folder exists at C:\ProgramData\chocolatey
            // - But no working choco.exe executable
            
            string chocolateyRoot = @"C:\ProgramData\chocolatey";
            string chocoExe = Path.Combine(chocolateyRoot, "bin", "choco.exe");
            
            if (Directory.Exists(chocolateyRoot))
            {
                // Folder exists, check if executable works
                if (!File.Exists(chocoExe))
                {
                    Logger.Warning($"Broken Chocolatey detected: folder exists at {chocolateyRoot} but no choco.exe found");
                    return true;
                }
                
                // Executable exists, test if it actually works
                try
                {
                    var testProcess = new ProcessStartInfo
                    {
                        FileName = chocoExe,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    
                    using var process = Process.Start(testProcess);
                    if (process != null)
                    {
                        process.WaitForExit(5000); // 5 second timeout
                        if (process.ExitCode != 0)
                        {
                            Logger.Warning($"Broken Chocolatey detected: choco.exe exists but fails to run (exit code: {process.ExitCode})");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Broken Chocolatey detected: choco.exe exists but cannot be executed: {ex.Message}");
                    return true;
                }
            }
            
            return false;
        }

        static void CleanupChocolateyLib()
        {
            try
            {
                string chocolateyLibPath = @"C:\ProgramData\chocolatey\lib";
                
                if (!Directory.Exists(chocolateyLibPath))
                {
                    Logger.Debug("Chocolatey lib directory does not exist - no cleanup needed");
                    return;
                }
                
                Logger.Debug("Cleaning up potentially corrupted Chocolatey lib directory...");
                Logger.WriteSubProgress("Cleaning Chocolatey cache", "Removing corrupted packages");
                
                // Get all subdirectories in the lib folder
                var libDirectories = Directory.GetDirectories(chocolateyLibPath);
                int cleanedCount = 0;
                
                foreach (string libDir in libDirectories)
                {
                    try
                    {
                        string packageName = Path.GetFileName(libDir);
                        
                        // Look for .nupkg files in this package directory
                        var nupkgFiles = Directory.GetFiles(libDir, "*.nupkg", SearchOption.TopDirectoryOnly);
                        
                        foreach (string nupkgFile in nupkgFiles)
                        {
                            try
                            {
                                // Test if the .nupkg file is a valid ZIP archive
                                using var archive = ZipFile.OpenRead(nupkgFile);
                                var entries = archive.Entries; // This will throw if corrupted
                                Logger.Debug($"Package {packageName} - nupkg file is valid");
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"Found corrupted nupkg file: {nupkgFile} - {ex.Message}");
                                Logger.Debug($"Removing corrupted package directory: {libDir}");
                                
                                // Remove the entire package directory if nupkg is corrupted
                                Directory.Delete(libDir, true);
                                cleanedCount++;
                                
                                Logger.Debug($"Cleaned corrupted package: {packageName}");
                                break; // Move to next package directory
                            }
                        }
                        
                        // Also check for directories without .nupkg files (incomplete installations)
                        if (nupkgFiles.Length == 0)
                        {
                            // Check if this looks like an incomplete installation
                            var filesInDir = Directory.GetFiles(libDir, "*", SearchOption.AllDirectories);
                            var directoriesInDir = Directory.GetDirectories(libDir, "*", SearchOption.AllDirectories);
                            
                            // If there are no nupkg files but there are other files/dirs, it might be incomplete
                            if (filesInDir.Length > 0 || directoriesInDir.Length > 0)
                            {
                                Logger.Warning($"Found package directory without nupkg file: {packageName}");
                                Logger.Debug($"Removing incomplete package directory: {libDir}");
                                
                                Directory.Delete(libDir, true);
                                cleanedCount++;
                                
                                Logger.Debug($"Cleaned incomplete package: {packageName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Could not process package directory {libDir}: {ex.Message}");
                        // Continue with other packages
                    }
                }
                
                if (cleanedCount > 0)
                {
                    Logger.Info($"Cleaned up {cleanedCount} corrupted/incomplete Chocolatey packages");
                    Logger.WriteSubProgress("Chocolatey cleanup complete", $"Removed {cleanedCount} corrupted packages");
                }
                else
                {
                    Logger.Debug("No corrupted Chocolatey packages found");
                }
                
                // Also clean up any orphaned temp files in the chocolatey root
                try
                {
                    string chocolateyRoot = @"C:\ProgramData\chocolatey";
                    var tempFiles = Directory.GetFiles(chocolateyRoot, "*.tmp", SearchOption.TopDirectoryOnly);
                    var lockFiles = Directory.GetFiles(chocolateyRoot, "*.lock", SearchOption.AllDirectories);
                    
                    foreach (string tempFile in tempFiles.Concat(lockFiles))
                    {
                        try
                        {
                            File.Delete(tempFile);
                            Logger.Debug($"Removed temp/lock file: {Path.GetFileName(tempFile)}");
                        }
                        catch
                        {
                            // Ignore errors deleting temp files
                        }
                    }
                }
                catch
                {
                    // Ignore errors in temp file cleanup
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Chocolatey lib cleanup failed: {ex.Message}");
                // Don't fail the entire process if cleanup fails
            }
        }
        
        static async Task<bool> PerformAggressiveChocolateyCleanup()
        {
            try
            {
                string chocolateyRoot = @"C:\ProgramData\chocolatey";
                Logger.Info($"Starting aggressive cleanup of broken Chocolatey installation at: {chocolateyRoot}");
                
                // Step 1: Kill all Chocolatey processes
                Logger.Debug("Step 1: Terminating all Chocolatey processes...");
                var chocoProcesses = Process.GetProcessesByName("choco");
                foreach (var proc in chocoProcesses)
                {
                    try
                    {
                        Logger.Debug($"Terminating chocolatey process (PID: {proc.Id})");
                        proc.Kill();
                        proc.WaitForExit(5000);
                        proc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not kill process {proc.Id}: {ex.Message}");
                    }
                }
                
                // Step 2: Clean environment variables first (critical for forcing reinstall)
                Logger.Debug("Step 2: Cleaning Chocolatey environment variables...");
                try
                {
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null, EnvironmentVariableTarget.Machine);
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null, EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null, EnvironmentVariableTarget.Process);
                    Logger.Debug("Environment variables cleaned");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not clean environment variables: {ex.Message}");
                }
                
                // Step 3: CRITICAL - Complete removal of Chocolatey directory
                // This MUST succeed or Chocolatey installer will think it's already installed
                if (Directory.Exists(chocolateyRoot))
                {
                    Logger.Warning($"CRITICAL: Performing complete removal of broken Chocolatey at: {chocolateyRoot}");
                    Logger.Info("This is necessary because Chocolatey installer detects existing folder and skips installation");
                    
                    bool removalSuccess = false;
                    
                    // Method 1: Use PowerShell with maximum force
                    Logger.Debug("Method 1: Using PowerShell Remove-Item with maximum force...");
                    try
                    {
                        string psCommand = @"
                            $ErrorActionPreference = 'Stop'
                            $path = 'C:\ProgramData\chocolatey'
                            if (Test-Path $path) {
                                Write-Host 'Attempting PowerShell removal...'
                                Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
                                Start-Sleep -Seconds 2
                                if (Test-Path $path) {
                                    Write-Host 'Standard removal failed, trying takeown + icacls...'
                                    takeown /f $path /r /d y | Out-Null
                                    icacls $path /grant administrators:F /t | Out-Null
                                    Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
                                }
                            }
                        ";
                        
                        var psProcess = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-ExecutionPolicy Bypass -NoProfile -Command \"{psCommand}\"",
                            UseShellExecute = true, // Run elevated
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        
                        using var process = Process.Start(psProcess);
                        if (process != null)
                        {
                            await process.WaitForExitAsync();
                            Logger.Debug($"PowerShell cleanup completed with exit code: {process.ExitCode}");
                        }
                        
                        await Task.Delay(2000); // Wait for file handles to release
                        
                        if (!Directory.Exists(chocolateyRoot))
                        {
                            Logger.Info("✅ PowerShell complete removal successful");
                            removalSuccess = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"PowerShell complete removal failed: {ex.Message}");
                    }
                    
                    // Method 2: C# Directory.Delete with multiple retries
                    if (!removalSuccess && Directory.Exists(chocolateyRoot))
                    {
                        Logger.Debug("Method 2: Using C# Directory.Delete with retries...");
                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            try
                            {
                                Directory.Delete(chocolateyRoot, true);
                                Logger.Info($"✅ C# Directory.Delete succeeded on attempt {attempt}");
                                removalSuccess = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"C# Directory.Delete attempt {attempt}/5 failed: {ex.Message}");
                                if (attempt < 5)
                                {
                                    await Task.Delay(3000); // Wait 3 seconds before retry
                                }
                            }
                        }
                    }
                    
                    // Method 3: Last resort - try to remove just enough to make installer think it's not installed
                    if (!removalSuccess && Directory.Exists(chocolateyRoot))
                    {
                        Logger.Warning("Method 3: Last resort - removing critical files to trick installer...");
                        try
                        {
                            // Remove the bin folder specifically (this is what Chocolatey installer checks)
                            string binPath = Path.Combine(chocolateyRoot, "bin");
                            if (Directory.Exists(binPath))
                            {
                                Directory.Delete(binPath, true);
                                Logger.Info("Removed bin folder");
                            }
                            
                            // Remove the lib folder (packages)
                            string libPath = Path.Combine(chocolateyRoot, "lib");
                            if (Directory.Exists(libPath))
                            {
                                Directory.Delete(libPath, true);
                                Logger.Info("Removed lib folder");
                            }
                            
                            // Remove install marker files
                            string[] markerFiles = {
                                Path.Combine(chocolateyRoot, ".chocolatey"),
                                Path.Combine(chocolateyRoot, "choco.exe.manifest"),
                                Path.Combine(chocolateyRoot, "redirects")
                            };
                            
                            foreach (string marker in markerFiles)
                            {
                                if (File.Exists(marker))
                                {
                                    File.Delete(marker);
                                    Logger.Debug($"Removed marker: {marker}");
                                }
                            }
                            
                            removalSuccess = true; // Good enough for installer to proceed
                            Logger.Info("✅ Critical file removal successful - installer should proceed");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Last resort removal also failed: {ex.Message}");
                        }
                    }
                    
                    if (!removalSuccess)
                    {
                        Logger.Error("❌ CRITICAL FAILURE: Could not remove broken Chocolatey installation");
                        Logger.Error("This will prevent proper Chocolatey reinstallation");
                        return false;
                    }
                }
                
                // Step 4: Clean up PATH environment variable
                Logger.Debug("Step 4: Cleaning Chocolatey from PATH...");
                try
                {
                    // Read and write Path straight from the registry. Environment.GetEnvironmentVariable
                    // expands REG_EXPAND_SZ tokens, so writing the result back would permanently flatten
                    // entries such as %SystemRoot% for every account on the machine.
                    using var env = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true);

                    if (env is null)
                    {
                        Logger.Warning("Could not open the machine environment key; leaving PATH untouched");
                    }
                    else
                    {
                        RegistryValueKind kind = env.GetValueKind("Path");
                        string currentPath = env.GetValue("Path", "",
                            RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

                        // Match Chocolatey's own directory, not every path containing the word.
                        string chocoRoot = (Environment.GetEnvironmentVariable("ChocolateyInstall")
                                            ?? @"C:\ProgramData\chocolatey").TrimEnd('\\');

                        bool IsChocolateyEntry(string entry)
                        {
                            string trimmed = entry.Trim().TrimEnd('\\');
                            return trimmed.Equals(chocoRoot, StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith(chocoRoot + @"\", StringComparison.OrdinalIgnoreCase);
                        }

                        string cleanedPath = string.Join(";",
                            currentPath.Split(';')
                                       .Where(p => !string.IsNullOrWhiteSpace(p))
                                       .Where(p => !IsChocolateyEntry(p)));

                        if (cleanedPath != currentPath)
                        {
                            env.SetValue("Path", cleanedPath, kind);
                            Logger.Debug($"Cleaned Chocolatey paths from system PATH (preserved {kind})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not clean PATH variable: {ex.Message}");
                }
                
                // Step 5: Final verification and force environment refresh
                Logger.Debug("Step 5: Final verification and environment refresh...");
                try
                {
                    // Force refresh environment variables in current process
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null);
                    
                    // Update PATH in current process
                    string machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                    string userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                    Environment.SetEnvironmentVariable("PATH", $"{machinePath};{userPath}");
                    
                    Logger.Debug("Environment variables refreshed in current process");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not refresh environment variables: {ex.Message}");
                }
                
                // Final check
                bool isCleanedUp = !Directory.Exists(chocolateyRoot) || 
                                  !Directory.Exists(Path.Combine(chocolateyRoot, "bin")) ||
                                  !File.Exists(Path.Combine(chocolateyRoot, "bin", "choco.exe"));
                
                if (isCleanedUp)
                {
                    Logger.Info("✅ Complete Chocolatey cleanup completed successfully");
                    Logger.Info("Chocolatey installer should now detect a clean system and perform full installation");
                    return true;
                }
                else
                {
                    Logger.Error("❌ Complete Chocolatey cleanup failed - installation artifacts still present");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception during complete Chocolatey cleanup: {ex.Message}");
                return false;
            }
        }
        
        static async Task EnsureChocolateyInstalled()
        {
            Logger.Debug("Checking if Chocolatey is installed...");
            
            // FIRST: Check for broken Chocolatey installation and clean it up
            if (IsBrokenChocolateyInstallation())
            {
                Logger.Warning("Detected broken Chocolatey installation - performing aggressive cleanup...");
                Logger.WriteSubProgress("Cleaning broken Chocolatey installation", "Removing corrupted files");
                
                // This is critical - we MUST clean up broken installations or they prevent proper reinstall
                bool cleanupSuccessful = await PerformAggressiveChocolateyCleanup();
                if (!cleanupSuccessful)
                {
                    throw new Exception("Failed to clean up broken Chocolatey installation. Cannot proceed with package installations.");
                }
            }
            else
            {
                // Check if Chocolatey directory is completely missing - might indicate recent cleanup
                string chocolateyRoot = @"C:\ProgramData\chocolatey";
                if (!Directory.Exists(chocolateyRoot))
                {
                    Logger.Debug("Chocolatey directory does not exist - may have been cleaned by previous package");
                    Logger.Info("No existing Chocolatey installation found - will perform fresh installation");
                }
            }
            
            // Clean up any corrupted Chocolatey lib directory
            CleanupChocolateyLib();
            
            // Find chocolatey executable path using improved method
            string? chocoPath = FindChocolateyExecutable();
            
            // If we have a valid path, test if Chocolatey actually works
            if (chocoPath != null)
            {
                var chocoCheck = new ProcessStartInfo
                {
                    FileName = chocoPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                try
                {
                    using var checkProcess = Process.Start(chocoCheck);
                    if (checkProcess != null)
                    {
                        await checkProcess.WaitForExitAsync();
                        if (checkProcess.ExitCode == 0)
                        {
                            Logger.Debug("Chocolatey is already installed and working");
                            Logger.WriteSubProgress("Chocolatey is already installed and working");
                            return; // Chocolatey is available and functional
                        }
                        else
                        {
                            Logger.Warning($"Chocolatey executable found but not working (exit code: {checkProcess.ExitCode})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Chocolatey check failed: {ex.Message}");
                    // choco.exe found but not functional, need to reinstall
                }
            }
            
            Logger.Debug("Chocolatey not found or not working. Installing Chocolatey...");
            Logger.WriteSubProgress("Installing Chocolatey package manager");
            
            // Install Chocolatey using the official installation method
            // CRITICAL: Use -Force parameter to ensure clean installation over broken remains
            string installScript = @"
                Set-ExecutionPolicy Bypass -Scope Process -Force
                [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
                
                # Force clean installation even if remnants exist
                $env:CHOCOLATEY_FORCE = 'true'
                
                # Download and execute installer
                iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
                
                # Verify installation worked
                if (Test-Path 'C:\ProgramData\chocolatey\bin\choco.exe') {
                    Write-Host 'Chocolatey installation verified'
                    exit 0
                } else {
                    Write-Host 'Chocolatey installation failed - executable not found' -ForegroundColor Red
                    exit 1
                }
            ";
            
            var chocolateyInstall = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -Command \"{installScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = true, // Critical for ESP privilege inheritance
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            
            using var installProcess = Process.Start(chocolateyInstall);
            if (installProcess != null)
            {
                await installProcess.WaitForExitAsync();
                
                Logger.Debug($"Chocolatey installation completed with exit code: {installProcess.ExitCode}");
                
                if (installProcess.ExitCode != 0)
                {
                    throw new Exception($"Chocolatey installation failed with exit code: {installProcess.ExitCode}");
                }
                
                Logger.Debug("Chocolatey installed successfully");
                Logger.WriteSubProgress("Chocolatey installed successfully");
                
                // Refresh environment variables to pick up chocolatey PATH
                Logger.Debug("Refreshing environment variables...");
                RefreshEnvironmentPath();
                
                // Wait a moment for the installation to settle
                await Task.Delay(2000);
                
                // Verify installation by re-checking with updated paths
                string? newChocoPath = FindChocolateyExecutable();
                if (newChocoPath != null && await VerifyChocolateyInstallation(newChocoPath))
                {
                    Logger.Debug($"Chocolatey installation verified at: {newChocoPath}");
                    Logger.WriteSubProgress("Chocolatey installation verified successfully");
                }
                else
                {
                    Logger.Warning($"Chocolatey installation verification failed. Expected location: C:\\ProgramData\\chocolatey\\bin\\choco.exe");
                    Logger.Warning("Installation will proceed but may encounter issues. Consider manually checking Chocolatey installation.");
                }
            }
        }
        
        static async Task<bool> VerifyChocolateyInstallation(string chocoPath)
        {
            try
            {
                var verifyStartInfo = new ProcessStartInfo
                {
                    FileName = chocoPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(verifyStartInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // Verification failed
            }
            
            return false;
        }
        
        static void RefreshEnvironmentPath()
        {
            try
            {
                // Get the current PATH from the registry (machine and user)
                string machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                string userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                
                // Check for Chocolatey installation paths and add them if missing
                List<string> additionalPaths = new List<string>();
                
                // Common Chocolatey installation paths - prioritize by most common
                string[] chocolateyPaths = {
                    @"C:\ProgramData\chocolatey\bin",
                    Environment.GetEnvironmentVariable("ChocolateyInstall") + @"\bin",
                    @"C:\Chocolatey\bin"
                };
                
                foreach (string chocoPath in chocolateyPaths)
                {
                    if (!string.IsNullOrEmpty(chocoPath) && Directory.Exists(chocoPath))
                    {
                        string chocoExePath = Path.Combine(chocoPath, "choco.exe");
                        if (File.Exists(chocoExePath))
                        {
                            // Check if this path is already in the combined PATH
                            string combinedCurrentPath = machinePath + ";" + userPath;
                            if (!combinedCurrentPath.ToLowerInvariant().Contains(chocoPath.ToLowerInvariant()))
                            {
                                additionalPaths.Add(chocoPath);
                                Logger.Debug($"Adding Chocolatey path to environment PATH: {chocoPath}");
                            }
                            else
                            {
                                Logger.Debug($"Chocolatey path already in PATH: {chocoPath}");
                            }
                            break; // Found a working chocolatey installation
                        }
                        else
                        {
                            Logger.Debug($"Chocolatey directory exists but executable missing: {chocoPath}");
                        }
                    }
                }
                
                // Combine all paths
                string combinedPath = string.Join(";", new[] { machinePath, userPath }.Concat(additionalPaths).Where(p => !string.IsNullOrEmpty(p)));
                
                // Update the current process PATH
                Environment.SetEnvironmentVariable("PATH", combinedPath, EnvironmentVariableTarget.Process);
                
                if (additionalPaths.Count > 0)
                {
                    Logger.Debug($"Environment PATH updated with {additionalPaths.Count} Chocolatey paths");
                }
                else
                {
                    // Try to find chocolatey using FindChocolateyExecutable to provide more info
                    string? foundPath = FindChocolateyExecutable();
                    if (foundPath != null)
                    {
                        Logger.Debug("Chocolatey executable found via direct search - PATH already correct");
                    }
                    else
                    {
                        Logger.Debug("No Chocolatey installation found to add to PATH");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to refresh PATH environment variable: {ex.Message}");
                // Continue anyway - chocolatey might still work if already in PATH
            }
        }
        
        static async Task<bool> IsChocolateyPackageInstalled(string packageId)
        {
            try
            {
                // Find chocolatey executable path using improved method
                string? chocoPath = FindChocolateyExecutable();
                
                if (chocoPath == null)
                {
                    Logger.Debug($"Chocolatey executable not found - package '{packageId}' assumed not installed");
                    return false; // If no chocolatey, package definitely not installed
                }
                
                // Use 'choco list' to check if package is installed (modern Chocolatey syntax)
                var startInfo = new ProcessStartInfo
                {
                    FileName = chocoPath,
                    Arguments = $"list \"{packageId}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                Logger.Debug($"Checking if package '{packageId}' is installed using: {chocoPath}");
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        
                        // Parse the output - if the package is installed, it will be listed
                        // Format is typically: "packagename version"
                        // If not installed, output will be empty or show "0 packages installed"
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            var trimmedLine = line.Trim();
                            if (trimmedLine.StartsWith(packageId, StringComparison.OrdinalIgnoreCase) && 
                                !trimmedLine.Contains("packages installed") &&
                                !trimmedLine.Contains("Chocolatey"))
                            {
                                Logger.Debug($"Package '{packageId}' is installed: {trimmedLine}");
                                return true;
                            }
                        }
                        
                        Logger.Debug($"Package '{packageId}' check output: {output.Trim()}");
                    }
                    else
                    {
                        string error = await process.StandardError.ReadToEndAsync();
                        string output = await process.StandardOutput.ReadToEndAsync();
                        Logger.Warning($"Chocolatey list command failed with exit code {process.ExitCode}");
                        Logger.Debug($"Chocolatey list stderr: {error}");
                        Logger.Debug($"Chocolatey list stdout: {output}");
                    }
                }
                
                Logger.Debug($"Package '{packageId}' is not installed");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error checking if package '{packageId}' is installed: {ex.Message}");
                // If we can't determine, assume it's not installed and try to install
                return false;
            }
        }
        
        static async Task RunChocolateyInstall(string nupkgPath, JsonElement packageInfo)
        {
            var args = GetArguments(packageInfo);
            
            // First check if chocolatey is installed, install it if missing
            await EnsureChocolateyInstalled();
            
            // Extract package details from the .nupkg file by reading the .nuspec
            string packageDir = Path.GetDirectoryName(nupkgPath) ?? Path.GetTempPath();
            string packageId = "";
            string packageVersion = "";
            
            try
            {
                // Read the .nuspec file from the .nupkg to get the correct package ID and version
                using var archive = ZipFile.OpenRead(nupkgPath);
                var nuspecEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec"));
                
                if (nuspecEntry != null)
                {
                    using var stream = nuspecEntry.Open();
                    using var reader = new StreamReader(stream);
                    string nuspecContent = await reader.ReadToEndAsync();
                    
                    // Parse XML to extract ID and version
                    var doc = System.Xml.Linq.XDocument.Parse(nuspecContent);
                    var ns = doc.Root?.GetDefaultNamespace();
                    
                    if (ns != null)
                    {
                        packageId = doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value ?? "";
                        packageVersion = doc.Root?.Element(ns + "metadata")?.Element(ns + "version")?.Value ?? "";
                    }
                    
                    Logger.Debug($"Extracted from .nuspec: ID='{packageId}', Version='{packageVersion}'");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to read package metadata from {nupkgPath}: {ex.Message}");
                // Fallback to filename parsing
                string packageFileName = Path.GetFileNameWithoutExtension(nupkgPath);
                int lastDashIndex = packageFileName.LastIndexOf('-');
                if (lastDashIndex > 0 && lastDashIndex < packageFileName.Length - 1)
                {
                    string potentialVersion = packageFileName.Substring(lastDashIndex + 1);
                    if (potentialVersion.Contains('.'))
                    {
                        packageId = packageFileName.Substring(0, lastDashIndex);
                        packageVersion = potentialVersion;
                    }
                }
                
                if (string.IsNullOrEmpty(packageId))
                {
                    packageId = packageFileName;
                }
                Logger.Debug($"Fallback filename parsing: ID='{packageId}', Version='{packageVersion}'");
            }
            
            if (string.IsNullOrEmpty(packageId))
            {
                throw new Exception($"Could not determine package ID from {nupkgPath}");
            }
            
            // Check if package is already installed and determine the correct action
            bool isInstalled = await IsChocolateyPackageInstalled(packageId);
            string action = isInstalled ? "upgrade" : "install";
            
            Logger.Debug($"Package '{packageId}' is {(isInstalled ? "already installed" : "not installed")} - using '{action}' command");
            Logger.WriteSubProgress($"Package '{packageId}' is {(isInstalled ? "already installed" : "not installed")} - using '{action}' command");
            
            // Use proper chocolatey syntax with smart install/upgrade logic
            // Always use --force (-f) to handle conflicts and ensure package state
            // Add --no-progress, --quiet, and --limit-output to suppress sounds and visual feedback
            // Chocolatey verifies the checksums of what a package downloads. That
            // check stays on unless the item opts out with "ignoreChecksums": true.
            string checksums = IgnoreChocolateyChecksums(packageInfo) ? "--ignore-checksums " : "";
            if (checksums.Length > 0)
                Logger.Warning($"{packageId}: Chocolatey checksum verification disabled by the item's ignoreChecksums");
            string arguments;
            if (!string.IsNullOrEmpty(packageVersion))
            {
                arguments = $"{action} \"{packageId}\" --source=\"{packageDir}\" --version=\"{packageVersion}\" -y {checksums}--acceptlicense --confirm --force --no-progress --quiet --limit-output {string.Join(" ", args)}";
            }
            else
            {
                arguments = $"{action} \"{packageId}\" --source=\"{packageDir}\" -y {checksums}--acceptlicense --confirm --force --no-progress --quiet --limit-output {string.Join(" ", args)}";
            }

            // Find chocolatey executable path using improved method
            string? chocoPath = FindChocolateyExecutable();
            
            if (chocoPath == null)
            {
                Logger.Error("CRITICAL: Chocolatey executable not found after installation attempt");
                Logger.Error("Expected locations checked:");
                Logger.Error("  - C:\\ProgramData\\chocolatey\\bin\\choco.exe");
                Logger.Error("  - C:\\Chocolatey\\bin\\choco.exe");
                Logger.Error("  - %ChocolateyInstall%\\bin\\choco.exe");
                throw new Exception("Chocolatey executable not found. Installation may have failed or PATH not properly configured.");
            }
            
            Logger.Debug($"Using Chocolatey executable: {chocoPath}");

            // In ESP environment, BootstrapMate should already be running elevated
            // Use PowerShell to run Chocolatey and capture output for better error reporting
            // Suppress PowerShell host notifications and sounds
            string powershellCommand = $"$host.UI.RawUI.WindowTitle = 'Silent'; $ProgressPreference = 'SilentlyContinue'; $ErrorActionPreference = 'Continue'; & '{chocoPath}' {arguments}";
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -Command \"{powershellCommand}\"",
                UseShellExecute = false, // Changed to false to capture output
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            
            Logger.Debug($"Executing Chocolatey command: {chocoPath} {arguments}");
            Logger.WriteSubProgress("Running Chocolatey", $"{action} command");
            
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                // Capture output for better error reporting
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                await process.WaitForExitAsync();
                
                var stdout = await outputTask;
                var stderr = await errorTask;
                
                Logger.Debug($"Chocolatey completed with exit code: {process.ExitCode}");
                
                // Always log ALL output for debugging - this is critical for troubleshooting
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    Logger.WriteCapturedOutput("Chocolatey", stdout);
                }
                
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Logger.WriteCapturedOutput("Chocolatey", stderr, isError: true);
                }
                
                if (process.ExitCode != 0)
                {
                    // Enhanced error message with all available details
                    var errorParts = new List<string>();
                    
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        errorParts.Add($"STDERR: {stderr.Trim()}");
                    }
                    
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        // Include relevant stdout for failed commands
                        errorParts.Add($"STDOUT: {stdout.Trim()}");
                    }
                    
                    // Add diagnostic information about the executable used
                    errorParts.Add($"Executable used: {chocoPath}");
                    errorParts.Add($"Arguments: {arguments}");
                    
                    string errorDetails = errorParts.Count > 0 ? $" - {string.Join(" | ", errorParts)}" : "";
                    
                    Logger.Error($"Chocolatey {action} failed for package '{packageId}' with exit code {process.ExitCode}");
                    Logger.Error($"Full command: {chocoPath} {arguments}");
                    
                    // Check for common error patterns and provide helpful suggestions
                    if (stderr.Contains("not recognized") || stderr.Contains("CommandNotFoundException"))
                    {
                        Logger.Error("ERROR: Chocolatey executable not recognized by PowerShell");
                        Logger.Error($"Verify that Chocolatey is properly installed at: {chocoPath}");
                        Logger.Error("Try running 'refreshenv' or restart the terminal if running manually");
                    }
                    
                    throw new Exception($"Chocolatey {action} failed with exit code {process.ExitCode}: {errorDetails}");
                }
                
                // Log successful installation details if verbose
                if (!string.IsNullOrWhiteSpace(stdout) && stdout.ToLower().Contains("successfully installed"))
                {
                    var lines = stdout.Split('\n');
                    var successLines = lines.Where(l => l.ToLower().Contains("successfully")).ToList();
                    if (successLines.Any())
                    {
                        Logger.Debug($"Chocolatey success: {string.Join(", ", successLines.Select(l => l.Trim()))}");
                    }
                }
            }
        }

        static async Task RunSbinInstall(string packagePath, JsonElement packageInfo)
        {
            var args = GetArguments(packageInfo);
            
            // Find sbin-installer executable
            string? sbinPath = FindSbinInstaller();
            
            if (sbinPath == null)
            {
                Logger.Error("sbin-installer not found. This is required for .pkg and .nupkg packages.");
                Logger.Error("Please install sbin-installer from: https://github.com/windowsadmins/sbin-installer/releases");
                Logger.Error("Expected location: C:\\Program Files\\sbin\\installer.exe");
                Logger.Error("Note: If you see Chocolatey conflicts, run the FixChocoConflict.ps1 script.");
                Logger.Error("IMPORTANT: Use full path 'C:\\Program Files\\sbin\\installer.exe' to avoid conflicts.");
                throw new Exception("sbin-installer executable not found. Cannot proceed with package installation.");
            }
            
            Logger.Debug($"Using sbin-installer at: {sbinPath}");
            
            // Determine target from package info, or use default "/" (matching sbin-installer default)
            string target = "/"; // Default to system root, same as sbin-installer default
            
            if (packageInfo.TryGetProperty("target", out var targetProperty))
            {
                string? specifiedTarget = targetProperty.GetString();
                if (!string.IsNullOrEmpty(specifiedTarget))
                {
                    target = specifiedTarget;
                }
                // If target is specified but empty/null, keep default "/"
            }
            
            // Build sbin-installer command: installer --pkg <path> --target <target> [args]
            var allArgs = new List<string>
            {
                "--pkg", $"\"{packagePath}\"",
                "--target", target
            };
            
            // Add any additional arguments from the package info
            allArgs.AddRange(args);
            
            string arguments = string.Join(" ", allArgs);
            
            Logger.Debug($"Running {sbinPath} {arguments}");
            Logger.WriteSubProgress("Running install for", Path.GetFileName(packagePath));
            
            var startInfo = new ProcessStartInfo
            {
                FileName = sbinPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                // Capture output for better error reporting
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                await process.WaitForExitAsync();
                
                var stdout = await outputTask;
                var stderr = await errorTask;
                
                Logger.Debug($"sbin-installer completed with exit code: {process.ExitCode}");
                
                // Always log output for debugging
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    Logger.Debug($"sbin-installer stdout: {stdout.Trim()}");
                }
                
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Logger.Debug($"sbin-installer stderr: {stderr.Trim()}");
                }
                
                if (process.ExitCode != 0)
                {
                    // Enhanced error message with all available details
                    var errorParts = new List<string>();
                    
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        errorParts.Add($"STDERR: {stderr.Trim()}");
                    }
                    
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        errorParts.Add($"STDOUT: {stdout.Trim()}");
                    }
                    
                    string errorDetails = errorParts.Count > 0 ? $" - {string.Join(" | ", errorParts)}" : "";
                    string packageName = Path.GetFileName(packagePath);
                    
                    Logger.Error($"sbin-installer install failed: {packageName} (exit code {process.ExitCode}){errorDetails}");
                    
                    throw new Exception($"sbin-installer install failed with exit code: {process.ExitCode}{errorDetails}");
                }
                
                // Log success information
                Logger.Debug($"sbin-installer successfully installed: {Path.GetFileName(packagePath)}");
                Logger.WriteSubProgress("Installation completed", "Success");
            }
        }
        
        /// <summary>
        /// Fails the item when the manifest pins a SHA-256 <c>hash</c> the download does not
        /// match. Without a hash there is nothing to check; that is logged, not refused.
        /// </summary>
        static void VerifyPayloadHash(string displayName, JsonElement packageInfo, string actualSha256)
        {
            string? expected = packageInfo.ValueKind == JsonValueKind.Object &&
                packageInfo.TryGetProperty("hash", out var hashProp) && hashProp.ValueKind == JsonValueKind.String
                ? hashProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(expected))
            {
                Logger.Debug($"{displayName}: no manifest hash - payload integrity not pinned (SHA-256 {actualSha256})");
                return;
            }

            if (PayloadIntegrity.Check(expected, actualSha256) is { } problem)
                throw new Exception($"Refusing to install {displayName}: {problem}");

            Logger.Debug($"{displayName}: SHA-256 matches the manifest hash");
        }

        static bool IgnoreChocolateyChecksums(JsonElement packageInfo) =>
            packageInfo.ValueKind == JsonValueKind.Object &&
            packageInfo.TryGetProperty("ignoreChecksums", out var flag) &&
            flag.ValueKind == JsonValueKind.True;

        static List<string> GetArguments(JsonElement packageInfo)
        {
            var arguments = new List<string>();
            
            if (packageInfo.TryGetProperty("arguments", out var argsProperty) && argsProperty.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in argsProperty.EnumerateArray())
                {
                    if (arg.ValueKind == JsonValueKind.String)
                    {
                        arguments.Add(arg.GetString() ?? "");
                    }
                }
            }
            
            return arguments;
        }

        static int ShowStatus()
        {
            try
            {
                Console.WriteLine("BootstrapMate Status");
                Console.WriteLine("==========================");
                Console.WriteLine();

                foreach (InstallationPhase phase in Enum.GetValues<InstallationPhase>())
                {
                    var status = StatusManager.GetPhaseStatus(phase);
                    
                    Console.WriteLine($"Phase: {phase}");
                    Console.WriteLine($"  Stage: {status.Stage}");
                    Console.WriteLine($"  Architecture: {status.Architecture}");
                    
                    if (!string.IsNullOrEmpty(status.StartTime))
                        Console.WriteLine($"  Start Time: {status.StartTime}");
                    
                    if (!string.IsNullOrEmpty(status.CompletionTime))
                        Console.WriteLine($"  Completion Time: {status.CompletionTime}");
                    
                    if (status.ExitCode != 0)
                        Console.WriteLine($"  Exit Code: {status.ExitCode}");
                    
                    if (!string.IsNullOrEmpty(status.LastError))
                        Console.WriteLine($"  Last Error: {status.LastError}");
                    
                    if (!string.IsNullOrEmpty(status.RunId))
                        Console.WriteLine($"  Run ID: {status.RunId}");
                    
                    if (!string.IsNullOrEmpty(status.BootstrapUrl))
                        Console.WriteLine($"  Bootstrap URL: {status.BootstrapUrl}");
                    
                    Console.WriteLine();
                }

                // Show global version information
                Console.WriteLine("Completion Status:");
                try
                {
                    var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
                    
                    foreach (var view in views)
                    {
                        try
                        {
                            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                            using var key = baseKey.OpenSubKey(@"SOFTWARE\BootstrapMate");
                            
                            if (key != null)
                            {
                                var lastRunVersion = key.GetValue("LastRunVersion")?.ToString();
                                
                                if (!string.IsNullOrEmpty(lastRunVersion))
                                {
                                    Console.WriteLine($"  Last Run Version ({view}): {lastRunVersion}");
                                    break; // Only show once if found
                                }
                            }
                        }
                        catch
                        {
                            // Continue to next view
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️  Warning: Could not read completion information: {ex.Message}");
                }
                Console.WriteLine();

                // Show registry paths for troubleshooting
                Console.WriteLine("Registry Paths:");
                Console.WriteLine("  Completion Status: HKLM\\SOFTWARE\\BootstrapMate\\LastRunVersion");
                Console.WriteLine("  64-bit Status: HKLM\\SOFTWARE\\BootstrapMate\\Status");
                Console.WriteLine("  32-bit Status: HKLM\\SOFTWARE\\WOW6432Node\\BootstrapMate\\Status");
                Console.WriteLine();
                Console.WriteLine("Status File: C:\\ProgramData\\ManagedBootstrap\\status.json");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error retrieving status: {ex.Message}");
                return 1;
            }
        }

        static int ClearStatus()
        {
            try
            {
                Console.WriteLine("Clearing BootstrapMate status...");

                // Clear all phase statuses
                foreach (InstallationPhase phase in Enum.GetValues<InstallationPhase>())
                {
                    try
                    {
                        // Delete registry entries for this phase
                        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
                        
                        foreach (var view in views)
                        {
                            try
                            {
                                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                                baseKey.DeleteSubKeyTree($@"SOFTWARE\BootstrapMate\Status\{phase}", false);
                            }
                            catch
                            {
                                // Key might not exist, continue
                            }
                        }
                        
                        Console.WriteLine($"  [+] Cleared {phase} status");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ⚠️  Warning: Could not clear {phase} status: {ex.Message}");
                    }
                }

                // Clear version registry entry
                try
                {
                    var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
                    
                    foreach (var view in views)
                    {
                        try
                        {
                            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                            using var key = baseKey.OpenSubKey(@"SOFTWARE\BootstrapMate", true);
                            if (key != null)
                            {
                                key.DeleteValue("LastRunVersion", false);
                            }
                        }
                        catch
                        {
                            // Values might not exist, continue
                        }
                    }
                    
                    Console.WriteLine("  [+] Cleared completion registry entries");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️  Warning: Could not clear completion registry entries: {ex.Message}");
                }

                // Clear status file
                try
                {
                    var statusFile = @"C:\ProgramData\ManagedBootstrap\status.json";
                    if (File.Exists(statusFile))
                    {
                        File.Delete(statusFile);
                        Console.WriteLine("  [+] Cleared status file");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️  Warning: Could not clear status file: {ex.Message}");
                }

                Console.WriteLine("\n[+] Status cleanup completed");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error clearing status: {ex.Message}");
                return 1;
            }
        }

        static void ClearPackageCache()
        {
            try
            {
                string cacheDir = GetCacheDirectory();
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    Logger.Debug($"Cleared package cache directory: {cacheDir}");
                }
                else
                {
                    Logger.Debug($"Package cache directory does not exist: {cacheDir}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not clear package cache: {ex.Message}");
            }
        }

        static void ClearAllCachesAggressive()
        {
            try
            {
                Logger.Debug("Starting aggressive cache clearing (BootstrapMate + Chocolatey)");
                
                // 1. Clear BootstrapMate package cache
                string cacheDir = GetCacheDirectory();
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    Logger.Debug($"Aggressively cleared BootstrapMate cache: {cacheDir}");
                }
                
                // 2. Clear Chocolatey caches aggressively - all cache locations
                string[] chocolateyCachePaths = {
                    @"C:\ProgramData\chocolatey\temp",
                    @"C:\ProgramData\chocolatey\lib-bad", 
                    @"C:\ProgramData\chocolatey\logs",
                    @"C:\Users\" + Environment.UserName + @"\AppData\Local\Temp\chocolatey"
                };
                
                foreach (string cachePath in chocolateyCachePaths)
                {
                    try
                    {
                        if (Directory.Exists(cachePath))
                        {
                            Directory.Delete(cachePath, true);
                            Logger.Debug($"Aggressively cleared Chocolatey cache: {cachePath}");
                            
                            // Recreate essential directories
                            if (cachePath.EndsWith("temp"))
                            {
                                Directory.CreateDirectory(cachePath);
                                Logger.Debug($"Recreated essential cache directory: {cachePath}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Could not clear Chocolatey cache {cachePath}: {ex.Message}");
                    }
                }
                
                // 3. Run chocolatey cache clear command if available
                try
                {
                    string? chocoPath = FindChocolateyExecutable();
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = chocoPath,
                        Arguments = "cache clear --all --force --yes",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    
                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit(10000); // 10 second timeout
                        if (process.ExitCode == 0)
                        {
                            Logger.Debug("Successfully ran 'choco cache clear --all --force'");
                        }
                        else
                        {
                            Logger.Debug($"choco cache clear returned exit code: {process.ExitCode}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Could not run choco cache clear: {ex.Message}");
                }
                
                Logger.Info("Aggressive cache clearing completed");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Aggressive cache clearing failed: {ex.Message}");
            }
        }

        static void CleanupOldCache(TimeSpan maxAge)
        {
            try
            {
                string cacheDir = GetCacheDirectory();
                if (!Directory.Exists(cacheDir))
                {
                    return; // No cache directory exists
                }

                var cutoffTime = DateTime.Now - maxAge;
                var files = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
                int cleanedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTime < cutoffTime)
                        {
                            File.Delete(file);
                            cleanedCount++;
                            Logger.Debug($"Cleaned old cache file: {Path.GetFileName(file)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Could not delete old cache file {file}: {ex.Message}");
                    }
                }

                // Try to remove empty directories
                try
                {
                    var directories = Directory.GetDirectories(cacheDir, "*", SearchOption.AllDirectories);
                    foreach (var dir in directories.OrderByDescending(d => d.Length)) // Delete deepest first
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            {
                                Directory.Delete(dir);
                                Logger.Debug($"Removed empty cache directory: {dir}");
                            }
                        }
                        catch
                        {
                            // Ignore errors when removing empty directories
                        }
                    }
                }
                catch
                {
                    // Ignore directory cleanup errors
                }

                if (cleanedCount > 0)
                {
                    Logger.Debug($"Cleaned up {cleanedCount} old cache files (older than {maxAge.TotalDays:F1} days)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not cleanup old cache files: {ex.Message}");
            }
        }

        static int ClearCache()
        {
            try
            {
                Console.WriteLine("Aggressively clearing all caches (BootstrapMate + Chocolatey)...");
                
                // Use the aggressive cache clearing method
                ClearAllCachesAggressive();
                
                Console.WriteLine("[+] All caches cleared aggressively");
                Logger.Info("Manual aggressive cache clearing completed");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error clearing caches: {ex.Message}");
                Logger.Error($"Error clearing caches: {ex.Message}");
                return 1;
            }
        }

        static int ResetChocolatey(bool silentMode = false)
        {
            try
            {
                Console.WriteLine("Resetting Chocolatey (complete cleanup)...");
                Console.WriteLine("WARNING: This will remove ALL Chocolatey packages and force a clean reinstall.");
                Console.WriteLine();

                // Confirm with the user, but only when there is a user to answer.
                // Under --silent, or with stdin redirected (scheduled task, remote
                // session, MDM script), the prompt would block on input that never
                // arrives and the caller would see a timeout instead of a result.
                bool canPrompt = !silentMode && !Console.IsInputRedirected;
                if (canPrompt)
                {
                    Console.Write("Are you sure you want to completely reset Chocolatey? (y/N): ");
                    var response = Console.ReadLine()?.Trim().ToLowerInvariant();

                    if (response != "y" && response != "yes")
                    {
                        Console.WriteLine("Chocolatey reset cancelled.");
                        return ExitSuccess;
                    }
                }
                else
                {
                    Logger.Info("Non-interactive session detected (--silent or redirected stdin) - proceeding with Chocolatey reset without confirmation");
                    if (!silentMode)
                        Console.WriteLine("Non-interactive session - proceeding without confirmation.");
                }
                
                Logger.Info("Starting complete Chocolatey reset");
                
                string chocolateyRoot = @"C:\ProgramData\chocolatey";
                int removedItems = 0;
                
                if (Directory.Exists(chocolateyRoot))
                {
                    Console.WriteLine($"[*] Removing Chocolatey directory: {chocolateyRoot}");
                    
                    try
                    {
                        // Try to stop any running chocolatey processes first
                        var chocoProcesses = Process.GetProcessesByName("choco");
                        foreach (var proc in chocoProcesses)
                        {
                            try
                            {
                                Console.WriteLine($"[*] Terminating chocolatey process (PID: {proc.Id})");
                                proc.Kill();
                                proc.WaitForExit(5000);
                            }
                            catch
                            {
                                // Ignore errors killing processes
                            }
                        }
                        
                        // Remove the entire chocolatey directory
                        Directory.Delete(chocolateyRoot, true);
                        removedItems++;
                        Console.WriteLine($"[+] Removed Chocolatey directory");
                        Logger.Info($"Removed Chocolatey directory: {chocolateyRoot}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error removing Chocolatey directory: {ex.Message}");
                        Logger.Error($"Error removing Chocolatey directory: {ex.Message}");
                        
                        // Try to remove just the lib directory if full removal fails
                        try
                        {
                            string libDir = Path.Combine(chocolateyRoot, "lib");
                            if (Directory.Exists(libDir))
                            {
                                Directory.Delete(libDir, true);
                                Console.WriteLine($"[+] Removed Chocolatey lib directory (partial cleanup)");
                                Logger.Info($"Removed Chocolatey lib directory: {libDir}");
                                removedItems++;
                            }
                        }
                        catch (Exception libEx)
                        {
                            Console.WriteLine($"❌ Error removing Chocolatey lib directory: {libEx.Message}");
                            Logger.Error($"Error removing Chocolatey lib directory: {libEx.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"INFO: Chocolatey directory does not exist: {chocolateyRoot}");
                }
                
                // Clean up environment variables
                try
                {
                    Console.WriteLine("[*] Cleaning up Chocolatey environment variables");
                    
                    // Remove ChocolateyInstall environment variable
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null, EnvironmentVariableTarget.Machine);
                    Environment.SetEnvironmentVariable("ChocolateyInstall", null, EnvironmentVariableTarget.User);
                    
                    // Clean PATH environment variables (remove chocolatey paths)
                    string[] pathTargets = { "Machine", "User" };
                    foreach (string target in pathTargets)
                    {
                        try
                        {
                            var envTarget = target == "Machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
                            string currentPath = Environment.GetEnvironmentVariable("PATH", envTarget) ?? "";
                            
                            // Remove chocolatey-related paths
                            var pathParts = currentPath.Split(';')
                                .Where(p => !string.IsNullOrWhiteSpace(p) && 
                                           !p.ToLowerInvariant().Contains("chocolatey"))
                                .ToArray();
                            
                            string cleanPath = string.Join(";", pathParts);
                            Environment.SetEnvironmentVariable("PATH", cleanPath, envTarget);
                            
                            Logger.Debug($"Cleaned {target} PATH environment variable");
                        }
                        catch (Exception pathEx)
                        {
                            Logger.Warning($"Could not clean {target} PATH: {pathEx.Message}");
                        }
                    }
                    
                    Console.WriteLine($"[+] Cleaned environment variables");
                    removedItems++;
                }
                catch (Exception envEx)
                {
                    Console.WriteLine($"⚠️  Warning: Could not clean environment variables: {envEx.Message}");
                    Logger.Warning($"Could not clean environment variables: {envEx.Message}");
                }
                
                Console.WriteLine();
                if (removedItems > 0)
                {
                    Console.WriteLine($"[+] Chocolatey reset completed! Removed {removedItems} items.");
                    Console.WriteLine("    Chocolatey will be automatically reinstalled when needed.");
                    Logger.Info($"Chocolatey reset completed successfully. Removed {removedItems} items.");
                }
                else
                {
                    Console.WriteLine("INFO: No Chocolatey installation found to reset.");
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error resetting Chocolatey: {ex.Message}");
                Logger.Error($"Error resetting Chocolatey: {ex.Message}");
                return 1;
            }
        }
    }
}
