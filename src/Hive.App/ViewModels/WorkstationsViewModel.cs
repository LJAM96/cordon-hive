using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Devices;

namespace Hive.App.ViewModels;

public enum WorkstationSortMode
{
    LastSeenDescending,
    HostnameAscending
}

public sealed class WorkstationItemViewModel
{
    public WorkstationItemViewModel(DeviceSummaryDto dto)
    {
        Dto = dto;
        Connectivity = Enum.TryParse<ConnectivityState>(dto.ConnectivityState, ignoreCase: true, out var parsed)
            ? parsed
            : DeviceConnectivityEvaluator.Evaluate(dto.LastSeenUtc, DateTimeOffset.UtcNow);
    }

    public DeviceSummaryDto Dto { get; }
    public Guid DeviceId => Dto.DeviceId;
    public string Hostname => Dto.Hostname;
    public string SiteId => Dto.SiteId;
    public string AssetNumberDisplay => string.IsNullOrWhiteSpace(Dto.AssetNumber) ? "Unassigned asset" : Dto.AssetNumber;
    public string AssignedUserDisplay => string.IsNullOrWhiteSpace(Dto.AssignedUserSid) ? "Unassigned user" : Dto.AssignedUserSid;
    public string EnrollmentState => Dto.EnrollmentState;
    public string AgentVersionDisplay => Dto.AgentVersion ?? "Unknown";
    public DateTimeOffset? LastSeenUtc => Dto.LastSeenUtc;
    public long RowVersion => Dto.RowVersion;
    public ConnectivityState Connectivity { get; }

    // Text + glyph representation (never color alone).
    public string ConnectivityText => Connectivity switch
    {
        ConnectivityState.Online => "Online",
        ConnectivityState.Stale => "Stale",
        _ => "Offline"
    };

    public string ConnectivityGlyph => Connectivity switch
    {
        ConnectivityState.Online => "\uE73E",  // CheckMark
        ConnectivityState.Stale => "\uE7BA",   // Warning
        _ => "\uE711"                          // Cancel / Offline
    };

    public string ConnectivityAccessibilityName => $"Connectivity: {ConnectivityText}, {LastSeenLocalText}";

    public string LastSeenLocalText => Dto.LastSeenUtc is { } seen
        ? $"Last seen {seen.ToLocalTime():g}"
        : "Last seen: Never";

    // Offline workstations must never be shown as 0% load.
    public string LoadStatusText => Connectivity == ConnectivityState.Offline
        ? "Load: Unavailable (offline)"
        : "Load: Select to view detail & telemetry";
}

public sealed partial class WorkstationsViewModel : ObservableObject
{
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;
    private List<DeviceSummaryDto> _allDevices = [];

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSavingAssignment { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty]
    public partial string LastRefreshedLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial WorkstationItemViewModel? SelectedWorkstation { get; set; }

    [ObservableProperty]
    public partial string EditAssetNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditAssignedUserSid { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AssignmentFeedback { get; set; } = string.Empty;

    public ObservableCollection<WorkstationItemViewModel> FilteredWorkstations { get; } = [];

    public int TotalCount => _allDevices.Count;
    public int OnlineCount => _allDevices.Count(d => ParseConnectivity(d) == ConnectivityState.Online);
    public int StaleCount => _allDevices.Count(d => ParseConnectivity(d) == ConnectivityState.Stale);
    public int OfflineCount => _allDevices.Count(d => ParseConnectivity(d) == ConnectivityState.Offline);

    public bool HasSelection => SelectedWorkstation is not null;

    public WorkstationsViewModel(HiveApiClient apiClient, MainViewModel mainViewModel)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilterAndSort();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnSelectedWorkstationChanged(WorkstationItemViewModel? value)
    {
        EditAssetNumber = value?.Dto.AssetNumber ?? string.Empty;
        EditAssignedUserSid = value?.Dto.AssignedUserSid ?? string.Empty;
        AssignmentFeedback = string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        AssignAssetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_mainViewModel.CanNavigateTo(MainViewModel.PageKeys.Workstations))
        {
            StatusMessage = "Access restricted: Supervisor, Administrator, or Auditor role required.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading workstations...";

        try
        {
            var result = await _apiClient.GetWorkstationsAsync();
            LastRefreshedLocalText = DateTimeOffset.Now.ToString("g");

            if (!result.IsSuccess || result.Value is null)
            {
                _allDevices = [];
                ApplyFilterAndSort();
                StatusMessage = result.ErrorMessage ?? "Unable to load workstations.";

                if (result.ErrorKind == ApiErrorKind.Forbidden)
                {
                    _mainViewModel.ReportBanner(
                        ConnectivityBannerState.PermissionDenied,
                        "Workstations Access Denied",
                        StatusMessage);
                }
                else if (result.ErrorKind == ApiErrorKind.Unauthorized)
                {
                    _mainViewModel.ReportBanner(
                        ConnectivityBannerState.AuthError,
                        "Authentication Required",
                        StatusMessage);
                }
                else
                {
                    _mainViewModel.ReportBanner(
                        ConnectivityBannerState.Offline,
                        "Workstations Unavailable",
                        StatusMessage);
                }
                return;
            }

            _allDevices = [.. result.Value];
            ApplyFilterAndSort();
            StatusMessage = $"Loaded {_allDevices.Count} workstation(s).";
        }
        finally
        {
            IsLoading = false;
            NotifyCountsChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAssignAsset))]
    private async Task AssignAssetAsync()
    {
        if (SelectedWorkstation is null)
        {
            return;
        }

        IsSavingAssignment = true;
        AssignAssetCommand.NotifyCanExecuteChanged();
        AssignmentFeedback = "Saving assignment...";

        try
        {
            var request = new UpdateAssignmentRequest(
                AssetNumber: string.IsNullOrWhiteSpace(EditAssetNumber) ? null : EditAssetNumber.Trim(),
                AssignedUserSid: string.IsNullOrWhiteSpace(EditAssignedUserSid) ? null : EditAssignedUserSid.Trim(),
                ExpectedRowVersion: SelectedWorkstation.RowVersion);

            var result = await _apiClient.UpdateWorkstationAssignmentAsync(SelectedWorkstation.DeviceId, request);
            if (!result.IsSuccess)
            {
                AssignmentFeedback = result.ErrorMessage ?? "Failed to update workstation assignment.";
                return;
            }

            AssignmentFeedback = "Assignment updated.";
            await LoadAsync();
        }
        finally
        {
            IsSavingAssignment = false;
            AssignAssetCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAssignAsset() => SelectedWorkstation is not null && !IsSavingAssignment;

    private void ApplyFilterAndSort()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        IEnumerable<DeviceSummaryDto> queryable = _allDevices;

        if (!string.IsNullOrEmpty(query))
        {
            queryable = queryable.Where(d =>
                d.Hostname.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(d.AssetNumber) && d.AssetNumber.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.AssignedUserSid) && d.AssignedUserSid.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        var sortMode = SelectedSortIndex == 1
            ? WorkstationSortMode.HostnameAscending
            : WorkstationSortMode.LastSeenDescending;

        queryable = sortMode == WorkstationSortMode.HostnameAscending
            ? queryable.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            : queryable.OrderByDescending(d => d.LastSeenUtc ?? DateTimeOffset.MinValue)
                       .ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase);

        var selectedId = SelectedWorkstation?.DeviceId;
        FilteredWorkstations.Clear();
        WorkstationItemViewModel? restoredSelection = null;

        foreach (var dto in queryable)
        {
            var vm = new WorkstationItemViewModel(dto);
            FilteredWorkstations.Add(vm);
            if (selectedId == dto.DeviceId)
            {
                restoredSelection = vm;
            }
        }

        SelectedWorkstation = restoredSelection;
        NotifyCountsChanged();
    }

    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(StaleCount));
        OnPropertyChanged(nameof(OfflineCount));
    }

    private static ConnectivityState ParseConnectivity(DeviceSummaryDto dto) =>
        Enum.TryParse<ConnectivityState>(dto.ConnectivityState, ignoreCase: true, out var parsed)
            ? parsed
            : DeviceConnectivityEvaluator.Evaluate(dto.LastSeenUtc, DateTimeOffset.UtcNow);
}
