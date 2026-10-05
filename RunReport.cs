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
                RecoverInterruptedRun();
                _record = new LastRunRecord
                {
                    SessionId = Logger.GetSessionId() ?? "",
                    RunType = RunTypes.Provisioning,
                    Status = RunStatuses.Running,
                    ToolVersion = version,
                    StartTime = Iso(Logger.SessionStartTime)
                };
                LastRunFile.Write(_record);
            }
        }

        /// <summary>
        /// A last-run.json still at "running" belongs to a run that died: this run holds the
        /// single-instance lock, so none other is going. Its session is relabelled
        /// "interrupted", and so is the baseline record, which lets the next baseline run
        /// straight away: the retry comes from the next normal trigger, never a relaunch.
        /// </summary>
        private static void RecoverInterruptedRun()
        {
            var orphan = LastRunFile.MarkInterrupted(LastRunFile.Read(), Logger.LogDirectory);
            if (orphan is null) return;

            Logger.Warning($"Previous run {orphan.SessionId} ({orphan.RunType}, started {orphan.StartTime}) " +
                           "never finished; recorded as interrupted");
            if (string.Equals(orphan.RunType, RunTypes.Baseline, StringComparison.OrdinalIgnoreCase))
            {
                var when = DateTimeOffset.TryParse(orphan.StartTime, out var started) ? started : DateTimeOffset.Now;
                BaselineThrottle.Write(BaselineThrottle.After(BaselineThrottle.Read(), RunStatuses.Interrupted, when, orphan.ToolVersion));
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
        /// only the first call writes. <paramref name="status"/> overrides the status the
        /// session would derive from its error count.
        /// </summary>
        public static void Finish(string? status = null)
        {
            var outcome = Logger.WriteSessionSummary(status);
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

                // The baseline clock: a baseline sets it, a provisioning run clears it (a
                // machine provisioned again starts over), a skip leaves it alone.
                if (_record.RunType == RunTypes.Baseline)
                    BaselineThrottle.Write(BaselineThrottle.After(BaselineThrottle.Read(), outcome.Status, new DateTimeOffset(outcome.End), _record.ToolVersion));
                else if (_record.RunType == RunTypes.Provisioning && outcome.Status == RunStatuses.Completed)
                    BaselineThrottle.Clear();
            }
        }

        private static string Iso(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
    }
}
