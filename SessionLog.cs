using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BootstrapMate
{
    /// <summary>
    /// The structured half of a run's logs.
    /// </summary>
    /// <remarks>
    /// Every run owns a session directory under the tool's logs root,
    /// <c>C:\ProgramData\ManagedBootstrap\logs\YYYY-MM-DD\HHMMSS\</c>, holding
    /// bootstrap.log (the human log, written by <see cref="Logger"/>), events.jsonl
    /// (one JSON record per line, appended as the run proceeds) and session.json
    /// (the run as a whole, written when it starts and rewritten when it ends).
    /// The layout and field names match Cimian's session logger and the macOS
    /// BootstrapMate's, so the same readers work on every managed tool.
    /// </remarks>
    public sealed class SessionLog
    {
        /// <summary>Session directories kept across all days, newest first.</summary>
        public const int MaxSessions = 100;

        private static readonly JsonSerializerOptions EventOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions SessionOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string SessionId { get; }
        public string SessionDirectory { get; }
        public string LogFilePath { get; }

        private readonly DateTime _startTime;
        private readonly string _runType;
        private readonly string _version;
        private readonly string _eventsPath;
        private readonly object _writeLock = new();
        private int _eventIndex;
        private int _errors;
        private int _warnings;
        private int _events;

        private SessionLog(string sessionDirectory, string sessionId, DateTime start, string runType, string version)
        {
            SessionDirectory = sessionDirectory;
            SessionId = sessionId;
            LogFilePath = Path.Combine(sessionDirectory, "bootstrap.log");
            _startTime = start;
            _runType = runType;
            _version = version;
            _eventsPath = Path.Combine(sessionDirectory, "events.jsonl");
            WriteSessionFile("running");
        }

        /// <summary>
        /// Creates <c>logs\YYYY-MM-DD\HHMMSS\</c>, appending <c>_2</c> through <c>_9</c>
        /// when a previous run started in the same second. Returns null when the directory
        /// cannot be created, which leaves the caller to fall back to a flat file.
        /// </summary>
        public static SessionLog? Create(string logsDirectory, string version, string runType, DateTime start)
        {
            try
            {
                var day = start.ToString("yyyy-MM-dd");
                var time = start.ToString("HHmmss");
                var dayDirectory = Path.Combine(logsDirectory, day);
                var directory = Path.Combine(dayDirectory, time);
                var name = time;

                if (Directory.Exists(directory))
                {
                    var placed = false;
                    for (var suffix = 2; suffix <= 9; suffix++)
                    {
                        var candidate = Path.Combine(dayDirectory, $"{time}_{suffix}");
                        if (Directory.Exists(candidate)) continue;
                        directory = candidate;
                        name = $"{time}_{suffix}";
                        placed = true;
                        break;
                    }
                    if (!placed) return null;
                }

                Directory.CreateDirectory(directory);
                return new SessionLog(directory, $"{day}-{name}", start, runType, version);
            }
            catch
            {
                // A session directory is a convenience; a run must never fail over one.
                return null;
            }
        }

        /// <summary>
        /// Appends one record to events.jsonl and keeps the run's counts. <paramref name="message"/>
        /// is the same text the human log carries, with any leading <c>[TAG]</c> lifted into the
        /// event's type and status.
        /// </summary>
        public void Append(string level, string message, DateTime timestamp)
        {
            var (eventType, status, text) = Classify(level, message);
            lock (_writeLock)
            {
                if (level == "ERROR") _errors++;
                else if (level == "WARN") _warnings++;
                _events++;
                _eventIndex++;

                var record = new SessionEvent
                {
                    EventId = $"{SessionId}-{_eventIndex:D5}",
                    SessionId = SessionId,
                    Timestamp = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                    Level = level,
                    EventType = eventType,
                    Status = status,
                    Message = text,
                    Error = level == "ERROR" ? text : null
                };

                try
                {
                    File.AppendAllText(_eventsPath, JsonSerializer.Serialize(record, EventOptions) + Environment.NewLine);
                }
                catch
                {
                    // Structured logging is best-effort and must never stop a run.
                }
            }
        }

        /// <summary>Rewrites session.json with the run's outcome.</summary>
        public void Finish(string? status = null, DateTime? end = null)
        {
            var finished = end ?? DateTime.Now;
            var resolved = status ?? (_errors > 0 ? "partial_failure" : "completed");
            WriteSessionFile(resolved, finished);
        }

        private void WriteSessionFile(string status, DateTime? end = null)
        {
            var record = new SessionRecord
            {
                SessionId = SessionId,
                StartTime = _startTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                EndTime = end?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                DurationSeconds = end.HasValue ? (int)Math.Round((end.Value - _startTime).TotalSeconds) : null,
                RunType = _runType,
                Status = status,
                ToolVersion = _version,
                Environment = new Dictionary<string, string>
                {
                    ["hostname"] = System.Environment.MachineName,
                    ["os_version"] = System.Environment.OSVersion.ToString(),
                    ["user"] = System.Environment.UserName,
                    ["pid"] = System.Environment.ProcessId.ToString(),
                    ["command_line"] = System.Environment.CommandLine
                },
                Summary = new SessionSummary { Events = _events, Errors = _errors, Warnings = _warnings }
            };

            try
            {
                File.WriteAllText(Path.Combine(SessionDirectory, "session.json"),
                    JsonSerializer.Serialize(record, SessionOptions));
            }
            catch
            {
                // Best-effort, as above.
            }
        }

        /// <summary>
        /// Lifts a leading <c>[TAG]</c> off a message into an event type and status, so the
        /// structured stream carries what the human log carries in prose. An unrecognised
        /// bracket is left in the message rather than invented into a type.
        /// </summary>
        internal static (string EventType, string? Status, string Message) Classify(string level, string message)
        {
            var fallbackType = level == "ERROR" ? "error" : "message";
            var fallbackStatus = level == "ERROR" ? "FAILED" : null;
            if (!message.StartsWith('[')) return (fallbackType, fallbackStatus, message);
            var close = message.IndexOf(']');
            if (close < 0) return (fallbackType, fallbackStatus, message);

            var tag = message.Substring(1, close - 1).ToUpperInvariant();
            var text = message[(close + 1)..].TrimStart(' ');
            return tag switch
            {
                "SECTION" => ("section", null, text),
                "PROGRESS" or "SUB-PROGRESS" => ("progress", "PROGRESS", text),
                "SUCCESS" => ("item", "SUCCESS", text),
                "SKIPPED" => ("item", "SKIPPED", text),
                "COMPLETION" => ("session_end", "SUCCESS", text),
                "OUTPUT" => ("output", null, text),
                _ => (fallbackType, fallbackStatus, message)
            };
        }

        /// <summary>
        /// Removes day directories older than the retention window, then the oldest session
        /// directories beyond the cap. Loose per-run files left at the logs root by the flat
        /// layout this replaced are swept separately, by the same age rule, in
        /// <see cref="Logger.PruneExpiredLogs"/>.
        /// </summary>
        internal static int Prune(string logsDirectory, int retentionDays, DateTime now)
        {
            var removed = 0;
            try
            {
                if (!Directory.Exists(logsDirectory)) return 0;
                var cutoff = now.AddDays(-retentionDays).Date;
                var dayDirectories = Directory.GetDirectories(logsDirectory)
                    .Select(path => (Path: path, Name: Path.GetFileName(path)))
                    .Where(entry => DateTime.TryParseExact(entry.Name, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    .OrderByDescending(entry => entry.Name, StringComparer.Ordinal)
                    .ToList();

                var surviving = new List<(string Path, string Name)>();
                foreach (var entry in dayDirectories)
                {
                    var day = DateTime.ParseExact(entry.Name, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (day < cutoff)
                    {
                        try { Directory.Delete(entry.Path, recursive: true); removed++; } catch { }
                    }
                    else
                    {
                        surviving.Add(entry);
                    }
                }

                var sessions = surviving
                    .SelectMany(entry => Directory.GetDirectories(entry.Path)
                        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
                    .ToList();
                foreach (var path in sessions.Skip(MaxSessions))
                {
                    try { Directory.Delete(path, recursive: true); removed++; } catch { }
                }
            }
            catch
            {
                // Retention is best-effort and must never stop a bootstrap run.
            }
            return removed;
        }

        private sealed class SessionEvent
        {
            [JsonPropertyName("event_id")] public string EventId { get; set; } = "";
            [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
            [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
            [JsonPropertyName("level")] public string Level { get; set; } = "";
            [JsonPropertyName("event_type")] public string EventType { get; set; } = "";
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("message")] public string Message { get; set; } = "";
            [JsonPropertyName("error")] public string? Error { get; set; }
        }

        private sealed class SessionSummary
        {
            [JsonPropertyName("events")] public int Events { get; set; }
            [JsonPropertyName("errors")] public int Errors { get; set; }
            [JsonPropertyName("warnings")] public int Warnings { get; set; }
        }

        private sealed class SessionRecord
        {
            [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
            [JsonPropertyName("start_time")] public string StartTime { get; set; } = "";
            [JsonPropertyName("end_time")] public string? EndTime { get; set; }
            [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }
            [JsonPropertyName("run_type")] public string RunType { get; set; } = "";
            [JsonPropertyName("status")] public string Status { get; set; } = "";
            [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "";
            [JsonPropertyName("environment")] public Dictionary<string, string> Environment { get; set; } = new();
            [JsonPropertyName("summary")] public SessionSummary Summary { get; set; } = new();
        }
    }
}
