using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Devices;
using Microsoft.UI.Xaml;

namespace Hive.App.ViewModels;

public sealed class AssignedWorkstationItemViewModel
{
    public AssignedWorkstationItemViewModel(DeviceSummaryDto dto)
    {
        DeviceId = dto.DeviceId;
        Hostname = dto.Hostname;
        SiteId = dto.SiteId;
        AssetNumberDisplay = string.IsNullOrWhiteSpace(dto.AssetNumber) ? "Unassigned asset" : dto.AssetNumber;

        var state = TelemetryPresentation.ParseConnectivity(dto.ConnectivityState, dto.LastSeenUtc);
        ConnectivityText = TelemetryPresentation.GetConnectivityText(state);
        ConnectivityGlyph = TelemetryPresentation.GetConnectivityGlyph(state);
        LastSeenLocalText = $"Last seen {TelemetryPresentation.FormatLastSeenLocal(dto.LastSeenUtc)}";
    }

    public Guid DeviceId { get; }
    public string Hostname { get; }
    public string SiteId { get; }
    public string AssetNumberDisplay { get; }
    public string ConnectivityText { get; }
    public string ConnectivityGlyph { get; }
    public string LastSeenLocalText { get; }
}

public sealed partial class ResourcesViewModel : ObservableObject
{
    private readonly LocalAgentIpcClient _ipcClient;
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ActiveSourceTitle { get; set; } = "This Workstation (Local Agent IPC)";

    [ObservableProperty]
    public partial string AgentIpcStatusTitle { get; set; } = "Checking Local Agent IPC...";

    [ObservableProperty]
    public partial string AgentIpcStatusMessage { get; set; } = "Connecting to local agent pipe CordonHive.Agent.User...";

    [ObservableProperty]
    public partial string AgentEnrollmentDisplay { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string AgentConnectivityText { get; set; } = "Offline";

    [ObservableProperty]
    public partial string AgentConnectivityGlyph { get; set; } = "\uE711";

    [ObservableProperty]
    public partial string LastHeartbeatLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial string PendingQueueDisplay { get; set; } = "—";

    [ObservableProperty]
    public partial string CpuMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string MemoryMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string GpuMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string DiskMetricText { get; set; } = TelemetryPresentation.UnavailableOfflineText;

    [ObservableProperty]
    public partial string SampleTimestampLocalText { get; set; } = "No sample recorded";

    [ObservableProperty]
    public partial string ProcessSnapshotHeaderText { get; set; } = "Running Processes";

    [ObservableProperty]
    public partial string LastRefreshedLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial string AssignedWorkstationsStatusText { get; set; } = "Checking assigned workstations...";

    [ObservableProperty]
    public partial AssignedWorkstationItemViewModel? SelectedAssignedWorkstation { get; set; }

    [ObservableProperty]
    public partial bool IsViewingLocalSource { get; set; } = true;

    [ObservableProperty]
    public partial Visibility EmptyVolumesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyProcessesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyAssignedVisibility { get; set; } = Visibility.Visible;

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = [];
    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];
    public ObservableCollection<AssignedWorkstationItemViewModel> AssignedWorkstations { get; } = [];

    public ResourcesViewModel(
        LocalAgentIpcClient ipcClient,
        HiveApiClient apiClient,
        MainViewModel mainViewModel)
    {
        _ipcClient = ipcClient;
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    partial void OnSelectedAssignedWorkstationChanged(AssignedWorkstationItemViewModel? value)
    {
        if (value is not null)
        {
            _ = LoadAssignedWorkstationTelemetryAsync(value);
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var statusTask = _ipcClient.GetStatusAsync();
            var telemetryTask = _ipcClient.GetLocalTelemetryAsync();
            var assignedTask = LoadAssignedWorkstationsAsync();

            await Task.WhenAll(statusTask, telemetryTask, assignedTask);

            var statusResult = statusTask.Result;
            var telemetryResult = telemetryTask.Result;

            ApplyAgentStatus(statusResult);

            if (IsViewingLocalSource || SelectedAssignedWorkstation is null)
            {
                ActiveSourceTitle = "This Workstation (Local Agent IPC)";
                ApplyLocalTelemetry(statusResult, telemetryResult);
            }
            else
            {
                await LoadAssignedWorkstationTelemetryAsync(SelectedAssignedWorkstation);
            }

            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SwitchToLocalAsync()
    {
        SelectedAssignedWorkstation = null;
        IsViewingLocalSource = true;
        ActiveSourceTitle = "This Workstation (Local Agent IPC)";

        IsLoading = true;
        try
        {
            var statusTask = _ipcClient.GetStatusAsync();
            var telemetryTask = _ipcClient.GetLocalTelemetryAsync();
            await Task.WhenAll(statusTask, telemetryTask);

            ApplyAgentStatus(statusTask.Result);
            ApplyLocalTelemetry(statusTask.Result, telemetryTask.Result);
            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyAgentStatus(LocalIpcResult<IpcAgentStatusResponse> statusResult)
    {
        if (!statusResult.IsAvailable || statusResult.Value is null)
        {
            AgentIpcStatusTitle = "Local Agent IPC Unavailable";
            AgentIpcStatusMessage = statusResult.StatusMessage;
            AgentEnrollmentDisplay = "Service unreachable";
            AgentConnectivityText = "Offline";
            AgentConnectivityGlyph = TelemetryPresentation.GetConnectivityGlyph(ConnectivityState.Offline);
            LastHeartbeatLocalText = "Unavailable";
            PendingQueueDisplay = "—";
            return;
        }

        var status = statusResult.Value;
        var connectivity = TelemetryPresentation.ParseConnectivity(status.ConnectivityState, status.LastHeartbeatUtc);
        AgentIpcStatusTitle = "Local Agent Connected (IPC)";
        AgentIpcStatusMessage = status.ProtectedModeActive
            ? $"Connected via {LocalAgentIpcClient.DefaultPipeName}. Protected mode active."
            : $"Connected via {LocalAgentIpcClient.DefaultPipeName}. Policy rev {status.PolicyRevision}.";
        AgentEnrollmentDisplay = status.EnrollmentState;
        AgentConnectivityText = TelemetryPresentation.GetConnectivityText(connectivity);
        AgentConnectivityGlyph = TelemetryPresentation.GetConnectivityGlyph(connectivity);
        LastHeartbeatLocalText = TelemetryPresentation.FormatLastSeenLocal(status.LastHeartbeatUtc);
        PendingQueueDisplay = status.PendingQueueCount.ToString();
    }

    private void ApplyLocalTelemetry(
        LocalIpcResult<IpcAgentStatusResponse> statusResult,
        LocalIpcResult<IpcLocalTelemetryResponse> telemetryResult)
    {
        Volumes.Clear();
        Processes.Clear();

        if (!telemetryResult.IsAvailable || telemetryResult.Value is null)
        {
            var isOffline = !statusResult.IsAvailable;
            CpuMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            MemoryMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            GpuMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            DiskMetricText = isOffline ? TelemetryPresentation.UnavailableOfflineText : TelemetryPresentation.UnsupportedUnknownText;
            SampleTimestampLocalText = "Local telemetry unavailable";
            ProcessSnapshotHeaderText = "Running Processes";

            if (!statusResult.IsAvailable)
            {
                AgentIpcStatusMessage = telemetryResult.StatusMessage;
            }
            else
            {
                AgentIpcStatusMessage = $"{AgentIpcStatusMessage} ({telemetryResult.StatusMessage})";
            }

            EmptyVolumesVisibility = Visibility.Visible;
            EmptyProcessesVisibility = Visibility.Visible;
            return;
        }

        var local = telemetryResult.Value;
        ApplyTelemetryPayload(local.LatestSample, local.Volumes, local.LatestProcessSnapshot, isOffline: false);
    }

    private async Task LoadAssignedWorkstationsAsync()
    {
        if (_mainViewModel.CurrentUser is null)
        {
            AssignedWorkstations.Clear();
            EmptyAssignedVisibility = Visibility.Visible;
            AssignedWorkstationsStatusText = "Central server offline or unauthenticated; showing local IPC telemetry only.";
            return;
        }

        var result = await _apiClient.GetMyWorkstationsAsync();
        if (!result.IsSuccess || result.Value is null)
        {
            AssignedWorkstations.Clear();
            EmptyAssignedVisibility = Visibility.Visible;
            AssignedWorkstationsStatusText = result.ErrorKind == ApiErrorKind.Offline
                ? "Central server offline; local IPC telemetry remains available."
                : result.ErrorMessage ?? "Assigned workstations unavailable.";
            return;
        }

        var selectedId = SelectedAssignedWorkstation?.DeviceId;
        AssignedWorkstations.Clear();
        AssignedWorkstationItemViewModel? restored = null;
        foreach (var dto in result.Value)
        {
            var vm = new AssignedWorkstationItemViewModel(dto);
            AssignedWorkstations.Add(vm);
            if (selectedId == dto.DeviceId)
            {
                restored = vm;
            }
        }

        EmptyAssignedVisibility = AssignedWorkstations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AssignedWorkstationsStatusText = AssignedWorkstations.Count == 0
            ? "No workstations currently assigned to your account."
            : $"{AssignedWorkstations.Count} assigned workstation(s) on LAN server. Select one to inspect server telemetry.";

        if (restored is not null)
        {
            SelectedAssignedWorkstation = restored;
        }
    }

    private async Task LoadAssignedWorkstationTelemetryAsync(AssignedWorkstationItemViewModel workstation)
    {
        IsViewingLocalSource = false;
        ActiveSourceTitle = $"Assigned Workstation: {workstation.Hostname}";
        IsLoading = true;

        try
        {
            var result = await _apiClient.GetWorkstationTelemetryAsync(workstation.DeviceId, 60);
            if (!result.IsSuccess || result.Value is null)
            {
                Volumes.Clear();
                Processes.Clear();
                CpuMetricText = TelemetryPresentation.UnavailableOfflineText;
                MemoryMetricText = TelemetryPresentation.UnavailableOfflineText;
                GpuMetricText = TelemetryPresentation.UnavailableOfflineText;
                DiskMetricText = TelemetryPresentation.UnavailableOfflineText;
                SampleTimestampLocalText = result.ErrorMessage ?? "Unable to load workstation telemetry.";
                ProcessSnapshotHeaderText = "Running Processes";
                EmptyVolumesVisibility = Visibility.Visible;
                EmptyProcessesVisibility = Visibility.Visible;
                return;
            }

            var telemetry = result.Value;
            var connectivity = TelemetryPresentation.ParseConnectivity(telemetry.ConnectivityState, telemetry.LastSeenUtc);
            var isOffline = connectivity == ConnectivityState.Offline;
            ApplyTelemetryPayload(
                telemetry.LatestSample,
                telemetry.Volumes,
                telemetry.LatestProcessSnapshot,
                isOffline);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyTelemetryPayload(
        MetricSampleDto? latest,
        IReadOnlyList<VolumeSnapshotDto> volumes,
        ProcessSnapshotDto? processSnapshot,
        bool isOffline)
    {
        CpuMetricText = TelemetryPresentation.FormatPercentMetric(latest?.CpuPercent, isOffline);
        MemoryMetricText = TelemetryPresentation.FormatMemoryMetric(latest, isOffline);
        GpuMetricText = TelemetryPresentation.FormatPercentMetric(latest?.GpuPercent, isOffline);
        DiskMetricText = TelemetryPresentation.FormatPercentMetric(latest?.DiskActivePercent, isOffline);

        SampleTimestampLocalText = isOffline
            ? "Workstation is offline (live metrics unavailable)"
            : latest is not null
                ? $"Sampled {latest.SampledAtUtc.ToLocalTime():g}"
                : "No metric sample recorded yet";

        Volumes.Clear();
        foreach (var vol in volumes)
        {
            Volumes.Add(new VolumeItemViewModel(vol));
        }

        Processes.Clear();
        if (processSnapshot is not null)
        {
            ProcessSnapshotHeaderText = $"Running Processes (sampled {processSnapshot.SampledAtUtc.ToLocalTime():g})";
            foreach (var proc in processSnapshot.Processes
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
    }
}
