using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Identity;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public sealed class AuditEventItemViewModel
{
    public AuditEventItemViewModel(AuditEventDto dto)
    {
        EventId = dto.EventId;
        OccurredAtUtcText = dto.OccurredAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        Action = dto.Action;
        ActorId = dto.ActorId;
        ObjectType = dto.ObjectType;
        ObjectId = dto.ObjectId;
        Result = dto.Result;
        SiteId = string.IsNullOrWhiteSpace(dto.SiteId) ? "-" : dto.SiteId;
        CorrelationId = dto.CorrelationId;
        AccessibilityName = $"{OccurredAtUtcText}: {Action} by {ActorId} on {ObjectType} {ObjectId}, result {Result}, site {SiteId}";
    }

    public Guid EventId { get; }
    public string OccurredAtUtcText { get; }
    public string Action { get; }
    public string ActorId { get; }
    public string ObjectType { get; }
    public string ObjectId { get; }
    public string Result { get; }
    public string SiteId { get; }
    public string CorrelationId { get; }
    public string AccessibilityName { get; }
}

public sealed partial class AuditViewModel : ObservableObject
{
    private const int DefaultPageSize = 50;

    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEmergencyStopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyFiltersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string RoleScopeSummaryText { get; set; } = "Checking permissions...";

    [ObservableProperty]
    public partial string LastRefreshedLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity FeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string FeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanViewOperationalHealth { get; set; }

    [ObservableProperty]
    public partial Visibility OperationalHealthVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEmergencyStopCommand))]
    public partial bool CanOperateEmergencyStop { get; set; }

    [ObservableProperty]
    public partial Visibility EmergencyStopControlsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFiltersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial bool CanViewAudit { get; set; }

    [ObservableProperty]
    public partial Visibility AuditLogVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility AuditRestrictedNoticeVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string OperationalStatusValue { get; set; } = "-";

    [ObservableProperty]
    public partial string StorageProviderValue { get; set; } = "-";

    [ObservableProperty]
    public partial string ProtocolVersionValue { get; set; } = "-";

    [ObservableProperty]
    public partial string ActiveDeploymentLeaseCountValue { get; set; } = "-";

    [ObservableProperty]
    public partial string PendingOutboxCountValue { get; set; } = "-";

    [ObservableProperty]
    public partial string LastTelemetryPruneUtcValue { get; set; } = "Never";

    [ObservableProperty]
    public partial bool DeploymentsPaused { get; set; }

    [ObservableProperty]
    public partial string PauseReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmergencyStopButtonText { get; set; } = "Pause Deployments (Emergency Stop)";

    [ObservableProperty]
    public partial string EmergencyStopReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool InvalidateActiveLeases { get; set; }

    [ObservableProperty]
    public partial string SiteIdFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActionFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActorIdFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial int CurrentPage { get; set; } = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial string PageSummaryText { get; set; } = "Page 1 · 0 total event(s)";

    [ObservableProperty]
    public partial Visibility EmptyAuditEventsVisibility { get; set; } = Visibility.Visible;

    public bool IsNotBusy => !IsBusy;

    public ObservableCollection<AuditEventItemViewModel> AuditEvents { get; } = [];

    public AuditViewModel(HiveApiClient apiClient, MainViewModel mainViewModel)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var me = await ResolveCurrentUserAsync();
            if (me is null)
            {
                ApplyUnauthenticatedState();
                return;
            }

            var actor = ToActorIdentity(me);
            UpdateCapabilities(actor);

            var errors = new List<string>();

            if (CanViewOperationalHealth)
            {
                await LoadOperationalStatusCoreAsync(errors);
            }
            else
            {
                ResetOperationalStatusFields();
            }

            if (CanViewAudit)
            {
                await LoadAuditEventsCoreAsync(errors);
            }
            else
            {
                AuditEvents.Clear();
                TotalCount = 0;
                EmptyAuditEventsVisibility = Visibility.Visible;
                UpdatePageSummary();
            }

            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
            if (errors.Count > 0)
            {
                SetFeedback(InfoBarSeverity.Warning, "Partial Load Warning", string.Join(" | ", errors));
            }
            else if (IsFeedbackOpen && FeedbackSeverity == InfoBarSeverity.Warning)
            {
                IsFeedbackOpen = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyFilters))]
    private async Task ApplyFiltersAsync()
    {
        CurrentPage = 1;
        await ReloadAuditPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1)
        {
            return;
        }

        CurrentPage--;
        await ReloadAuditPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private async Task NextPageAsync()
    {
        if (!HasNextPage())
        {
            return;
        }

        CurrentPage++;
        await ReloadAuditPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApplyFilters))]
    private async Task ExportCsvAsync()
    {
        if (!CanViewAudit)
        {
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", "Only Auditor or Administrator roles can export audit logs.");
            return;
        }

        IsBusy = true;
        try
        {
            var exportResult = await _apiClient.ExportAuditAsync(
                "csv",
                SiteIdFilter,
                ActionFilter,
                ActorIdFilter,
                ResultFilter);

            if (!exportResult.IsSuccess || exportResult.Value is null)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Audit Export Failed",
                    exportResult.ErrorMessage ?? "Unable to export scoped audit log.");
                return;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var exportsDir = Path.Combine(localAppData, "CordonHive", "Exports");
            Directory.CreateDirectory(exportsDir);

            var fileName = $"audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv";
            var fullPath = Path.Combine(exportsDir, fileName);
            await File.WriteAllTextAsync(fullPath, exportResult.Value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var savedMessage = $"Exported scoped audit CSV to {fullPath}";
            SetFeedback(InfoBarSeverity.Success, "Audit CSV Exported", savedMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFeedback(InfoBarSeverity.Error, "File Write Failed", $"Could not write audit CSV file: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteEmergencyStop))]
    private async Task ToggleEmergencyStopAsync()
    {
        if (!CanOperateEmergencyStop)
        {
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", "Only Administrator accounts can operate the deployment emergency stop.");
            return;
        }

        if (string.IsNullOrWhiteSpace(EmergencyStopReason))
        {
            SetFeedback(InfoBarSeverity.Warning, "Reason Required", "Enter a mandatory operational reason before changing emergency stop state.");
            return;
        }

        IsBusy = true;
        try
        {
            var targetPause = !DeploymentsPaused;
            var request = new EmergencyStopRequest(
                PauseDeployments: targetPause,
                InvalidateActiveLeases: targetPause && InvalidateActiveLeases,
                Reason: EmergencyStopReason.Trim());

            var result = await _apiClient.SetEmergencyStopAsync(request);
            if (!result.IsSuccess || result.Value is null)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Emergency Stop Failed",
                    result.ErrorMessage ?? "Unable to update deployment emergency stop state.");
                return;
            }

            EmergencyStopReason = string.Empty;
            InvalidateActiveLeases = false;

            var errors = new List<string>();
            await LoadOperationalStatusCoreAsync(errors);
            if (CanViewAudit)
            {
                await LoadAuditEventsCoreAsync(errors);
            }

            var actionSummary = result.Value.DeploymentsPaused
                ? $"Deployments paused ({result.Value.InvalidatedLeaseCount} lease(s) invalidated). Reason: {result.Value.Reason}"
                : $"Deployments resumed. Reason: {result.Value.Reason}";
            SetFeedback(
                result.Value.DeploymentsPaused ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                result.Value.DeploymentsPaused ? "Emergency Stop Activated" : "Deployments Resumed",
                actionSummary);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteEmergencyStop() => !IsBusy && CanOperateEmergencyStop;

    private bool CanApplyFilters() => !IsBusy && CanViewAudit;

    private bool CanGoToPreviousPage() => !IsBusy && CanViewAudit && CurrentPage > 1;

    private bool CanGoToNextPage() => !IsBusy && CanViewAudit && HasNextPage();

    private bool HasNextPage() => CurrentPage * DefaultPageSize < TotalCount;

    private async Task ReloadAuditPageAsync()
    {
        if (!CanViewAudit)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var errors = new List<string>();
            await LoadAuditEventsCoreAsync(errors);
            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
            if (errors.Count > 0)
            {
                SetFeedback(InfoBarSeverity.Warning, "Audit Query Warning", string.Join(" | ", errors));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadOperationalStatusCoreAsync(List<string> errors)
    {
        var statusResult = await _apiClient.GetOperationalStatusAsync();
        if (!statusResult.IsSuccess || statusResult.Value is null)
        {
            errors.Add($"Operations Status: {statusResult.ErrorMessage ?? "Unavailable"}");
            return;
        }

        var status = statusResult.Value;
        OperationalStatusValue = status.Status;
        StorageProviderValue = status.StorageProvider;
        ProtocolVersionValue = status.ProtocolVersion;
        ActiveDeploymentLeaseCountValue = status.ActiveDeploymentLeaseCount.ToString();
        PendingOutboxCountValue = status.PendingOutboxCount.ToString();
        LastTelemetryPruneUtcValue = status.LastTelemetryPruneUtc is { } pruned
            ? pruned.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
            : "Never";

        DeploymentsPaused = status.DeploymentsPaused;
        PauseReason = string.IsNullOrWhiteSpace(status.PauseReason)
            ? "Deployments are paused by administrator emergency stop."
            : status.PauseReason;
        EmergencyStopButtonText = status.DeploymentsPaused
            ? "Resume Deployments"
            : "Pause Deployments (Emergency Stop)";
    }

    private async Task LoadAuditEventsCoreAsync(List<string> errors)
    {
        var auditResult = await _apiClient.GetAuditEventsAsync(
            CurrentPage,
            DefaultPageSize,
            SiteIdFilter,
            ActionFilter,
            ActorIdFilter,
            ResultFilter);

        if (!auditResult.IsSuccess || auditResult.Value is null)
        {
            errors.Add($"Audit Log: {auditResult.ErrorMessage ?? "Unavailable"}");
            return;
        }

        var page = auditResult.Value;
        AuditEvents.Clear();
        foreach (var item in page.Items)
        {
            AuditEvents.Add(new AuditEventItemViewModel(item));
        }

        CurrentPage = Math.Max(1, page.Page);
        TotalCount = Math.Max(0, page.TotalCount);
        EmptyAuditEventsVisibility = AuditEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageSummary();
    }

    private void UpdatePageSummary()
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)DefaultPageSize));
        PageSummaryText = $"Page {CurrentPage} of {totalPages} · {TotalCount} total event(s)";
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private async Task<MeResponse?> ResolveCurrentUserAsync()
    {
        if (_mainViewModel.CurrentUser is { } cached)
        {
            return cached;
        }

        var meResult = await _apiClient.GetMeAsync();
        if (meResult.IsSuccess && meResult.Value is not null)
        {
            return meResult.Value;
        }

        var severity = meResult.ErrorKind is ApiErrorKind.Unauthorized or ApiErrorKind.Forbidden
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;
        SetFeedback(
            severity,
            "Authentication Required",
            meResult.ErrorMessage ?? "Sign in to load audit and operational telemetry.");
        return null;
    }

    private void UpdateCapabilities(ActorIdentity actor)
    {
        CanViewOperationalHealth = ScopeAuthorizationService.CanViewOperationalHealth(actor);
        CanOperateEmergencyStop = ScopeAuthorizationService.CanOperateEmergencyStop(actor);
        CanViewAudit = ScopeAuthorizationService.CanViewAudit(actor);

        OperationalHealthVisibility = CanViewOperationalHealth ? Visibility.Visible : Visibility.Collapsed;
        EmergencyStopControlsVisibility = CanOperateEmergencyStop ? Visibility.Visible : Visibility.Collapsed;
        AuditLogVisibility = CanViewAudit ? Visibility.Visible : Visibility.Collapsed;
        AuditRestrictedNoticeVisibility = CanViewAudit ? Visibility.Collapsed : Visibility.Visible;

        var rolesDisplay = actor.Roles.Count > 0
            ? string.Join(", ", actor.Roles.Select(r => r.ToString()))
            : "None";

        RoleScopeSummaryText = (CanViewAudit, CanOperateEmergencyStop) switch
        {
            (true, true) => $"Full Audit & Emergency Stop Control · Roles: {rolesDisplay}",
            (true, false) => $"Scoped Audit & Operations Read-Only · Roles: {rolesDisplay}",
            (false, _) when CanViewOperationalHealth => $"Operational Health Read-Only (Audit log requires Auditor or Administrator) · Roles: {rolesDisplay}",
            _ => $"Access Restricted · Roles: {rolesDisplay}"
        };
    }

    private void ApplyUnauthenticatedState()
    {
        CanViewOperationalHealth = false;
        CanOperateEmergencyStop = false;
        CanViewAudit = false;
        OperationalHealthVisibility = Visibility.Collapsed;
        EmergencyStopControlsVisibility = Visibility.Collapsed;
        AuditLogVisibility = Visibility.Collapsed;
        AuditRestrictedNoticeVisibility = Visibility.Visible;
        RoleScopeSummaryText = "Not signed in";
        ResetOperationalStatusFields();
        AuditEvents.Clear();
        TotalCount = 0;
        CurrentPage = 1;
        EmptyAuditEventsVisibility = Visibility.Visible;
        UpdatePageSummary();
    }

    private void ResetOperationalStatusFields()
    {
        OperationalStatusValue = "-";
        StorageProviderValue = "-";
        ProtocolVersionValue = "-";
        ActiveDeploymentLeaseCountValue = "-";
        PendingOutboxCountValue = "-";
        LastTelemetryPruneUtcValue = "Never";
        DeploymentsPaused = false;
        PauseReason = string.Empty;
        EmergencyStopButtonText = "Pause Deployments (Emergency Stop)";
    }

    private void SetFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackSeverity = severity;
        FeedbackTitle = title;
        FeedbackMessage = message;
        StatusMessage = message;
        IsFeedbackOpen = true;
    }

    private static ActorIdentity ToActorIdentity(MeResponse me)
    {
        var roles = new HashSet<HiveRole>();
        foreach (var raw in me.Roles)
        {
            if (Enum.TryParse<HiveRole>(raw, ignoreCase: true, out var parsed))
            {
                roles.Add(parsed);
            }
        }

        var sites = new HashSet<string>(me.AllowedSiteIds, StringComparer.OrdinalIgnoreCase);
        return new ActorIdentity(
            me.UserSid,
            me.DisplayName,
            roles,
            sites,
            me.AllSitesAccess,
            IsDevice: false,
            DeviceId: null);
    }
}
