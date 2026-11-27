using Hive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class OverviewPage : Page
{
    public MainViewModel MainVm => App.MainViewModel;

    public OverviewPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadMyWorkstationsSummaryAsync();
    }

    private async void RefreshOverviewButton_Click(object sender, RoutedEventArgs e)
    {
        await MainVm.InitializeCommand.ExecuteAsync(null);
        await LoadMyWorkstationsSummaryAsync();
    }

    private async Task LoadMyWorkstationsSummaryAsync()
    {
        var result = await App.ApiClient.GetMyWorkstationsAsync();
        if (!result.IsSuccess || result.Value is null)
        {
            MyWorkstationsSummaryText.Text = result.ErrorMessage ?? "Workstation summary unavailable offline.";
            return;
        }

        if (result.Value.Count == 0)
        {
            MyWorkstationsSummaryText.Text = "No workstations currently assigned to your account.";
            return;
        }

        var names = string.Join(", ", result.Value.Select(w => $"{w.Hostname} ({w.ConnectivityState})"));
        MyWorkstationsSummaryText.Text = $"{result.Value.Count} assigned: {names}";
    }
}
