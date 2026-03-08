using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BootstrapMate.App.Views;

namespace BootstrapMate.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "BootstrapMate";

        // DPI-aware sizing: ensure 1100x750 logical pixels regardless of display scaling
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(750 * scale)));

        // Extend content into title bar for seamless theme-matching appearance
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Apply Mica backdrop for modern Windows 11 look
        SystemBackdrop = new MicaBackdrop();

        // Select the first tab on launch
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            var pageType = tag switch
            {
                "prefs" => typeof(PrefsPage),
                "run"   => typeof(RunPage),
                "logs"  => typeof(LogsPage),
                _       => typeof(PrefsPage)
            };
            ContentFrame.Navigate(pageType);
        }
    }
}
