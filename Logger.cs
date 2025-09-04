using System;
using System.IO;

namespace BootstrapMate
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        Success
    }

    public static class Logger
    {
        private static string? LogFile;
        private static bool _verboseConsole = false;
        private static bool _silentMode = false;
        
        public static void Initialize(string logDirectory, string version = "Unknown", bool verboseConsole = false, bool silentMode = false)
        {
            try
            {
                _verboseConsole = verboseConsole;
                _silentMode = silentMode;
                
                // Ensure log directory exists
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                LogFile = Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
                
                // Write session header to log file
                WriteToFile("=== BootstrapMate Session Started ===");
                WriteToFile($"Version: {version}");
                WriteToFile($"Process ID: {Environment.ProcessId}");
                WriteToFile($"User: {Environment.UserName}");
                WriteToFile($"Machine: {Environment.MachineName}");
                WriteToFile($"OS: {Environment.OSVersion}");
                WriteToFile($"Process Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                WriteToFile($"OS Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
                WriteToFile($"Working Directory: {Environment.CurrentDirectory}");
                WriteToFile($"Command Line: {Environment.CommandLine}");
                WriteToFile($"Is Interactive: {Environment.UserInteractive}");
                WriteToFile($"Current User: {System.Security.Principal.WindowsIdentity.GetCurrent().Name}");
                WriteToFile($"Verbose Console: {verboseConsole}");
                WriteToFile($"Silent Mode: {silentMode}");
            }
            catch (Exception ex)
            {
                if (!_silentMode)
                {
                    Console.WriteLine($"Warning: Could not initialize logging: {ex.Message}");
                }
            }
        }

        public static void Debug(string message)
        {
            Log(LogLevel.Debug, message);
        }

        public static void Info(string message)
        {
            Log(LogLevel.Info, message);
        }

        public static void Warning(string message)
        {
            Log(LogLevel.Warning, message);
        }

        public static void Error(string message)
        {
            Log(LogLevel.Error, message);
        }

        public static void Success(string message)
        {
            Log(LogLevel.Success, message);
        }

        private static void Log(LogLevel level, string message)
        {
            // Always write to log file with full detail
            WriteToFile($"[{level}] {message}");

            // Write to console based on level and verbose setting
            WriteToConsole(level, message);
        }

        private static void WriteToFile(string message)
        {
            if (string.IsNullOrEmpty(LogFile)) return;
            
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] {message}";
                File.AppendAllText(LogFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Silent fail for file logging to not disrupt main process
            }
        }

        private static void WriteToConsole(LogLevel level, string message)
        {
            // Skip console output in silent mode
            if (_silentMode)
                return;
                
            // Only show debug messages in verbose mode
            if (level == LogLevel.Debug && !_verboseConsole)
                return;

            // Get appropriate icon and color for the message
            var (icon, color) = GetDisplayFormat(level);
            
            // Set console color if supported
            var originalColor = Console.ForegroundColor;
            try
            {
                if (color.HasValue)
                    Console.ForegroundColor = color.Value;
                
                Console.WriteLine($"{icon} {message}");
                Console.Out.Flush();
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        private static (string icon, ConsoleColor? color) GetDisplayFormat(LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => ("[DBG]", ConsoleColor.Gray),
                LogLevel.Info => ("[i]", null),
                LogLevel.Warning => ("[!]", ConsoleColor.Yellow),
                LogLevel.Error => ("[X]", ConsoleColor.Red),
                LogLevel.Success => ("[+]", ConsoleColor.Green),
                _ => ("•", null)
            };
        }

        // User-facing output methods that write to both log file and console (unless silent)
        public static void WriteHeader(string title)
        {
            WriteToFile($"=== {title} ===");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"══ {title} ══");
        }

        public static void WriteSection(string section)
        {
            WriteToFile($"[SECTION] {section}");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"[>] {section}");
        }

        public static void WriteProgress(string operation, string item)
        {
            WriteToFile($"[PROGRESS] {operation}: {item}");
            if (_silentMode) return;
            Console.WriteLine($"   [*] {operation}: {item}");
        }

        public static void WriteSubProgress(string status, string details = "")
        {
            var message = string.IsNullOrEmpty(details) ? status : $"{status}: {details}";
            WriteToFile($"[SUB-PROGRESS] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      • {message}");
        }

        public static void WriteSuccess(string message)
        {
            WriteToFile($"[SUCCESS] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [+] {message}");
        }

        public static void WriteWarning(string message)
        {
            WriteToFile($"[WARNING] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [!] {message}");
        }

        public static void WriteError(string message)
        {
            WriteToFile($"[ERROR] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [X] {message}");
        }

        public static void WriteSkipped(string message)
        {
            WriteToFile($"[SKIPPED] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [-] {message}");
        }

        public static void WriteCompletion(string message)
        {
            WriteToFile($"[COMPLETION] {message}");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"[+] {message}");
            Console.WriteLine();
        }

        // Convenience method for complex operations with timing
        public static void LogOperation(string operation, Action action)
        {
            var startTime = DateTime.Now;
            Debug($"Starting operation: {operation}");
            
            try
            {
                action();
                var duration = DateTime.Now - startTime;
                Debug($"Completed operation: {operation} (took {duration.TotalSeconds:F1}s)");
            }
            catch (Exception ex)
            {
                var duration = DateTime.Now - startTime;
                Error($"Failed operation: {operation} after {duration.TotalSeconds:F1}s - {ex.Message}");
                throw;
            }
        }

        // Get the current log file path for external reference
        public static string? GetLogFilePath()
        {
            return LogFile;
        }
    }
}
