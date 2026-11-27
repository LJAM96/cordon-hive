using Hive.App.Services;
using Hive.App.ViewModels;
using Hive.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class SettingsPage : Page
{
    private sealed class AssignedWorkstationOption
    {
        public AssignedWorkstationOption(DeviceSummaryDto dto)
        {
            Dto = dto;
            DisplayLabel = string.IsNullOrWhiteSpace(dto.AssetNumber)
                ? $"{dto.Hostname} ({dto.SiteId})"
                : $"{dto.Hostname} ({dto.SiteId} · {dto.AssetNumber})";
        }

        public DeviceSummaryDto Dto { get; }
        public string DisplayLabel { get; }

        public override string ToString() => DisplayLabel;
    }

    private bool _isLoadingWorkstations;
    private bool _isSubmittingEscrow;

    public MainViewModel MainVm => App.MainViewModel;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ServerUrlTextBox.Text = App.Settings.BaseUrl;
        SettingsStatusText.Text = $"Current LAN endpoint: {App.Settings.BaseUrl}";
        AccountStatusText.Text = MainVm.IsSignedIn
            ? $"Active session: {MainVm.SignedInUserSummary}"
            : "No active authenticated session.";

        await LoadAssignedWorkstationsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ClearEscrowPasswordInputs();
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
            await LoadAssignedWorkstationsAsync();
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await MainVm.SignOutCommand.ExecuteAsync(null);
        AccountStatusText.Text = "Signed out. Local DPAPI session cleared.";
        ClearEscrowPasswordInputs();
        await LoadAssignedWorkstationsAsync();
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

        await LoadAssignedWorkstationsAsync();
    }

    private async void RefreshEscrowWorkstationsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAssignedWorkstationsAsync();
    }

    private async void EscrowWorkstationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingWorkstations)
        {
            return;
        }

        if (EscrowWorkstationComboBox.SelectedItem is not AssignedWorkstationOption option)
        {
            EscrowMetadataText.Text = string.Empty;
            return;
        }

        await RefreshSelectedEscrowMetadataAsync(option.Dto.DeviceId);
    }

    private async void SaveEscrowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmittingEscrow)
        {
            return;
        }

        if (!MainVm.IsSignedIn)
        {
            EscrowStatusText.Text = "Sign in before submitting break-glass credentials.";
            return;
        }

        if (EscrowWorkstationComboBox.SelectedItem is not AssignedWorkstationOption selected)
        {
            EscrowStatusText.Text = "Select one of your assigned workstations first.";
            return;
        }

        var username = EscrowUsernameTextBox.Text?.Trim() ?? string.Empty;
        var password = EscrowPasswordBox.Password ?? string.Empty;
        var confirmPassword = EscrowConfirmPasswordBox.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            EscrowStatusText.Text = "Workstation login username is required.";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            EscrowStatusText.Text = "Workstation password is required.";
            return;
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            EscrowStatusText.Text = "Workstation password and confirmation do not match.";
            return;
        }

        _isSubmittingEscrow = true;
        UpdateEscrowControlsEnabled();
        EscrowStatusText.Text = $"Encrypting and saving break-glass credential for {selected.Dto.Hostname}...";

        try
        {
            var result = await App.ApiClient.UpsertEscrowCredentialAsync(
                selected.Dto.DeviceId,
                new UpsertEscrowCredentialRequest(username, password));

            if (!result.IsSuccess || result.Value is null)
            {
                EscrowStatusText.Text = result.ErrorMessage ?? "Unable to save break-glass credential.";
                return;
            }

            var updatedAt = result.Value.UpdatedAtUtc?.ToLocalTime().ToString("g") ?? DateTimeOffset.Now.ToString("g");
            EscrowMetadataText.Text = $"Status: Escrowed ({result.Value.AccountUsername}, updated {updatedAt})";
            EscrowStatusText.Text = $"Saved write-only break-glass credential for {selected.Dto.Hostname}. Password cleared from form.";
        }
        finally
        {
            ClearEscrowPasswordInputs();
            _isSubmittingEscrow = false;
            UpdateEscrowControlsEnabled();
        }
    }

    private async Task LoadAssignedWorkstationsAsync()
    {
        _isLoadingWorkstations = true;
        try
        {
            EscrowWorkstationComboBox.Items.Clear();
            EscrowMetadataText.Text = string.Empty;

            if (!MainVm.IsSignedIn)
            {
                EscrowStatusText.Text = "Sign in to manage break-glass credentials for your assigned workstations.";
                UpdateEscrowControlsEnabled();
                return;
            }

            EscrowStatusText.Text = "Loading your assigned workstations...";
            UpdateEscrowControlsEnabled();

            var result = await App.ApiClient.GetMyWorkstationsAsync();
            if (!result.IsSuccess || result.Value is null)
            {
                EscrowStatusText.Text = result.ErrorMessage ?? "Unable to load assigned workstations.";
                UpdateEscrowControlsEnabled();
                return;
            }

            foreach (var device in result.Value.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                EscrowWorkstationComboBox.Items.Add(new AssignedWorkstationOption(device));
            }

            if (EscrowWorkstationComboBox.Items.Count == 0)
            {
                EscrowMetadataText.Text = "No workstations are currently assigned to your account.";
                EscrowStatusText.Text = "Ask a supervisor to assign your workstation before escrowing a credential.";
                UpdateEscrowControlsEnabled();
                return;
            }

            EscrowWorkstationComboBox.SelectedIndex = 0;
            EscrowStatusText.Text = $"Loaded {EscrowWorkstationComboBox.Items.Count} assigned workstation(s).";
        }
        finally
        {
            _isLoadingWorkstations = false;
            UpdateEscrowControlsEnabled();
        }

        if (EscrowWorkstationComboBox.SelectedItem is AssignedWorkstationOption selected)
        {
            await RefreshSelectedEscrowMetadataAsync(selected.Dto.DeviceId);
        }
    }

    private async Task RefreshSelectedEscrowMetadataAsync(Guid deviceId)
    {
        if (!MainVm.IsSignedIn)
        {
            EscrowMetadataText.Text = string.Empty;
            return;
        }

        EscrowMetadataText.Text = "Checking current escrow status...";
        var statusResult = await App.ApiClient.GetEscrowCredentialStatusAsync(deviceId);
        if (!statusResult.IsSuccess || statusResult.Value is null)
        {
            EscrowMetadataText.Text = statusResult.ErrorKind == ApiErrorKind.Forbidden
                ? "Status check restricted for this device."
                : "Status: Unavailable (write-only update still available).";
            return;
        }

        var status = statusResult.Value;
        if (status.HasEscrowedCredential)
        {
            var updatedText = status.UpdatedAtUtc is { } utc
                ? utc.ToLocalTime().ToString("g")
                : "unknown time";
            var account = string.IsNullOrWhiteSpace(status.AccountUsername) ? "unknown user" : status.AccountUsername;
            EscrowMetadataText.Text = $"Status: Escrowed ({account}, updated {updatedText})";

            if (string.IsNullOrWhiteSpace(EscrowUsernameTextBox.Text) &&
                !string.IsNullOrWhiteSpace(status.AccountUsername))
            {
                EscrowUsernameTextBox.Text = status.AccountUsername;
            }
        }
        else
        {
            EscrowMetadataText.Text = "Status: Not Escrowed";
        }
    }

    private void ClearEscrowPasswordInputs()
    {
        EscrowPasswordBox.Password = string.Empty;
        EscrowConfirmPasswordBox.Password = string.Empty;
    }

    private void UpdateEscrowControlsEnabled()
    {
        var signedIn = MainVm.IsSignedIn;
        var hasSelection = signedIn && EscrowWorkstationComboBox.Items.Count > 0;

        RefreshEscrowWorkstationsButton.IsEnabled = signedIn && !_isLoadingWorkstations && !_isSubmittingEscrow;
        EscrowWorkstationComboBox.IsEnabled = hasSelection && !_isSubmittingEscrow;
        EscrowUsernameTextBox.IsEnabled = hasSelection && !_isSubmittingEscrow;
        EscrowPasswordBox.IsEnabled = hasSelection && !_isSubmittingEscrow;
        EscrowConfirmPasswordBox.IsEnabled = hasSelection && !_isSubmittingEscrow;
        SaveEscrowButton.IsEnabled = hasSelection && !_isSubmittingEscrow;
    }
}
