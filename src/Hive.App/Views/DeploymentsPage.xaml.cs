using Hive.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class DeploymentsPage : Page
{
    public DeploymentsViewModel ViewModel => App.DeploymentsViewModel;

    public DeploymentsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }
}
