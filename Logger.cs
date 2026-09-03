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
        private static DateTime _sessionStartTime;
        private static TextWriter? _pipeWriter;

        /// <summary>
        /// Sets an additional output writer (e.g. named pipe) that receives all log lines.
        /// Used by the GUI app to stream real-time output.
        /// </summary>
        public static void SetPipeWriter(TextWriter? writer) => _pipeWriter = writer;
        
        public static void Initialize(string logDirectory, string version = "Unknown", bool verboseConsole = false, bool silentMode = false)
        {
            try
            {
                _verboseConsole = verboseConsole;
                _silentMode = silentMode;
                _sessionStartTime = DateTime.Now;
                
                // Ensure log directory exists
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                LogFile = Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd-HHmmss}.log");

                PruneExpiredLogs(logDirectory);

                // Write session header to log file
                WriteToFile(LogLevel.Info, "=== BootstrapMate Session Started ===");
                WriteToFile(LogLevel.Info, $"Version: {version}");
                WriteToFile(LogLevel.Info, $"Session Start Time: {_sessionStartTime:yyyy-MM-dd HH:mm:ss.fff}");
                WriteToFile(LogLevel.Info, $"Process ID: {Environment.ProcessId}");
                WriteToFile(LogLevel.Info, $"User: {Environment.UserName}");
                WriteToFile(LogLevel.Info, $"Machine: {Environment.MachineName}");
                WriteToFile(LogLevel.Info, $"OS: {Environment.OSVersion}");
                WriteToFile(LogLevel.Info, $"Process Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                WriteToFile(LogLevel.Info, $"OS Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
                WriteToFile(LogLevel.Info, $"Working Directory: {Environment.CurrentDirectory}");
                WriteToFile(LogLevel.Info, $"Command Line: {Environment.CommandLine}");
                WriteToFile(LogLevel.Info, $"Is Interactive: {Environment.UserInteractive}");
                WriteToFile(LogLevel.Info, $"Current User: {System.Security.Principal.WindowsIdentity.GetCurrent().Name}");
                WriteToFile(LogLevel.Info, $"Verbose Console: {verboseConsole}");
                WriteToFile(LogLevel.Info, $"Silent Mode: {silentMode}");
            }
            catch (Exception ex)
            {
                if (!_silentMode)
                {
                    Console.WriteLine($"Warning: Could not initialize logging: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Deletes logs in <paramref name="logDirectory"/> older than the retention
        /// window. Every run writes a new timestamped file and nothing has ever removed
        /// one, so on a machine that has been enrolled for a year the directory holds
        /// hundreds of them.
        /// </summary>
        /// <remarks>
        /// Age comes from the file's last write time rather than its name: the directory
        /// also collects logs written by wrapper scripts, which do not follow the
        /// timestamped naming, and those need expiring too.
        /// </remarks>
        internal static void PruneExpiredLogs(string logDirectory, int? retentionDays = null)
        {
            var days = retentionDays ?? BootstrapMate.Core.BootstrapMateConstants.LogRetentionDays;
            if (days <= 0)
            {
                return;
            }

            try
            {
                var cutoff = DateTime.Now.AddDays(-days);

                foreach (var file in Directory.GetFiles(logDirectory, "*.log"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime < cutoff)
                        {
                            info.Delete();
                        }
                    }
                    catch
                    {
                        // A log still held open elsewhere throws here. Skip it and try
                        // again next run rather than aborting the sweep.
                    }
                }
            }
            catch
            {
                // Retention is best-effort and must never stop a bootstrap run.
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
            WriteToFile(level, message);

            // Write to console based on level and verbose setting
            WriteToConsole(level, message);

            // Write to pipe for GUI streaming
            WriteToPipe(level, message);
        }

        /// <summary>
        /// Formats one log-file line: <c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c> in local
        /// time, with the level left-aligned in a five-character column. The level
        /// vocabulary in the file is DEBUG, INFO, WARN and ERROR only; every other
        /// classification is written as INFO with any marker carried in the message.
        /// </summary>
        internal static string FormatLine(LogLevel level, string message, DateTime timestamp)
        {
            return $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {FileLevel(level),-5} {message}";
        }

        private static string FileLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                _ => "INFO"
            };
        }

        /// <summary>
        /// Strips ANSI colour sequences. A package's own output is written for a terminal
        /// and carries escape codes that nothing renders once they are in a log file.
        /// </summary>
        internal static string StripDecoration(string message)
            => System.Text.RegularExpressions.Regex.Replace(message, @"\x1b\[[0-9;]*m", string.Empty);

        /// <summary>
        /// Writes one stamped line per line of <paramref name="message"/>.
        /// </summary>
        /// <remarks>
        /// A multi-line message used to be written with a single stamp on the front, so only
        /// its first line carried a timestamp and level and the rest landed in the log as bare
        /// text. That is how a package's captured stdout ended up sitting unstamped between
        /// two properly formatted lines. Blank lines are dropped: captured output is full of
        /// them and they carry nothing.
        /// </remarks>
        private static void WriteToFile(LogLevel level, string message)
        {
            if (string.IsNullOrEmpty(LogFile)) return;

            try
            {
                var now = DateTime.Now;
                var builder = new System.Text.StringBuilder();
                foreach (var line in StripDecoration(message).Split('\n'))
                {
                    var text = line.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    builder.Append(FormatLine(level, text, now)).Append(Environment.NewLine);
                }

                if (builder.Length > 0)
                    File.AppendAllText(LogFile, builder.ToString());
            }
            catch
            {
                // Silent fail for file logging to not disrupt main process
            }
        }

        /// <summary>
        /// Records output captured from a package's own process: stdout at INFO, stderr at
        /// WARN, one stamped line each, tagged so a reader can tell the package's words from
        /// BootstrapMate's own. The tag is uppercase in brackets at the start of the message,
        /// matching [PROGRESS] and [SUCCESS], which the log viewer renders as a pill.
        /// </summary>
        public static void WriteCapturedOutput(string package, string output, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(output)) return;
            var level = isError ? LogLevel.Warning : LogLevel.Info;
            foreach (var line in StripDecoration(output).Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(text)) continue;
                WriteToFile(level, $"[OUTPUT] {package}: {text}");
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

        private static void WriteToPipe(LogLevel level, string message)
        {
            if (_pipeWriter is null) return;
            try
            {
                var (icon, _) = GetDisplayFormat(level);
                _pipeWriter.WriteLine($"{icon} {message}");
            }
            catch
            {
                // Pipe broken — silently stop writing
                _pipeWriter = null;
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
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            WriteToFile(LogLevel.Info, $"=== {title} === (Started: {timestamp})");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"══ {title} ══");
            Console.WriteLine($"Started: {timestamp}");
        }

        public static void WriteSection(string section)
        {
            WriteToFile(LogLevel.Info, $"[SECTION] {section}");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"[>] {section}");
        }

        public static void WriteProgress(string operation, string item)
        {
            WriteToFile(LogLevel.Info, $"[PROGRESS] {operation}: {item}");
            if (_silentMode) return;
            Console.WriteLine($"   [*] {operation}: {item}");
        }

        public static void WriteSubProgress(string status, string details = "")
        {
            var message = string.IsNullOrEmpty(details) ? status : $"{status}: {details}";
            WriteToFile(LogLevel.Info, $"[SUB-PROGRESS] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      • {message}");
        }

        public static void WriteSuccess(string message)
        {
            WriteToFile(LogLevel.Info, $"[SUCCESS] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [+] {message}");
        }

        public static void WriteWarning(string message)
        {
            WriteToFile(LogLevel.Warning, message);
            if (_silentMode) return;
            Console.WriteLine($"      [!] {message}");
        }

        public static void WriteError(string message)
        {
            WriteToFile(LogLevel.Error, message);
            if (_silentMode) return;
            Console.WriteLine($"      [X] {message}");
        }

        public static void WriteSkipped(string message)
        {
            WriteToFile(LogLevel.Info, $"[SKIPPED] {message}");
            if (_silentMode) return;
            Console.WriteLine($"      [-] {message}");
        }

        public static void WriteCompletion(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var duration = DateTime.Now - _sessionStartTime;
            WriteToFile(LogLevel.Info, $"[COMPLETION] {message} (Completed: {timestamp}, Total Duration: {duration.TotalSeconds:F1}s)");
            if (_silentMode) return;
            Console.WriteLine();
            Console.WriteLine($"[+] {message}");
            Console.WriteLine($"Completed: {timestamp}");
            Console.WriteLine($"Total Duration: {duration.TotalMinutes:F1} minutes ({duration.TotalSeconds:F1} seconds)");
            Console.WriteLine();
        }

        // Convenience method for complex operations with timing
        public static void LogOperation(string operation, Action action)
        {
            var startTime = DateTime.Now;
            Debug($"Starting operation: {operation} at {startTime:yyyy-MM-dd HH:mm:ss.fff}");
            
            try
            {
                action();
                var duration = DateTime.Now - startTime;
                var endTime = DateTime.Now;
                Debug($"Completed operation: {operation} at {endTime:yyyy-MM-dd HH:mm:ss.fff} (took {duration.TotalSeconds:F1}s)");
            }
            catch (Exception ex)
            {
                var duration = DateTime.Now - startTime;
                var endTime = DateTime.Now;
                Error($"Failed operation: {operation} at {endTime:yyyy-MM-dd HH:mm:ss.fff} after {duration.TotalSeconds:F1}s - {ex.Message}");
                throw;
            }
        }

        // Get the current log file path for external reference
        public static string? GetLogFilePath()
        {
            return LogFile;
        }

        // Get the current session duration
        public static TimeSpan GetSessionDuration()
        {
            return DateTime.Now - _sessionStartTime;
        }

        // Write session summary with total duration
        public static void WriteSessionSummary()
        {
            var duration = GetSessionDuration();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            WriteToFile(LogLevel.Info, $"=== BootstrapMate Session Ended === (Duration: {duration.TotalSeconds:F1}s)");
            WriteToFile(LogLevel.Info, $"Session End Time: {timestamp}");
            WriteToFile(LogLevel.Info, $"Total Session Duration: {duration.TotalMinutes:F2} minutes");
        }
    }
}
