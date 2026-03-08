using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using BootstrapMate.App.ViewModels;

namespace BootstrapMate.App.Views;

public sealed partial class RunPage : Page
{
    private readonly RunViewModel _vm;
    private readonly PrefsViewModel _prefs;

    public RunPage()
    {
        InitializeComponent();

        _prefs = new PrefsViewModel();
        _prefs.Load();

        _vm = new RunViewModel(DispatcherQueue.GetForCurrentThread());
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.OutputLines.CollectionChanged += (_, _) => ScrollToBottom();
    }

    // ── Button Handlers ─────────────────────────────────────────

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning)
        {
            _vm.StopCommand.Execute(null);
        }
        else
        {
            _prefs.Load(); // Reload latest auto-saved settings from registry
            await _vm.RunCommand.ExecuteAsync(_prefs);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => _vm.ClearCommand.Execute(null);

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
        => _vm.ShowDebug = DebugToggle.IsChecked ?? false;

    // ── UI State Sync ────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunViewModel.IsRunning):
                UpdateRunningState();
                break;
            case nameof(RunViewModel.LastExitCode):
                UpdateStatusIndicator();
                UpdateResultBanner();
                break;
            case nameof(RunViewModel.FilteredLines):
                UpdateConsoleItems();
                break;
            case nameof(RunViewModel.StepCount):
            case nameof(RunViewModel.CurrentItemName):
                UpdateStepInfo();
                break;
        }
    }

    private void UpdateRunningState()
    {
        if (_vm.IsRunning)
        {
            RunIcon.Glyph = "\uE71A"; // Stop icon
            RunText.Text = "Stop";
            RunButton.Background = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            RunningProgress.IsActive = true;
            RunningLabel.Visibility = Visibility.Visible;
            ClearButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            StepProgress.IsIndeterminate = true;
            StepItemLabel.Text = "Initializing...";
            StepCountLabel.Text = "";
            ResultBanner.IsOpen = false;
        }
        else
        {
            RunIcon.Glyph = "\uE768"; // Play icon
            RunText.Text = "Run BootstrapMate";
            RunButton.Background = null; // Reset to default
            RunningProgress.IsActive = false;
            RunningLabel.Visibility = Visibility.Collapsed;
            ClearButton.Visibility = _vm.OutputLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateStatusIndicator()
    {
        if (_vm.LastExitCode is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;

        if (_vm.LastExitCode == 0)
        {
            StatusIcon.Glyph = "\uE73E"; // Checkmark
            StatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
            StatusText.Text = "Completed successfully";
            StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
        }
        else
        {
            StatusIcon.Glyph = "\uEA39"; // Error
            StatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            StatusText.Text = $"Failed (exit code {_vm.LastExitCode})";
            StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }
    }

    private void UpdateConsoleItems()
    {
        ConsoleOutput.Blocks.Clear();
        foreach (var line in _vm.FilteredLines)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run { Text = line.Text });
            paragraph.Foreground = BrushForLevel(line.Level);
            paragraph.Margin = new Thickness(0, 1, 0, 1);
            ConsoleOutput.Blocks.Add(paragraph);
        }
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
            ConsoleScroller.ChangeView(null, ConsoleScroller.ScrollableHeight, null));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private void UpdateStepInfo()
    {
        if (!string.IsNullOrEmpty(_vm.CurrentItemName))
            StepItemLabel.Text = _vm.CurrentItemName;
        if (_vm.StepCount > 0)
            StepCountLabel.Text = $"Step {_vm.StepCount}";
    }

    private void UpdateResultBanner()
    {
        if (_vm.LastExitCode is null)
        {
            ResultBanner.IsOpen = false;
            return;
        }

        ResultBanner.IsOpen = true;
        if (_vm.LastExitCode == 0)
        {
            ResultBanner.Severity = InfoBarSeverity.Success;
            ResultBanner.Title = "Completed successfully";
            ResultBanner.Message = $"{_vm.StepCount} items processed";
        }
        else
        {
            ResultBanner.Severity = InfoBarSeverity.Error;
            ResultBanner.Title = $"Failed with exit code {_vm.LastExitCode}";
            ResultBanner.Message = _vm.ErrorCount > 0
                ? $"{_vm.ErrorCount} error{(_vm.ErrorCount == 1 ? "" : "s")} encountered during {_vm.StepCount} items"
                : "Check the console output for details";
        }
    }

    private static SolidColorBrush BrushForLevel(RunViewModel.LogLevel level) => level switch
    {
        RunViewModel.LogLevel.Error   => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        RunViewModel.LogLevel.Warning => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        RunViewModel.LogLevel.Success => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        RunViewModel.LogLevel.Debug   => new SolidColorBrush(Windows.UI.Color.FromArgb(204, 128, 128, 128)),
        _ => (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"],
    };
}
