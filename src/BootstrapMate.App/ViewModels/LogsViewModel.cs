using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BootstrapMate.Core;

namespace BootstrapMate.App.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private static readonly string LogDirectory = BootstrapMateConstants.LogDirectory;

    public ObservableCollection<LogFile> LogFiles { get; } = [];

    [ObservableProperty] private LogFile? _selectedLog;
    [ObservableProperty] private string _logContent = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    public IEnumerable<LogLine> FilteredLines
    {
        get
        {
            var lines = LogContent.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => new LogLine(l, ColorForLine(l)));
            if (string.IsNullOrWhiteSpace(FilterText))
                return lines;
            return lines.Where(l => l.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Models ───────────────────────────────────────────────────

    public record LogFile(string Name, string Path, DateTime? Date, long SizeBytes)
    {
        public string DisplayDate => Date is { } d
            ? $"{d:MMMM} {OrdinalDay(d.Day)} {d:yyyy} at {d:HH:mm}"
            : Name;

        public string DisplayTime => Date?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

        public string DisplaySize => SizeBytes switch
        {
            < 1024        => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
            _             => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        };

        private static string OrdinalDay(int day) => (day % 10, day) switch
        {
            (1, not 11) => $"{day}st",
            (2, not 12) => $"{day}nd",
            (3, not 13) => $"{day}rd",
            _           => $"{day}th",
        };
    }

    public record LogLine(string Text, LogLineColor Color);

    public enum LogLineColor { Default, Error, Warning, Success, Debug, Header }

    // ── Refresh ──────────────────────────────────────────────────

    [RelayCommand]
    public void Refresh()
    {
        LogFiles.Clear();

        if (!Directory.Exists(LogDirectory))
            return;

        // A run is a session directory, logs\YYYY-MM-DD\HHMMSS\bootstrap.log. Flat
        // per-run files at the root predate that layout and are still listed.
        var sessions = Directory.GetDirectories(LogDirectory)
            .SelectMany(day => Directory.GetDirectories(day)
                .Select(session => (Day: System.IO.Path.GetFileName(day), Session: System.IO.Path.GetFileName(session), Path: session)))
            .Select(entry =>
            {
                var log = Directory.GetFiles(entry.Path, "*.log")
                    .OrderBy(path => System.IO.Path.GetFileName(path) == "bootstrap.log" ? 0 : 1)
                    .FirstOrDefault();
                return log is null ? null : new LogFile($"{entry.Day}-{entry.Session}", log, ParseStamp($"{entry.Day}-{entry.Session}"), FileSize(log));
            })
            .OfType<LogFile>();

        var loose = Directory.GetFiles(LogDirectory, "*.log")
            .Select(path =>
            {
                var name = System.IO.Path.GetFileName(path);
                return new LogFile(name, path, ParseStamp(System.IO.Path.GetFileNameWithoutExtension(name)), FileSize(path));
            });

        var files = sessions.Concat(loose)
            .OrderByDescending(f => f.Date ?? DateTime.MinValue)
            .ToList();

        foreach (var f in files)
            LogFiles.Add(f);

        // Auto-select most recent
        if (SelectedLog is null && LogFiles.Count > 0)
            SelectedLog = LogFiles[0];
    }

    /// <summary>
    /// A session or flat-file stamp, at second or minute resolution. The minute form
    /// predates seconds and still appears on a device that has not been rebuilt.
    /// </summary>
    private static DateTime? ParseStamp(string stamp)
    {
        foreach (var format in new[] { "yyyy-MM-dd-HHmmss", "yyyy-MM-dd-HHmm" })
        {
            if (DateTime.TryParseExact(stamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
        }
        return null;
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ── Load Content ─────────────────────────────────────────────

    partial void OnSelectedLogChanged(LogFile? value)
    {
        if (value is null)
        {
            LogContent = string.Empty;
            return;
        }

        try
        {
            // Use FileShare.ReadWrite so we can read logs that are being written
            using var fs = new FileStream(value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            LogContent = reader.ReadToEnd();
        }
        catch
        {
            LogContent = "Unable to read log file.";
        }
    }

    partial void OnLogContentChanged(string value) => OnPropertyChanged(nameof(FilteredLines));
    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredLines));

    // ── Actions ──────────────────────────────────────────────────

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedLog is null) return;
        Process.Start(new ProcessStartInfo(SelectedLog.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!Directory.Exists(LogDirectory)) return;
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static LogLineColor ColorForLine(string line)
    {
        if (HasLevel(line, "ERROR") || line.Contains("[X]")) return LogLineColor.Error;
        if (HasLevel(line, "WARN") || line.Contains("[!]")) return LogLineColor.Warning;
        if (line.Contains("[+]")) return LogLineColor.Success;
        if (HasLevel(line, "DEBUG") || line.Contains("[DBG]")) return LogLineColor.Debug;
        if (line.StartsWith("===") || line.Contains("] ===")) return LogLineColor.Header;
        return LogLineColor.Default;
    }

    /// <summary>
    /// Logger.FormatLine writes "[timestamp] LEVEL message" with an unbracketed,
    /// space-padded level token (INFO/WARN/ERROR/DEBUG), so matching "[WARNING]"
    /// never fired and warnings rendered as ordinary lines. Match the token the
    /// logger actually emits; the [X]/[!]/[+] markers below are the console form.
    /// </summary>
    private static bool HasLevel(string line, string level)
    {
        var close = line.IndexOf("] ", StringComparison.Ordinal);
        if (close < 0) return false;
        var rest = line.AsSpan(close + 2).TrimStart();
        return rest.StartsWith(level, StringComparison.Ordinal)
            && (rest.Length == level.Length || rest[level.Length] == ' ');
    }
}
