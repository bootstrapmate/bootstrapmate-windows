using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BootstrapMate
{
    /// <summary>
    /// csharpdialog integration for user-facing progress UI during enrollment.
    /// Gracefully degrades to headless mode if csharpdialog is not available.
    /// </summary>
    public class DialogManager : IDisposable
    {
        private static DialogManager? _instance;
        private static readonly object _lock = new object();

        // csharpdialog paths
        private readonly string _dialogPath = @"C:\Program Files\csharpDialog\dialog.exe";
        private readonly string _defaultCommandFile;
        
        // State
        private Process? _dialogProcess;
        private string _commandFilePath;
        private bool _isAvailable;
        private bool _isRunning;
        private int _totalItems;
        private int _completedItems;
        private bool _disposed;

        public static DialogManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new DialogManager();
                    }
                }
                return _instance;
            }
        }

        private DialogManager()
        {
            _defaultCommandFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ManagedBootstrap", "dialog_commands.txt"
            );
            _commandFilePath = _defaultCommandFile;
            _isAvailable = File.Exists(_dialogPath);

            if (!_isAvailable)
            {
                Logger.Debug($"csharpdialog not found at {_dialogPath} - running in headless mode");
            }
        }

        /// <summary>
        /// Check if csharpdialog is available
        /// </summary>
        public bool IsDialogAvailable() => _isAvailable;

        /// <summary>
        /// Initialize and launch the dialog window
        /// </summary>
        public void Initialize(
            string title,
            string message,
            int totalPackages,
            string? icon = null,
            bool fullScreen = false,
            bool kioskMode = false)
        {
            if (!_isAvailable)
            {
                Logger.Debug("Dialog not available, skipping initialization");
                return;
            }

            // Reset state
            _totalItems = totalPackages;
            _completedItems = 0;

            // Ensure command file directory exists
            var commandDir = Path.GetDirectoryName(_commandFilePath);
            if (!string.IsNullOrEmpty(commandDir) && !Directory.Exists(commandDir))
            {
                Directory.CreateDirectory(commandDir);
            }

            // Clear command file
            ClearCommandFile();

            // Build dialog arguments
            var arguments = new StringBuilder();
            arguments.Append($"--title \"{title}\" ");
            arguments.Append($"--message \"{message}\" ");
            arguments.Append("--progressbar ");
            arguments.Append("--progress 0 ");
            arguments.Append("--progresstext \"Preparing...\" ");
            arguments.Append($"--commandfile \"{_commandFilePath}\" ");
            arguments.Append("--button1text \"Please Wait\" ");

            if (!string.IsNullOrEmpty(icon))
            {
                arguments.Append($"--icon \"{icon}\" ");
            }

            if (fullScreen)
            {
                arguments.Append("--fullscreen ");
            }

            if (kioskMode)
            {
                arguments.Append("--kiosk ");
            }

            // Launch dialog process
            LaunchDialog(arguments.ToString().Trim());
        }

        /// <summary>
        /// Add a new list item with initial status
        /// </summary>
        public void AddListItem(string name, DialogStatus status = DialogStatus.Pending, string statusText = "")
        {
            var command = BuildListItemCommand("add", name, status, statusText);
            WriteCommand(command);
            Logger.Debug($"Dialog: Added list item '{name}' with status {status}");
        }

        /// <summary>
        /// Update an existing list item's status
        /// </summary>
        public void UpdateListItem(string name, DialogStatus status, string statusText = "")
        {
            var command = BuildListItemCommand("update", name, status, statusText);
            WriteCommand(command);

            // Track completion for progress
            if (status == DialogStatus.Success || status == DialogStatus.Fail)
            {
                _completedItems++;
                var progressPercent = _totalItems > 0 ? (_completedItems * 100) / _totalItems : 0;
                UpdateProgress(progressPercent);
            }
        }

        /// <summary>
        /// Update the progress bar value (0-100)
        /// </summary>
        public void UpdateProgress(int percent)
        {
            WriteCommand($"progress: {Math.Clamp(percent, 0, 100)}");
        }

        /// <summary>
        /// Update the progress bar text
        /// </summary>
        public void UpdateProgressText(string text)
        {
            WriteCommand($"progresstext: {text}");
        }

        /// <summary>
        /// Update the dialog title
        /// </summary>
        public void UpdateTitle(string title)
        {
            WriteCommand($"title: {title}");
        }

        /// <summary>
        /// Update the dialog message
        /// </summary>
        public void UpdateMessage(string message)
        {
            WriteCommand($"message: {message}");
        }

        /// <summary>
        /// Mark progress as complete
        /// </summary>
        public void Complete(string message = "Setup Complete")
        {
            UpdateProgressText(message);
            WriteCommand("progress: 100");
            WriteCommand("button1text: Done");
            Logger.Info("Dialog: Marked as complete");
        }

        /// <summary>
        /// Close the dialog window
        /// </summary>
        public void Close()
        {
            if (!_isRunning) return;

            WriteCommand("quit");

            // Give dialog time to close gracefully
            System.Threading.Thread.Sleep(500);
            TerminateDialog();
        }

        /// <summary>
        /// Force terminate the dialog (for error scenarios)
        /// </summary>
        public void TerminateDialog()
        {
            if (!_isRunning || _dialogProcess == null) return;

            try
            {
                if (!_dialogProcess.HasExited)
                {
                    _dialogProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error terminating dialog: {ex.Message}");
            }
            finally
            {
                _dialogProcess?.Dispose();
                _dialogProcess = null;
                _isRunning = false;
                Logger.Debug("Dialog process terminated");
            }
        }

        #region Convenience Methods for Package Processing

        /// <summary>
        /// Helper for package download phase
        /// </summary>
        public void NotifyDownloadStarted(string packageName)
        {
            UpdateListItem(packageName, DialogStatus.Wait, "Downloading...");
            UpdateProgressText($"Downloading {packageName}...");
        }

        /// <summary>
        /// Helper for package installation phase
        /// </summary>
        public void NotifyInstallStarted(string packageName)
        {
            UpdateListItem(packageName, DialogStatus.Wait, "Installing...");
            UpdateProgressText($"Installing {packageName}...");
        }

        /// <summary>
        /// Helper for package success
        /// </summary>
        public void NotifyPackageSuccess(string packageName)
        {
            UpdateListItem(packageName, DialogStatus.Success, "Installed");
        }

        /// <summary>
        /// Helper for package failure
        /// </summary>
        public void NotifyPackageFailure(string packageName, string error)
        {
            UpdateListItem(packageName, DialogStatus.Fail, error);
        }

        /// <summary>
        /// Helper for package skipped
        /// </summary>
        public void NotifyPackageSkipped(string packageName, string reason = "Already installed")
        {
            UpdateListItem(packageName, DialogStatus.Success, reason);
        }

        /// <summary>
        /// Helper for phase transition
        /// </summary>
        public void NotifyPhaseStarted(string phase)
        {
            UpdateProgressText($"Phase: {phase}");
            Logger.WriteSection($"Processing {phase} packages");
        }

        #endregion

        #region Private Methods

        private void LaunchDialog(string arguments)
        {
            if (_isRunning)
            {
                Logger.Warning("Dialog already running, skipping launch");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _dialogPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                _dialogProcess = Process.Start(startInfo);
                
                if (_dialogProcess != null)
                {
                    _isRunning = true;
                    Logger.Info("csharpdialog launched successfully");
                }
                else
                {
                    Logger.Error("Failed to start csharpdialog process");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch csharpdialog: {ex.Message}");
                _isRunning = false;
            }
        }

        private void WriteCommand(string command)
        {
            if (!_isAvailable || !_isRunning) return;

            try
            {
                // Append command with newline
                File.AppendAllText(_commandFilePath, command + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to write dialog command: {ex.Message}");
            }
        }

        private void ClearCommandFile()
        {
            try
            {
                File.WriteAllText(_commandFilePath, string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to clear command file: {ex.Message}");
            }
        }

        private string BuildListItemCommand(string action, string title, DialogStatus status, string statusText)
        {
            var command = $"listitem: {action}, title: {title}, status: {status.ToString().ToLowerInvariant()}";
            if (!string.IsNullOrEmpty(statusText))
            {
                command += $", statustext: {statusText}";
            }
            return command;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                TerminateDialog();
            }

            _disposed = true;
        }

        ~DialogManager()
        {
            Dispose(false);
        }

        #endregion
    }

    /// <summary>
    /// Dialog status indicators matching csharpdialog/SwiftDialog
    /// </summary>
    public enum DialogStatus
    {
        None,
        Pending,
        Wait,
        Success,
        Fail,
        Error,
        Progress
    }
}
