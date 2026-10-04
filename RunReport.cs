using System;
using BootstrapMate.Core;

namespace BootstrapMate
{
    /// <summary>
    /// Keeps last-run.json in step with the run: written as "running" when the run
    /// starts, relabelled when the preflight picks a mode, and written with the
    /// outcome when the session closes.
    /// </summary>
    public static class RunReport
    {
        private static LastRunRecord? _record;
        private static readonly object _lock = new();

        public static void Start(string version)
        {
            lock (_lock)
            {
                _record = new LastRunRecord
                {
                    SessionId = Logger.GetSessionId() ?? "",
                    RunType = RunTypes.Provisioning,
                    Status = "running",
                    ToolVersion = version,
                    StartTime = Iso(Logger.SessionStartTime)
                };
                LastRunFile.Write(_record);
            }
        }

        public static void SetRunType(string runType)
        {
            Logger.SetRunType(runType);
            lock (_lock)
            {
                if (_record is null) return;
                _record.RunType = runType;
                LastRunFile.Write(_record);
            }
        }

        public static void Item(string name, string stage, string result, string? error = null)
        {
            lock (_lock)
            {
                _record?.Items.Add(new LastRunItem
                {
                    Name = name,
                    Stage = stage,
                    Result = result,
                    Error = result == ItemResults.Failed ? LastRunFile.ShortError(error) : null
                });
            }
        }

        /// <summary>
        /// Closes the session log and records the outcome. Safe to call more than once;
        /// only the first call writes.
        /// </summary>
        public static void Finish()
        {
            var outcome = Logger.WriteSessionSummary();
            if (outcome is null) return;
            lock (_lock)
            {
                if (_record is null) return;
                _record.Status = outcome.Status;
                _record.EndTime = Iso(outcome.End);
                _record.DurationSeconds = (int)Math.Round((outcome.End - Logger.SessionStartTime).TotalSeconds);
                _record.Errors = outcome.Errors;
                _record.Warnings = outcome.Warnings;
                LastRunFile.Write(_record);
            }
        }

        private static string Iso(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
    }
}
