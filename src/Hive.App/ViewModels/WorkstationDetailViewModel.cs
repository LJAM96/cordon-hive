using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Domain.Identity;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private string _currentSiteId = string.Empty;
    private string? _currentAssignedUserSid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteRemoteAccessAction))]
    [NotifyPropertyChangedFor(nameof(CanExecuteBreakGlassReveal))]
    [NotifyPropertyChangedFor(nameof(CanExecuteManageEscrow))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteBreakGlassReveal))]
    public partial bool HasEscrowedCredential { get; set; }

    [ObservableProperty]
    public partial string EscrowedAccountUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EscrowStatusText { get; set; } = "Not Escrowed";

    [ObservableProperty]
    public partial string RemoteAccessScopeNote { get; set; } = "Select a workstation to evaluate LAN RDP and break-glass custody permissions.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestRdpVisibility))]
    [NotifyPropertyChangedFor(nameof(CanExecuteRemoteAccessAction))]
    public partial bool CanRequestRdp { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BreakGlassCustodyVisibility))]
    [NotifyPropertyChangedFor(nameof(CanExecuteBreakGlassReveal))]
    public partial bool CanAccessBreakGlass { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManageEscrowVisibility))]
    [NotifyPropertyChangedFor(nameof(CanExecuteManageEscrow))]
    public partial bool CanManageEscrow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteRemoteAccessAction))]
    [NotifyPropertyChangedFor(nameof(CanExecuteBreakGlassReveal))]
    [NotifyPropertyChangedFor(nameof(CanExecuteManageEscrow))]
    public partial bool IsRemoteAccessBusy { get; set; }

    [ObservableProperty]
    public partial bool IsRemoteAccessFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity RemoteAccessFeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string RemoteAccessFeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RemoteAccessFeedbackMessage { get; set; } = string.Empty;

    public Visibility RequestRdpVisibility => CanRequestRdp ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BreakGlassCustodyVisibility => CanAccessBreakGlass ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManageEscrowVisibility => CanManageEscrow ? Visibility.Visible : Visibility.Collapsed;

    public bool CanExecuteRemoteAccessAction =>
        CanRequestRdp && SelectedDeviceId != Guid.Empty && !IsRemoteAccessBusy;

    public bool CanExecuteBreakGlassReveal =>
        CanAccessBreakGlass && SelectedDeviceId != Guid.Empty && HasEscrowedCredential && !IsRemoteAccessBusy;

    public bool CanExecuteManageEscrow =>
        CanManageEscrow && SelectedDeviceId != Guid.Empty && !IsRemoteAccessBusy;

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
        IsRemoteAccessFeedbackOpen = false;

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
            var escrowTask = _apiClient.GetEscrowCredentialStatusAsync(deviceId, cancellationToken);
            await Task.WhenAll(detailTask, telemetryTask, escrowTask);

            var detailResult = detailTask.Result;
            var telemetryResult = telemetryTask.Result;
            var escrowResult = escrowTask.Result;

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

            _currentSiteId = detail.SiteId;
            _currentAssignedUserSid = detail.AssignedUserSid;

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
            UpdateRolePermissions();
            ApplyEscrowStatus(escrowResult);
            StatusMessage = $"Loaded {detail.Hostname} ({ConnectivityText}).";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<ApiResult<RdpAuthorizationResponse>> RequestRdpAsync(
        string reason,
        bool useEscrowedCredential,
        CancellationToken cancellationToken = default)
    {
        if (SelectedDeviceId == Guid.Empty)
        {
            return ApiResult<RdpAuthorizationResponse>.Failure(ApiErrorKind.BadRequest, "No workstation selected.");
        }

        IsRemoteAccessBusy = true;
        try
        {
            return await _apiClient.RequestRdpAsync(
                SelectedDeviceId,
                new RdpRequest(reason.Trim(), useEscrowedCredential),
                cancellationToken);
        }
        finally
        {
            IsRemoteAccessBusy = false;
        }
    }

    public async Task<ApiResult<RevealEscrowCredentialResponse>> RevealEscrowCredentialAsync(
        string reason,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        if (SelectedDeviceId == Guid.Empty)
        {
            return ApiResult<RevealEscrowCredentialResponse>.Failure(ApiErrorKind.BadRequest, "No workstation selected.");
        }

        IsRemoteAccessBusy = true;
        try
        {
            return await _apiClient.RevealEscrowCredentialAsync(
                SelectedDeviceId,
                new RevealEscrowCredentialRequest(reason.Trim(), purpose),
                cancellationToken);
        }
        finally
        {
            IsRemoteAccessBusy = false;
        }
    }

    public async Task<ApiResult<EscrowCredentialStatusDto>> UpsertEscrowCredentialAsync(
        string accountUsername,
        string password,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (SelectedDeviceId == Guid.Empty)
        {
            return ApiResult<EscrowCredentialStatusDto>.Failure(ApiErrorKind.BadRequest, "No workstation selected.");
        }

        IsRemoteAccessBusy = true;
        try
        {
            var result = await _apiClient.UpsertEscrowCredentialAsync(
                SelectedDeviceId,
                new UpsertEscrowCredentialRequest(
                    accountUsername.Trim(),
                    password,
                    string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()),
                cancellationToken);

            if (result.IsSuccess && result.Value is not null)
            {
                ApplyEscrowStatus(result);
            }

            return result;
        }
        finally
        {
            IsRemoteAccessBusy = false;
        }
    }

    public void SetRemoteAccessFeedback(InfoBarSeverity severity, string title, string message)
    {
        RemoteAccessFeedbackSeverity = severity;
        RemoteAccessFeedbackTitle = title;
        RemoteAccessFeedbackMessage = message;
        IsRemoteAccessFeedbackOpen = true;
    }

    private void UpdateRolePermissions()
    {
        var actor = BuildCurrentActor();
        if (actor is null || string.IsNullOrWhiteSpace(_currentSiteId))
        {
            CanRequestRdp = false;
            CanAccessBreakGlass = false;
            CanManageEscrow = false;
            RemoteAccessScopeNote = "Remote access and break-glass custody permissions are unavailable.";
            return;
        }

        CanRequestRdp = ScopeAuthorizationService.CanRequestRdp(actor, _currentSiteId);
        CanAccessBreakGlass = ScopeAuthorizationService.CanAccessBreakGlassCredential(actor, _currentSiteId);
        CanManageEscrow = ScopeAuthorizationService.CanEscrowWorkstationCredential(
            actor,
            _currentSiteId,
            _currentAssignedUserSid);

        if (!CanRequestRdp && !CanAccessBreakGlass && !CanManageEscrow)
        {
            RemoteAccessScopeNote = "No LAN RDP or break-glass custody permissions for your role in this site.";
        }
        else if (!CanAccessBreakGlass)
        {
            RemoteAccessScopeNote = "Break-glass reveal and assisted RDP require explicit BreakGlassCustodian custody in this site.";
        }
        else
        {
            RemoteAccessScopeNote = "All RDP launches, escrow updates, and break-glass reveals are strictly reason-audited.";
        }
    }

    private void ApplyEscrowStatus(ApiResult<EscrowCredentialStatusDto> escrowResult)
    {
        if (!escrowResult.IsSuccess || escrowResult.Value is null)
        {
            HasEscrowedCredential = false;
            EscrowedAccountUsername = string.Empty;
            EscrowStatusText = escrowResult.ErrorKind == ApiErrorKind.Forbidden
                ? "Restricted (Custodian, Administrator, or assigned user required)"
                : "Not Escrowed";
            return;
        }

        var status = escrowResult.Value;
        if (status.HasEscrowedCredential)
        {
            HasEscrowedCredential = true;
            EscrowedAccountUsername = status.AccountUsername ?? string.Empty;
            var userLabel = string.IsNullOrWhiteSpace(status.AccountUsername) ? "unknown user" : status.AccountUsername;
            var updatedLocal = status.UpdatedAtUtc is { } utc
                ? utc.ToLocalTime().ToString("g")
                : "unknown time";
            EscrowStatusText = $"Escrowed ({userLabel}, updated {updatedLocal})";
        }
        else
        {
            HasEscrowedCredential = false;
            EscrowedAccountUsername = string.Empty;
            EscrowStatusText = "Not Escrowed";
        }
    }

    private ActorIdentity? BuildCurrentActor()
    {
        var me = _mainViewModel.CurrentUser;
        if (me is null || !_mainViewModel.IsSignedIn)
        {
            return null;
        }

        var roles = new HashSet<HiveRole>();
        foreach (var raw in me.Roles)
        {
            if (Enum.TryParse<HiveRole>(raw, ignoreCase: true, out var parsed))
            {
                roles.Add(parsed);
            }
        }

        return new ActorIdentity(
            UserSid: me.UserSid,
            DisplayName: me.DisplayName,
            Roles: roles,
            AllowedSiteIds: new HashSet<string>(me.AllowedSiteIds, StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: me.AllSitesAccess,
            IsDevice: false,
            DeviceId: null);
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
        _currentSiteId = string.Empty;
        _currentAssignedUserSid = null;
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
        HasEscrowedCredential = false;
        EscrowedAccountUsername = string.Empty;
        EscrowStatusText = "Not Escrowed";
        CanRequestRdp = false;
        CanAccessBreakGlass = false;
        CanManageEscrow = false;
        RemoteAccessScopeNote = "Select a workstation to evaluate LAN RDP and break-glass custody permissions.";
        IsRemoteAccessFeedbackOpen = false;
        ActiveSessions.Clear();
        Volumes.Clear();
        Processes.Clear();
        EmptySessionsVisibility = Visibility.Visible;
        EmptyVolumesVisibility = Visibility.Visible;
        EmptyProcessesVisibility = Visibility.Visible;
    }
}
