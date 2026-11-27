using Hive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class WorkstationDetailPage : Page
{
    public WorkstationDetailViewModel ViewModel => App.WorkstationDetailViewModel;

    public WorkstationDetailPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (!App.MainViewModel.CanNavigateTo(MainViewModel.PageKeys.WorkstationDetail))
        {
            App.MainViewModel.ReportPermissionDenied(MainViewModel.PageKeys.WorkstationDetail);
            App.MainWindowInstance?.NavigateTo(MainViewModel.PageKeys.Overview);
            return;
        }

        if (e.Parameter is Guid deviceId && deviceId != Guid.Empty)
        {
            await ViewModel.LoadAsync(deviceId);
        }
        else if (App.WorkstationsViewModel.SelectedWorkstation is { } selected)
        {
            await ViewModel.LoadAsync(selected.DeviceId);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.NavigateTo(MainViewModel.PageKeys.Workstations);
    }
}
