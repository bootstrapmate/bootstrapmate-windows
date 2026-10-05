using System.Text.Json;
using System.Text.Json.Serialization;

namespace BootstrapMate.Core;

/// <summary>
/// The last baseline run's outcome, kept apart from last-run.json because every run
/// rewrites that file, a throttled one included, so it cannot hold the clock.
/// The schema matches the macOS build's baseline.json.
/// </summary>
public sealed class BaselineRecord
{
    [JsonPropertyName("end_time")] public string EndTime { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("consecutive_failures")] public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// Keeps baseline runs from repeating. Every run downloads from the package host, so a
/// machine that is already current must not baseline again on every trigger (the daily
/// Self-Heal task, an Intune reinstall or retry). The rule matches the macOS build.
/// </summary>
/// <remarks>
/// Applied only after the preflight has chosen baseline, so provisioning is never limited:
/// <list type="bullet">
/// <item>The <c>.bootstrap_force</c> file in ProgramData\ManagedBootstrap: run. The throttle
/// only looks; the preflight consumes it.</item>
/// <item>No record: run. A provisioning run clears the record.</item>
/// <item>Last baseline completed less than <c>BaselineMinIntervalHours</c> ago (default 144): skip.</item>
/// <item>Last baseline failed, partially failed or was interrupted: one retry after 24 hours;
/// if that retry fails too, the full interval applies again.</item>
/// </list>
/// A throttled run leaves the record alone, so the last real baseline stays the reference.
/// </remarks>
public static class BaselineThrottle
{
    public const int DefaultMinIntervalHours = 144;
    public const int FailedRetryHours = 24;

    public static readonly string ForceFilePath = @"C:\ProgramData\ManagedBootstrap\.bootstrap_force";
    public static readonly string DefaultRecordPath = @"C:\ProgramData\ManagedBootstrap\baseline.json";

    public sealed record Decision(bool Skip, string Reason);

    public static Decision Evaluate(BaselineRecord? last, DateTimeOffset now, int minIntervalHours, bool forced)
    {
        if (forced) return new(false, $"{ForceFilePath} is present");
        if (last is null) return new(false, "no baseline recorded");
        if (!DateTimeOffset.TryParse(last.EndTime, out var when))
            return new(false, "last baseline has no readable time");

        var age = now - when;
        bool completed = string.Equals(last.Status, RunStatuses.Completed, StringComparison.OrdinalIgnoreCase);
        var interval = TimeSpan.FromHours(Math.Max(0, minIntervalHours));
        // The first failure earns one retry a day later; once that retry has been used,
        // a failing machine waits the full interval like a healthy one.
        var wait = completed || last.ConsecutiveFailures > 1
            ? interval
            : TimeSpan.FromHours(FailedRetryHours);

        if (age < wait)
        {
            return new(true,
                $"last baseline {last.Status} {age.TotalHours:F1}h ago, under the {wait.TotalHours:F0}h minimum; " +
                $"next baseline allowed after {when + wait:yyyy-MM-dd HH:mm zzz}");
        }

        return new(false, $"last baseline {last.Status} {age.TotalHours:F1}h ago (limit {wait.TotalHours:F0}h)");
    }

    /// <summary>The record a finished baseline leaves, counting failures in a row.</summary>
    public static BaselineRecord After(BaselineRecord? previous, string status, DateTimeOffset end) => new()
    {
        EndTime = end.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        Status = status,
        ConsecutiveFailures = string.Equals(status, RunStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            ? 0
            : (previous?.ConsecutiveFailures ?? 0) + 1
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The record at <paramref name="path"/>, or null when it is absent or unreadable.</summary>
    public static BaselineRecord? Read(string? path = null)
    {
        try
        {
            var target = path ?? DefaultRecordPath;
            return File.Exists(target) ? JsonSerializer.Deserialize<BaselineRecord>(File.ReadAllText(target)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes through a temporary file and a rename. Failure is swallowed.</summary>
    public static void Write(BaselineRecord record, string? path = null)
    {
        var target = path ?? DefaultRecordPath;
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(record, WriteOptions));
            File.Move(target + ".tmp", target, overwrite: true);
        }
        catch
        {
        }
    }

    public static void Clear(string? path = null)
    {
        try { File.Delete(path ?? DefaultRecordPath); } catch { }
    }
}
