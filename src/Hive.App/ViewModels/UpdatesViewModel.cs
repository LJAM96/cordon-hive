using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly LocalAgentIpcClient _ipcClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(EnableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProtectionLockCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableLock))]
    [NotifyPropertyChangedFor(nameof(CanDisableLock))]
    [NotifyPropertyChangedFor(nameof(CanToggleLock))]
    [NotifyCanExecuteChangedFor(nameof(EnableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProtectionLockCommand))]
    public partial bool IsIpcAvailable { get; set; }

    [ObservableProperty]
    public partial string IpcStatusTitle { get; set; } = "Checking Local Agent IPC...";

    [ObservableProperty]
    public partial string IpcStatusMessage { get; set; } = $"Connecting to {LocalAgentIpcClient.DefaultPipeName}...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectedModeHeadline))]
    [NotifyPropertyChangedFor(nameof(ProtectedModeGlyph))]
    [NotifyPropertyChangedFor(nameof(ToggleActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanEnableLock))]
    [NotifyPropertyChangedFor(nameof(CanDisableLock))]
    [NotifyCanExecuteChangedFor(nameof(EnableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProtectionLockCommand))]
    public partial bool ProtectedModeActive { get; set; }

    [ObservableProperty]
    public partial string ReasonCode { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string LockedBySid { get; set; } = "None";

    [ObservableProperty]
    public partial string UpdatedUtcDisplay { get; set; } = "Never";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableLock))]
    [NotifyCanExecuteChangedFor(nameof(EnableProtectionLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleProtectionLockCommand))]
    public partial string ReasonCodeInput { get; set; } = "forensic_examination_active";

    [ObservableProperty]
    public partial string LastRefreshedLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial bool IsFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity FeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string FeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackMessage { get; set; } = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public bool CanEnableLock =>
        !IsBusy &&
        IsIpcAvailable &&
        !ProtectedModeActive &&
        !string.IsNullOrWhiteSpace(ReasonCodeInput);

    public bool CanDisableLock =>
        !IsBusy &&
        IsIpcAvailable &&
        ProtectedModeActive;

    public bool CanToggleLock =>
        !IsBusy &&
        IsIpcAvailable &&
        (ProtectedModeActive || !string.IsNullOrWhiteSpace(ReasonCodeInput));

    public string ProtectedModeHeadline => !IsIpcAvailable
        ? "UNKNOWN — Fail-Closed (Local Agent Unreachable)"
        : ProtectedModeActive
            ? "ACTIVE — Protected Forensic Work Mode"
            : "INACTIVE — Standard Workload Safety Guard";

    public string ProtectedModeGlyph => !IsIpcAvailable
        ? "\uE7BA"
        : ProtectedModeActive
            ? "\uE72E"
            : "\uE785";

    public string ToggleActionLabel => ProtectedModeActive
        ? "Disable Protection Lock"
        : "Enable Protection Lock";

    public string FailClosedPolicySummary =>
        "Fail-closed deployment guarantee: Software deployments are automatically deferred whenever Protected Forensic Work Mode is active, local protection state is unknown, a protected forensic process is running, system disk free space is below the safety threshold (max of 2 GiB or 3x package size), or no valid server lease exists. Cordon Hive never terminates active forensic workloads and never triggers automatic reboots.";

    public string SignedInAccountSummary => _mainViewModel.CurrentUser is { } user
        ? $"Signed in: {user.DisplayName} ({user.UserSid})"
        : "Local IPC works even when central server is offline";

    public UpdatesViewModel(LocalAgentIpcClient ipcClient, MainViewModel mainViewModel)
    {
        _ipcClient = ipcClient;
        _mainViewModel = mainViewModel;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEnableLock));
        OnPropertyChanged(nameof(CanDisableLock));
        OnPropertyChanged(nameof(CanToggleLock));
    }

    partial void OnIsIpcAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ProtectedModeHeadline));
        OnPropertyChanged(nameof(ProtectedModeGlyph));
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _ipcClient.GetProtectionLockAsync();
            ApplyIpcResult(result);
            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
            OnPropertyChanged(nameof(SignedInAccountSummary));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnableLock))]
    private Task EnableProtectionLockAsync() => SetProtectionLockAsync(enable: true);

    [RelayCommand(CanExecute = nameof(CanDisableLock))]
    private Task DisableProtectionLockAsync() => SetProtectionLockAsync(enable: false);

    [RelayCommand(CanExecute = nameof(CanToggleLock))]
    private Task ToggleProtectionLockAsync() => SetProtectionLockAsync(enable: !ProtectedModeActive);

    public async Task SetProtectionLockAsync(bool enable)
    {
        var effectiveReason = string.IsNullOrWhiteSpace(ReasonCodeInput)
            ? (enable ? "manual_forensic_lock" : "manual_forensic_lock_cleared")
            : ReasonCodeInput.Trim();

        if (enable && string.IsNullOrWhiteSpace(ReasonCodeInput))
        {
            SetFeedback(
                InfoBarSeverity.Warning,
                "Reason Code Required",
                "Enter a reason code before enabling Protected Forensic Work Mode.");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _ipcClient.SetProtectionLockAsync(enable, effectiveReason);
            ApplyIpcResult(result);

            if (!result.IsAvailable || result.Value is null)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Protection Lock Update Failed",
                    result.StatusMessage);
                return;
            }

            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
            SetFeedback(
                InfoBarSeverity.Success,
                result.Value.ProtectedModeActive ? "Protection Lock Enabled" : "Protection Lock Disabled",
                result.Value.ProtectedModeActive
                    ? $"Protected Forensic Work Mode is active ({result.Value.ReasonCode}). Deployments will defer safely without terminating active workloads or rebooting."
                    : $"Protected Forensic Work Mode is inactive ({result.Value.ReasonCode}). Standard Workload Safety Guard checks remain enforced.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyIpcResult(LocalIpcResult<IpcProtectionLockResponse> result)
    {
        if (!result.IsAvailable || result.Value is null)
        {
            IsIpcAvailable = false;
            ProtectedModeActive = false;
            IpcStatusTitle = "Local Agent IPC Unavailable (Fail-Closed)";
            IpcStatusMessage = result.StatusMessage;
            ReasonCode = "protection_state_unknown";
            LockedBySid = "Unavailable";
            UpdatedUtcDisplay = "Unavailable";
            return;
        }

        var state = result.Value;
        IsIpcAvailable = true;
        ProtectedModeActive = state.ProtectedModeActive;
        IpcStatusTitle = "Local Agent Connected (IPC)";
        IpcStatusMessage = $"Connected to {LocalAgentIpcClient.DefaultPipeName}.";
        ReasonCode = string.IsNullOrWhiteSpace(state.ReasonCode) ? "none" : state.ReasonCode;
        LockedBySid = string.IsNullOrWhiteSpace(state.LockedBySid) ? "Not locked" : state.LockedBySid;
        UpdatedUtcDisplay = state.UpdatedUtc is { } updated
            ? $"{updated.ToLocalTime():g} (UTC {updated:u})"
            : "Never";

        if (state.ProtectedModeActive && !string.IsNullOrWhiteSpace(state.ReasonCode))
        {
            ReasonCodeInput = state.ReasonCode;
        }
    }

    private void SetFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackSeverity = severity;
        FeedbackTitle = title;
        FeedbackMessage = message;
        IsFeedbackOpen = true;
    }
}
