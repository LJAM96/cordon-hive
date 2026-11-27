using Hive.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel => App.UpdatesViewModel;

    public UpdatesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }
}
