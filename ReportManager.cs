using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using BootstrapMate.Core;

namespace BootstrapMate
{
    /// <summary>
    /// Posts a vendor-neutral run summary to an optional reporting endpoint when a
    /// bootstrap run completes, turning "did this PC provision cleanly?" into a
    /// fleet-dashboard query instead of an RDP/registry expedition.
    ///
    /// The payload is plain JSON and intentionally NOT specific to any one backend
    /// (ReportMate, MunkiReport, a custom collector, …). Windows counterpart of the
    /// macOS ReportManager; emits the same schema.
    /// </summary>
    public static class ReportManager
    {
        /// <summary>
        /// Build and POST the run summary to the configured reporting endpoint, if any.
        /// Best-effort: failures are logged and never abort the run.
        /// </summary>
        public static async Task SendRunSummaryAsync(bool success, DateTime startTimeUtc, string version, string manifestUrl)
        {
            var config = ConfigManager.Instance.Config;
            var url = config.ReportingUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                // Use the manifest URL actually used for this run (which may come
                // from --url), not whatever happens to be in config.
                var payload = BuildPayload(success, startTimeUtc, DateTime.UtcNow, version, manifestUrl);
                var json = JsonSerializer.Serialize(payload);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"BootstrapMate/{version}");
                if (!string.IsNullOrWhiteSpace(config.ReportingHeader))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", config.ReportingHeader);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                Logger.Info($"Posting run summary to reporting endpoint: {url}");
                using var response = await httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                    Logger.Info($"Run summary reported (HTTP {(int)response.StatusCode})");
                else
                    Logger.Warning($"Reporting endpoint returned HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Reporting POST failed: {ex.Message}");
            }
        }

        /// <summary>Assemble the vendor-neutral summary. Mirrors the macOS schema.</summary>
        public static Dictionary<string, object?> BuildPayload(
            bool success, DateTime startTimeUtc, DateTime endTimeUtc, string version, string manifestUrl)
        {
            return new Dictionary<string, object?>
            {
                ["tool"] = "BootstrapMate",
                ["platform"] = "Windows",
                ["schemaVersion"] = 1,
                ["version"] = version,
                ["runId"] = StatusManager.GetCurrentRunId(),
                ["success"] = success,
                ["startTime"] = startTimeUtc.ToString("o"),
                ["endTime"] = endTimeUtc.ToString("o"),
                ["durationSeconds"] = (int)Math.Round((endTimeUtc - startTimeUtc).TotalSeconds),
                ["architecture"] = CurrentArchitecture(),
                ["hostname"] = Environment.MachineName,
                ["serialNumber"] = SerialNumber() ?? "",
                ["manifestUrl"] = manifestUrl,
                ["phases"] = CollectPhases()
            };
        }

        private static Dictionary<string, object?> CollectPhases()
        {
            var phases = new Dictionary<string, object?>();
            foreach (var phase in new[] { InstallationPhase.SetupAssistant, InstallationPhase.Userland })
            {
                var status = StatusManager.GetPhaseStatus(phase);
                phases[phase.ToString()] = new Dictionary<string, object?>
                {
                    ["stage"] = status.Stage.ToString(),
                    ["exitCode"] = status.ExitCode,
                    ["startTime"] = status.StartTime,
                    ["completionTime"] = status.CompletionTime,
                    ["lastError"] = status.LastError
                };
            }
            return phases;
        }

        private static string CurrentArchitecture()
            => RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "ARM64",
                Architecture.X64 => "X64",
                Architecture.X86 => "X86",
                var other => other.ToString().ToUpperInvariant()
            };

        /// <summary>Best-effort hardware serial number from the SMBIOS registry key.</summary>
        private static string? SerialNumber()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return key?.GetValue("SystemSerialNumber") as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
