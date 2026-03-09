using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using BootstrapMate.App.ViewModels;

namespace BootstrapMate.App.Views;

public sealed partial class PrefsPage : Page
{
    public PrefsViewModel ViewModel { get; } = new();

    public PrefsPage()
    {
        InitializeComponent();

        var iconPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "Assets", "BootstrapMate.png");
        if (System.IO.File.Exists(iconPath))
        {
            AppIcon.Source = new BitmapImage(new Uri(iconPath));
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }

    private async void PreviewManifestButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.FetchManifestPreviewAsync();
    }
}
