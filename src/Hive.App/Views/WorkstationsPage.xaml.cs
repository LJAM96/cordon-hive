using Hive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class WorkstationsPage : Page
{
    public WorkstationsViewModel ViewModel => App.WorkstationsViewModel;

    public WorkstationsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void ViewDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWorkstation is { } selected)
        {
            App.MainWindowInstance?.NavigateToWorkstationDetail(selected.DeviceId);
        }
    }
}
