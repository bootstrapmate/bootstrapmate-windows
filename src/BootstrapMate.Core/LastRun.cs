using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BootstrapMate.Core;

/// <summary>
/// One run's outcome, kept at a fixed path so a reader (an Intune remediation
/// script, a support tech) can tell how the last run went without walking the
/// session logs. The schema matches the macOS build.
/// </summary>
public sealed class LastRunRecord
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("run_type")] public string RunType { get; set; } = "provisioning";
    [JsonPropertyName("status")] public string Status { get; set; } = "running";
    [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "";
    [JsonPropertyName("start_time")] public string StartTime { get; set; } = "";
    [JsonPropertyName("end_time")] public string? EndTime { get; set; }
    [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }
    [JsonPropertyName("errors")] public int Errors { get; set; }
    [JsonPropertyName("warnings")] public int Warnings { get; set; }
    [JsonPropertyName("items")] public List<LastRunItem> Items { get; set; } = new();
}

public sealed class LastRunItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Values the record uses, shared with session.json.</summary>
public static class RunTypes
{
    public const string Provisioning = "provisioning";
    public const string Baseline = "baseline";
    public const string Skip = "skip";

    public static string For(PreflightDecision decision) => decision switch
    {
        PreflightDecision.Skip => Skip,
        PreflightDecision.Baseline => Baseline,
        // A failed preflight stays provisioning: the run was not given a mode.
        _ => Provisioning
    };
}

public static class ItemResults
{
    public const string Installed = "installed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class LastRunFile
{
    public static readonly string DefaultPath = @"C:\ProgramData\ManagedBootstrap\last-run.json";

    /// <summary>The longest line <see cref="FormatLine"/> returns.</summary>
    public const int MaxLineLength = 1000;

    /// <summary>The longest item error kept in the record.</summary>
    public const int MaxErrorLength = 200;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Writes the record through a temporary file and a rename, so a reader never
    /// sees half a file. Failure is swallowed: reporting must never stop a run.
    /// </summary>
    public static void Write(LastRunRecord record, string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, WriteOptions));
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
        }
    }

    /// <summary>The record at <paramref name="path"/>, or null when it is absent or unreadable.</summary>
    public static LastRunRecord? Read(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            if (!File.Exists(target)) return null;
            return JsonSerializer.Deserialize<LastRunRecord>(File.ReadAllText(target));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First line of an error, trimmed to <see cref="MaxErrorLength"/>.</summary>
    public static string ShortError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "";
        var line = error.Trim().Split('\n')[0].Trim();
        return line.Length <= MaxErrorLength ? line : line[..(MaxErrorLength - 3)] + "...";
    }

    /// <summary>
    /// One line for a remediation script's output:
    /// <c>&lt;time&gt; &lt;run_type&gt; &lt;status&gt; v&lt;version&gt; installed=N skipped=N failed=N[: name: error; ...]</c>.
    /// The time is the run's end (its start while it is still running), ISO 8601 to
    /// the minute with the UTC offset. Never longer than <see cref="MaxLineLength"/>.
    /// </summary>
    public static string FormatLine(LastRunRecord? record)
    {
        if (record is null) return "no run recorded";

        var stamp = record.EndTime ?? record.StartTime;
        var time = DateTimeOffset.TryParse(stamp, out var parsed)
            ? parsed.ToString("yyyy-MM-ddTHH:mmzzz")
            : stamp;

        var installed = record.Items.Count(i => i.Result == ItemResults.Installed);
        var skipped = record.Items.Count(i => i.Result == ItemResults.Skipped);
        var failed = record.Items.Where(i => i.Result == ItemResults.Failed).ToList();

        var line = new StringBuilder()
            .Append($"{time} {record.RunType} {record.Status} v{record.ToolVersion} ")
            .Append($"installed={installed} skipped={skipped} failed={failed.Count}");
        if (failed.Count > 0)
        {
            line.Append(": ").Append(string.Join("; ", failed.Select(i =>
                string.IsNullOrEmpty(i.Error) ? i.Name : $"{i.Name}: {i.Error}")));
        }

        var text = line.ToString().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= MaxLineLength ? text : text[..(MaxLineLength - 3)] + "...";
    }
}
