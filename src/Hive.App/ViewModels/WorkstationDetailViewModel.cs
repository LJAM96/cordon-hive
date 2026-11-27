using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Devices;
using Microsoft.UI.Xaml;

namespace Hive.App.ViewModels;

public sealed class SessionItemViewModel
{
    public SessionItemViewModel(ObservedSessionDto dto)
    {
        SessionId = dto.SessionId;
        UserSidDisplay = string.IsNullOrWhiteSpace(dto.UserSid) ? "System / Unauthenticated" : dto.UserSid;
        SessionState = string.IsNullOrWhiteSpace(dto.SessionState) ? "Unknown" : dto.SessionState;
        ObservedLocalText = dto.ObservedAtUtc.ToLocalTime().ToString("g");
    }

    public int SessionId { get; }
    public string UserSidDisplay { get; }
    public string SessionState { get; }
    public string ObservedLocalText { get; }
    public string HeaderText => $"Session {SessionId} ({SessionState})";
}

public sealed partial class WorkstationDetailViewModel : ObservableObject
{
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    public partial Guid SelectedDeviceId { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Select a workstation to inspect.";

    [ObservableProperty]
    public partial string TelemetryStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Hostname { get; set; } = "—";

    [ObservableProperty]
    public partial string SiteIdDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string AssetNumberDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string AssignedUserDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string AgentVersionDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string EnrollmentStateDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string ConnectivityText { get; set; } = "Offline";

    [ObservableProperty]
    public partial string ConnectivityGlyph { get; set; } = "\uE711";

    [ObservableProperty]
    public partial string ConnectivityAccessibilityName { get; set; } = "Connectivity: Offline";

    [ObservableProperty]
    public partial string LastSeenLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial string CpuMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string MemoryMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string GpuMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string DiskMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string TelemetrySampledLocalText { get; set; } = "No sample recorded";

    [ObservableProperty]
    public partial string ProcessSnapshotHeaderText { get; set; } = "Running Processes";

    [ObservableProperty]
    public partial Visibility EmptySessionsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyVolumesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyProcessesVisibility { get; set; } = Visibility.Visible;

    public ObservableCollection<SessionItemViewModel> ActiveSessions { get; } = [];
    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = [];
    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];

    public WorkstationDetailViewModel(HiveApiClient apiClient, MainViewModel mainViewModel)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    [RelayCommand]
    public Task RefreshAsync() => SelectedDeviceId != Guid.Empty
        ? LoadAsync(SelectedDeviceId)
        : Task.CompletedTask;

    public async Task LoadAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        SelectedDeviceId = deviceId;

        if (!_mainViewModel.CanNavigateTo(MainViewModel.PageKeys.WorkstationDetail))
        {
            ClearDetails();
            StatusMessage = "Access restricted: Supervisor, Administrator, or Auditor role required.";
            TelemetryStatusMessage = "Unauthorized.";
            return;
        }

        if (deviceId == Guid.Empty)
        {
            ClearDetails();
            StatusMessage = "No workstation selected.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading workstation detail and telemetry...";
        TelemetryStatusMessage = "Fetching latest telemetry...";

        try
        {
            var detailTask = _apiClient.GetWorkstationDetailAsync(deviceId, cancellationToken);
            var telemetryTask = _apiClient.GetWorkstationTelemetryAsync(deviceId, 60, cancellationToken);
            await Task.WhenAll(detailTask, telemetryTask);

            var detailResult = detailTask.Result;
            var telemetryResult = telemetryTask.Result;

            if (!detailResult.IsSuccess || detailResult.Value is null)
            {
                ClearDetails();
                StatusMessage = detailResult.ErrorMessage ?? "Unable to load workstation detail.";

                if (detailResult.ErrorKind == ApiErrorKind.Forbidden)
                {
                    _mainViewModel.ReportBanner(
                        ConnectivityBannerState.PermissionDenied,
                        "Workstation Access Denied",
                        StatusMessage);
                }
                else if (detailResult.ErrorKind == ApiErrorKind.Unauthorized)
                {
                    _mainViewModel.ReportBanner(
                        ConnectivityBannerState.AuthError,
                        "Authentication Required",
                        StatusMessage);
                }

                return;
            }

            var detail = detailResult.Value;
            var telemetry = telemetryResult.IsSuccess ? telemetryResult.Value : null;

            Hostname = detail.Hostname;
            SiteIdDisplay = detail.SiteId;
            AssetNumberDisplay = string.IsNullOrWhiteSpace(detail.AssetNumber) ? "Unassigned" : detail.AssetNumber;
            AssignedUserDisplay = string.IsNullOrWhiteSpace(detail.AssignedUserSid) ? "Unassigned" : detail.AssignedUserSid;
            AgentVersionDisplay = string.IsNullOrWhiteSpace(detail.AgentVersion) ? "Unknown" : detail.AgentVersion;
            EnrollmentStateDisplay = detail.EnrollmentState;

            var lastSeenUtc = telemetry?.LastSeenUtc ?? detail.LastSeenUtc;
            LastSeenLocalText = TelemetryPresentation.FormatLastSeenLocal(lastSeenUtc);

            var connectivity = TelemetryPresentation.ParseConnectivity(
                telemetry?.ConnectivityState ?? detail.ConnectivityState,
                lastSeenUtc);
            ConnectivityText = TelemetryPresentation.GetConnectivityText(connectivity);
            ConnectivityGlyph = TelemetryPresentation.GetConnectivityGlyph(connectivity);
            ConnectivityAccessibilityName = $"Connectivity: {ConnectivityText}, Last seen {LastSeenLocalText}";

            ActiveSessions.Clear();
            foreach (var session in detail.ActiveSessions)
            {
                ActiveSessions.Add(new SessionItemViewModel(session));
            }
            EmptySessionsVisibility = ActiveSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var isOffline = connectivity == ConnectivityState.Offline;
            ApplyTelemetry(telemetry, telemetryResult, isOffline);
            StatusMessage = $"Loaded {detail.Hostname} ({ConnectivityText}).";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyTelemetry(
        DeviceTelemetryResponse? telemetry,
        ApiResult<DeviceTelemetryResponse> telemetryResult,
        bool isOffline)
    {
        Volumes.Clear();
        Processes.Clear();

        if (telemetry is null)
        {
            CpuMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            MemoryMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            GpuMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            DiskMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            TelemetrySampledLocalText = "No telemetry available";
            ProcessSnapshotHeaderText = "Running Processes";
            TelemetryStatusMessage = telemetryResult.ErrorMessage ?? "Telemetry endpoint unavailable.";
            EmptyVolumesVisibility = Visibility.Visible;
            EmptyProcessesVisibility = Visibility.Visible;
            return;
        }

        var latest = telemetry.LatestSample;
        CpuMetricText = TelemetryPresentation.FormatPercentMetric(latest?.CpuPercent, isOffline);
        MemoryMetricText = TelemetryPresentation.FormatMemoryMetric(latest, isOffline);
        GpuMetricText = TelemetryPresentation.FormatPercentMetric(latest?.GpuPercent, isOffline);
        DiskMetricText = TelemetryPresentation.FormatPercentMetric(latest?.DiskActivePercent, isOffline);

        TelemetrySampledLocalText = latest is not null
            ? $"Sampled {latest.SampledAtUtc.ToLocalTime():g}"
            : "No metric sample recorded yet";

        foreach (var vol in telemetry.Volumes)
        {
            Volumes.Add(new VolumeItemViewModel(vol));
        }

        if (telemetry.LatestProcessSnapshot is { } snapshot)
        {
            ProcessSnapshotHeaderText = $"Running Processes (sampled {snapshot.SampledAtUtc.ToLocalTime():g})";
            foreach (var proc in snapshot.Processes
                         .OrderByDescending(p => p.WorkingSetBytes)
                         .ThenBy(p => p.Pid))
            {
                Processes.Add(new ProcessItemViewModel(proc));
            }
        }
        else
        {
            ProcessSnapshotHeaderText = "Running Processes";
        }

        EmptyVolumesVisibility = Volumes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyProcessesVisibility = Processes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TelemetryStatusMessage = isOffline
            ? "Workstation is offline; live CPU/RAM/GPU/Disk metrics are unavailable."
            : latest is not null
                ? $"Showing telemetry ({telemetry.RecentSamples.Count} recent sample(s))."
                : "Connected, awaiting first telemetry sample.";
    }

    private void ClearDetails()
    {
        Hostname = "—";
        SiteIdDisplay = "—";
        AssetNumberDisplay = "—";
        AssignedUserDisplay = "—";
        AgentVersionDisplay = "—";
        EnrollmentStateDisplay = "—";
        ConnectivityText = "Offline";
        ConnectivityGlyph = TelemetryPresentation.GetConnectivityGlyph(ConnectivityState.Offline);
        ConnectivityAccessibilityName = "Connectivity: Offline";
        LastSeenLocalText = "Never";
        CpuMetricText = TelemetryPresentation.UnavailableOfflineText;
        MemoryMetricText = TelemetryPresentation.UnavailableOfflineText;
        GpuMetricText = TelemetryPresentation.UnavailableOfflineText;
        DiskMetricText = TelemetryPresentation.UnavailableOfflineText;
        TelemetrySampledLocalText = "No sample recorded";
        ProcessSnapshotHeaderText = "Running Processes";
        ActiveSessions.Clear();
        Volumes.Clear();
        Processes.Clear();
        EmptySessionsVisibility = Visibility.Visible;
        EmptyVolumesVisibility = Visibility.Visible;
        EmptyProcessesVisibility = Visibility.Visible;
    }
}
