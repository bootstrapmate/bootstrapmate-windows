using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using BootstrapMate.App.ViewModels;

namespace BootstrapMate.App.Views;

public sealed partial class PrefsPage : Page
{
    public PrefsViewModel ViewModel { get; } = new();

    public PrefsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }
}
