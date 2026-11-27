using Hive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel MainVm => App.MainViewModel;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ServerUrlTextBox.Text = App.Settings.BaseUrl;
        SettingsStatusText.Text = $"Current LAN endpoint: {App.Settings.BaseUrl}";
        AccountStatusText.Text = MainVm.IsSignedIn
            ? $"Active session: {MainVm.SignedInUserSummary}"
            : "No active authenticated session.";
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is null)
        {
            return;
        }

        var signedIn = await App.MainWindowInstance.ShowSignInDialogAsync();
        if (signedIn)
        {
            AccountStatusText.Text = $"Signed in: {MainVm.SignedInUserSummary}";
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await MainVm.SignOutCommand.ExecuteAsync(null);
        AccountStatusText.Text = "Signed out. Local DPAPI session cleared.";
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is null)
        {
            return;
        }

        var updated = await App.MainWindowInstance.ShowChangePasswordDialogAsync();
        if (updated)
        {
            AccountStatusText.Text = "Password updated successfully.";
        }
    }

    private async void ApplyAndReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Settings.TrySetBaseUrl(ServerUrlTextBox.Text, out var error))
        {
            SettingsStatusText.Text = error ?? "Invalid URL.";
            return;
        }

        SettingsStatusText.Text = $"Updated endpoint to {App.Settings.BaseUrl}. Reconnecting...";
        await App.MainViewModel.InitializeCommand.ExecuteAsync(null);
        SettingsStatusText.Text = $"Endpoint: {App.Settings.BaseUrl} — {App.MainViewModel.BannerTitle}";
        AccountStatusText.Text = MainVm.IsSignedIn
            ? $"Active session: {MainVm.SignedInUserSummary}"
            : MainVm.BannerMessage;
    }
}
