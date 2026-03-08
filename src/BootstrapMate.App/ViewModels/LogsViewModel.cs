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
            ? $"{d:MMMM} {OrdinalDay(d.Day)} {d:yyyy}"
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

        var files = Directory.GetFiles(LogDirectory, "*.log")
            .Select(path =>
            {
                var name = System.IO.Path.GetFileName(path);
                var baseName = System.IO.Path.GetFileNameWithoutExtension(name);
                DateTime? date = DateTime.TryParseExact(baseName, "yyyy-MM-dd-HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                return new LogFile(name, path, date, size);
            })
            .OrderByDescending(f => f.Date ?? DateTime.MinValue)
            .ToList();

        foreach (var f in files)
            LogFiles.Add(f);

        // Auto-select most recent
        if (SelectedLog is null && LogFiles.Count > 0)
            SelectedLog = LogFiles[0];
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
        if (line.Contains("[Error]") || line.Contains("[ERROR]") || line.Contains("[X]")) return LogLineColor.Error;
        if (line.Contains("[Warning]") || line.Contains("[WARNING]") || line.Contains("[!]")) return LogLineColor.Warning;
        if (line.Contains("[Success]") || line.Contains("[SUCCESS]") || line.Contains("[+]")) return LogLineColor.Success;
        if (line.Contains("[Debug]") || line.Contains("[DEBUG]") || line.Contains("[DBG]")) return LogLineColor.Debug;
        if (line.StartsWith("===") || line.Contains("] ===")) return LogLineColor.Header;
        return LogLineColor.Default;
    }
}
