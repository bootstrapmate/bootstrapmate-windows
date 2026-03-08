using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace BootstrapMate.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BootstrapMate", "startup-crash.log");

    private Window? _window;

    public App()
    {
        this.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log($"UnhandledException: {e.Exception?.GetType()?.FullName}\n{e.Exception?.Message}\n{e.Exception?.StackTrace}\nInner: {e.Exception?.InnerException?.GetType()?.FullName}: {e.Exception?.InnerException?.Message}");
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log($"OnLaunched exception: {ex.GetType()?.FullName}\n{ex.Message}\n{ex.StackTrace}\nInner: {ex.InnerException?.GetType()?.FullName}: {ex.InnerException?.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{message}\n");
        }
        catch { }
    }
}
