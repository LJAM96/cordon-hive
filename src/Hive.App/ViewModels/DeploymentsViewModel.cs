using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Hive.Domain.Identity;
using Hive.Domain.Software;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public sealed class ApprovedReleaseChoiceViewModel
{
    public ApprovedReleaseChoiceViewModel(SoftwareReleaseDto dto, string productName)
    {
        Dto = dto;
        ReleaseId = dto.ReleaseId;
        ProductId = dto.ProductId;
        ProductName = productName;
        VersionRaw = dto.VersionRaw;
        ValidationReference = dto.ValidationReference;
        DisplayLabel = $"{productName} {dto.VersionRaw} (Ref: {dto.ValidationReference})";
    }

    public SoftwareReleaseDto Dto { get; }
    public Guid ReleaseId { get; }
    public Guid ProductId { get; }
    public string ProductName { get; }
    public string VersionRaw { get; }
    public string ValidationReference { get; }
    public string DisplayLabel { get; }
}

public sealed partial class DeploymentWorkstationOptionViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public DeploymentWorkstationOptionViewModel(DeviceSummaryDto dto, Action selectionChanged)
    {
        _selectionChanged = selectionChanged;
        DeviceId = dto.DeviceId;
        Hostname = dto.Hostname;
        SiteId = dto.SiteId;
        ConnectivityState = dto.ConnectivityState;
        EnrollmentState = dto.EnrollmentState;
        Subtitle = $"{dto.SiteId} · {dto.ConnectivityState} · {dto.EnrollmentState}";
        AccessibilityName = $"{dto.Hostname} ({dto.SiteId}), {dto.ConnectivityState}";
    }

    public Guid DeviceId { get; }
    public string Hostname { get; }
    public string SiteId { get; }
    public string ConnectivityState { get; }
    public string EnrollmentState { get; }
    public string Subtitle { get; }
    public string AccessibilityName { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        _selectionChanged();
    }
}

public sealed class DeploymentTargetItemViewModel
{
    public DeploymentTargetItemViewModel(DeploymentTargetDto dto)
    {
        TargetId = dto.TargetId;
        JobId = dto.JobId;
        DeviceId = dto.DeviceId;
        Hostname = dto.Hostname;
        State = dto.State;
        AttemptCount = dto.AttemptCount;
        LastReasonCode = dto.LastReasonCode;
        LastMessage = dto.LastMessage;
        AttemptSummaryText = $"Attempts: {dto.AttemptCount} · Updated {dto.UpdatedUtc.ToLocalTime():g}";
        ReasonSummaryText = string.IsNullOrWhiteSpace(dto.LastReasonCode)
            ? "Awaiting agent sync"
            : string.IsNullOrWhiteSpace(dto.LastMessage)
                ? dto.LastReasonCode
                : $"{dto.LastReasonCode}: {dto.LastMessage}";
        DeferredUntilText = dto.DeferredUntilUtc is { } deferred
            ? $"Deferred until {deferred.ToLocalTime():g}"
            : string.Empty;
        DeferredVisibility = dto.DeferredUntilUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;
        StatusGlyph = dto.State switch
        {
            nameof(DeploymentState.Verified) => "\uE73E",
            nameof(DeploymentState.Installing) or nameof(DeploymentState.Verifying) or nameof(DeploymentState.Downloading) => "\uE895",
            nameof(DeploymentState.Deferred) or nameof(DeploymentState.PendingReboot) => "\uE7BA",
            nameof(DeploymentState.Failed) => "\uE783",
            nameof(DeploymentState.Cancelled) => "\uE711",
            _ => "\uE823"
        };
        AccessibilityName = $"{Hostname}: {State}, {ReasonSummaryText}";
    }

    public Guid TargetId { get; }
    public Guid JobId { get; }
    public Guid DeviceId { get; }
    public string Hostname { get; }
    public string State { get; }
    public int AttemptCount { get; }
    public string? LastReasonCode { get; }
    public string? LastMessage { get; }
    public string AttemptSummaryText { get; }
    public string ReasonSummaryText { get; }
    public string DeferredUntilText { get; }
    public Visibility DeferredVisibility { get; }
    public string StatusGlyph { get; }
    public string AccessibilityName { get; }
}

public sealed class DeploymentJobItemViewModel
{
    private static readonly HashSet<string> CancellableStates = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(DeploymentState.Pending),
        nameof(DeploymentState.Staged),
        nameof(DeploymentState.Deferred)
    };

    public DeploymentJobItemViewModel(DeploymentJobDto dto)
    {
        Dto = dto;
        JobId = dto.JobId;
        SiteId = dto.SiteId;
        ReleaseId = dto.ReleaseId;
        ProductName = dto.ProductName;
        Version = dto.Version;
        Policy = dto.Policy;
        CreatedBySid = dto.CreatedBySid;
        CreatedUtc = dto.CreatedUtc;

        var targets = dto.Targets
            .OrderBy(t => t.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(t => new DeploymentTargetItemViewModel(t))
            .ToArray();
        Targets = targets;

        var verifiedCount = dto.Targets.Count(t => string.Equals(t.State, nameof(DeploymentState.Verified), StringComparison.OrdinalIgnoreCase));
        var deferredCount = dto.Targets.Count(t => string.Equals(t.State, nameof(DeploymentState.Deferred), StringComparison.OrdinalIgnoreCase));
        var failedCount = dto.Targets.Count(t => string.Equals(t.State, nameof(DeploymentState.Failed), StringComparison.OrdinalIgnoreCase));
        CanCancelNotStarted = dto.Targets.Any(t => CancellableStates.Contains(t.State));

        TitleText = $"{dto.ProductName} {dto.Version}";
        MetadataText = $"Site: {dto.SiteId} · Policy: {dto.Policy} · Created by {dto.CreatedBySid} ({dto.CreatedUtc.ToLocalTime():g})";
        TargetSummaryText = $"{dto.Targets.Count} target(s): {verifiedCount} verified · {deferredCount} deferred · {failedCount} failed";
        AccessibilityName = $"{TitleText} ({dto.Policy}) for site {dto.SiteId}, {TargetSummaryText}";
    }

    public DeploymentJobDto Dto { get; }
    public Guid JobId { get; }
    public string SiteId { get; }
    public Guid ReleaseId { get; }
    public string ProductName { get; }
    public string Version { get; }
    public string Policy { get; }
    public string CreatedBySid { get; }
    public DateTimeOffset CreatedUtc { get; }
    public IReadOnlyList<DeploymentTargetItemViewModel> Targets { get; }
    public bool CanCancelNotStarted { get; }
    public string TitleText { get; }
    public string MetadataText { get; }
    public string TargetSummaryText { get; }
    public string AccessibilityName { get; }
}

public sealed partial class DeploymentsViewModel : ObservableObject
{
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(CreateDeploymentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSelectedJobCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string RoleScopeSummaryText { get; set; } = "Checking permissions...";

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateDeploymentCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSelectedJobCommand))]
    public partial bool CanManageDeployments { get; set; }

    [ObservableProperty]
    public partial Visibility ManageDeploymentsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility EmptyReleasesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyWorkstationsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyJobsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyTargetsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateDeploymentCommand))]
    [NotifyPropertyChangedFor(nameof(CanCreateDeployment))]
    public partial ApprovedReleaseChoiceViewModel? SelectedRelease { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateDeploymentCommand))]
    [NotifyPropertyChangedFor(nameof(CanCreateDeployment))]
    public partial string SelectedPolicy { get; set; } = nameof(DeploymentPolicy.Mandatory);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateDeploymentCommand))]
    [NotifyPropertyChangedFor(nameof(CanCreateDeployment))]
    public partial bool IsDeploymentConfirmed { get; set; }

    [ObservableProperty]
    public partial int SelectedTargetCount { get; set; }

    [ObservableProperty]
    public partial string TargetPreviewSummaryText { get; set; } = "Select an approved release and one or more workstations to preview targets.";

    [ObservableProperty]
    public partial string TargetPreviewHostnamesText { get; set; } = "No workstations selected.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSelectedJobCommand))]
    public partial DeploymentJobItemViewModel? SelectedJob { get; set; }

    [ObservableProperty]
    public partial string SelectedJobHeaderText { get; set; } = "Select a deployment job to inspect target states";

    public bool IsNotBusy => !IsBusy;

    public bool CanCreateDeployment => EvaluateCanCreateDeployment();

    public IReadOnlyList<string> Policies { get; } = new string[]
    {
        nameof(DeploymentPolicy.Mandatory),
        nameof(DeploymentPolicy.Optional),
        nameof(DeploymentPolicy.NotifyOnly),
        nameof(DeploymentPolicy.RingPilot)
    };

    public ObservableCollection<ApprovedReleaseChoiceViewModel> ApprovedReleases { get; } = [];
    public ObservableCollection<DeploymentWorkstationOptionViewModel> Workstations { get; } = [];
    public ObservableCollection<DeploymentJobItemViewModel> DeploymentJobs { get; } = [];
    public ObservableCollection<DeploymentTargetItemViewModel> SelectedJobTargets { get; } = [];

    public DeploymentsViewModel(HiveApiClient apiClient, MainViewModel mainViewModel)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreateDeployment));
    }

    partial void OnCanManageDeploymentsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreateDeployment));
    }

    partial void OnSelectedReleaseChanged(ApprovedReleaseChoiceViewModel? value)
    {
        UpdateTargetPreview();
    }

    partial void OnSelectedPolicyChanged(string value)
    {
        UpdateTargetPreview();
    }

    partial void OnSelectedJobChanged(DeploymentJobItemViewModel? value)
    {
        ApplySelectedJob(value);
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

            var productsTask = _apiClient.GetSoftwareProductsAsync();
            var releasesTask = _apiClient.GetSoftwareReleasesAsync();
            var workstationsTask = _apiClient.GetWorkstationsAsync();
            var deploymentsTask = _apiClient.GetDeploymentsAsync();

            await Task.WhenAll(productsTask, releasesTask, workstationsTask, deploymentsTask);

            var errors = new List<string>();

            var productNames = new Dictionary<Guid, string>();
            if (productsTask.Result.IsSuccess && productsTask.Result.Value is not null)
            {
                foreach (var product in productsTask.Result.Value)
                {
                    productNames[product.ProductId] = product.Name;
                }
            }
            else if (!productsTask.Result.IsSuccess && productsTask.Result.ErrorMessage is { } prodErr)
            {
                errors.Add($"Products: {prodErr}");
            }

            var prevReleaseId = SelectedRelease?.ReleaseId;
            ApprovedReleases.Clear();
            ApprovedReleaseChoiceViewModel? restoredRelease = null;
            if (releasesTask.Result.IsSuccess && releasesTask.Result.Value is not null)
            {
                foreach (var release in releasesTask.Result.Value
                             .Where(r => string.Equals(r.ApprovalState, nameof(ReleaseApprovalState.Approved), StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(r => r.ApprovedAtUtc ?? r.CreatedAtUtc))
                {
                    var productName = productNames.TryGetValue(release.ProductId, out var name)
                        ? name
                        : release.ProductId.ToString("D");
                    var vm = new ApprovedReleaseChoiceViewModel(release, productName);
                    ApprovedReleases.Add(vm);
                    if (prevReleaseId == release.ReleaseId)
                    {
                        restoredRelease = vm;
                    }
                }
            }
            else if (!releasesTask.Result.IsSuccess && releasesTask.Result.ErrorMessage is { } relErr)
            {
                errors.Add($"Releases: {relErr}");
            }

            EmptyReleasesVisibility = ApprovedReleases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedRelease = restoredRelease ?? ApprovedReleases.FirstOrDefault();

            var previouslySelectedIds = Workstations
                .Where(w => w.IsSelected)
                .Select(w => w.DeviceId)
                .ToHashSet();
            Workstations.Clear();
            if (workstationsTask.Result.IsSuccess && workstationsTask.Result.Value is not null)
            {
                foreach (var device in workstationsTask.Result.Value
                             .Where(d => actor.AllSitesAccess || actor.AllowedSiteIds.Contains(d.SiteId))
                             .OrderBy(d => d.SiteId, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase))
                {
                    var option = new DeploymentWorkstationOptionViewModel(device, OnWorkstationSelectionChanged)
                    {
                        IsSelected = previouslySelectedIds.Contains(device.DeviceId)
                    };
                    Workstations.Add(option);
                }
            }
            else if (!workstationsTask.Result.IsSuccess && workstationsTask.Result.ErrorMessage is { } wsErr)
            {
                errors.Add($"Workstations: {wsErr}");
            }

            EmptyWorkstationsVisibility = Workstations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTargetPreview();

            var prevJobId = SelectedJob?.JobId;
            DeploymentJobs.Clear();
            DeploymentJobItemViewModel? restoredJob = null;
            if (deploymentsTask.Result.IsSuccess && deploymentsTask.Result.Value is not null)
            {
                foreach (var job in deploymentsTask.Result.Value.OrderByDescending(j => j.CreatedUtc))
                {
                    var vm = new DeploymentJobItemViewModel(job);
                    DeploymentJobs.Add(vm);
                    if (prevJobId == job.JobId)
                    {
                        restoredJob = vm;
                    }
                }
            }
            else if (!deploymentsTask.Result.IsSuccess && deploymentsTask.Result.ErrorMessage is { } depErr)
            {
                errors.Add($"Deployments: {depErr}");
            }

            EmptyJobsVisibility = DeploymentJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedJob = restoredJob ?? DeploymentJobs.FirstOrDefault();
            ApplySelectedJob(SelectedJob);

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

    [RelayCommand(CanExecute = nameof(CanCreateDeployment))]
    private async Task CreateDeploymentAsync()
    {
        if (!CanManageDeployments)
        {
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", "Only SoftwareAdmin or Administrator roles can create deployment jobs.");
            return;
        }

        if (SelectedRelease is null)
        {
            SetFeedback(InfoBarSeverity.Warning, "Release Required", "Select an approved software release before deploying.");
            return;
        }

        var selectedWorkstations = Workstations.Where(w => w.IsSelected).ToList();
        if (selectedWorkstations.Count == 0)
        {
            SetFeedback(InfoBarSeverity.Warning, "Targets Required", "Select at least one workstation target.");
            return;
        }

        var distinctSites = selectedWorkstations
            .Select(w => w.SiteId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctSites.Count != 1)
        {
            SetFeedback(
                InfoBarSeverity.Warning,
                "Single Site Required",
                "All selected target workstations must belong to the same site for a deployment job.");
            return;
        }

        if (!IsDeploymentConfirmed)
        {
            SetFeedback(
                InfoBarSeverity.Warning,
                "Confirmation Required",
                "Explicitly confirm the target count and hostnames before creating a deployment job.");
            return;
        }

        IsBusy = true;
        try
        {
            var request = new CreateDeploymentJobRequest(
                SiteId: distinctSites[0],
                ReleaseId: SelectedRelease.ReleaseId,
                TargetDeviceIds: selectedWorkstations.Select(w => w.DeviceId).ToArray(),
                Policy: SelectedPolicy,
                Confirmed: true);

            var result = await _apiClient.CreateDeploymentJobAsync(request);
            if (!result.IsSuccess || result.Value is null)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Create Deployment Failed",
                    result.ErrorMessage ?? "Unable to create deployment job.");
                return;
            }

            IsDeploymentConfirmed = false;
            foreach (var ws in Workstations)
            {
                ws.IsSelected = false;
            }

            await LoadAsync();
            SetFeedback(
                InfoBarSeverity.Success,
                "Deployment Job Created",
                $"Created {result.Value.Policy} deployment for {result.Value.ProductName} {result.Value.Version} targeting {result.Value.Targets.Count} workstation(s).");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelSelectedJob))]
    private Task CancelSelectedJobAsync() =>
        SelectedJob is { } job ? CancelNotStartedAsync(job.JobId) : Task.CompletedTask;

    public async Task CancelNotStartedAsync(Guid jobId)
    {
        if (jobId == Guid.Empty)
        {
            return;
        }

        if (!CanManageDeployments)
        {
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", "Only SoftwareAdmin or Administrator roles can cancel deployments.");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.CancelDeploymentJobAsync(jobId);
            if (!result.IsSuccess)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Cancel Deployment Failed",
                    result.ErrorMessage ?? "Unable to cancel not-started targets.");
                return;
            }

            await LoadAsync();
            SetFeedback(
                InfoBarSeverity.Success,
                "Not-Started Targets Cancelled",
                "Cancelled all Pending, Staged, and Deferred targets for the selected deployment job.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCancelSelectedJob() =>
        !IsBusy &&
        CanManageDeployments &&
        SelectedJob is { CanCancelNotStarted: true };

    private bool EvaluateCanCreateDeployment()
    {
        if (IsBusy ||
            !CanManageDeployments ||
            SelectedRelease is null ||
            string.IsNullOrWhiteSpace(SelectedPolicy) ||
            !IsDeploymentConfirmed ||
            SelectedTargetCount <= 0)
        {
            return false;
        }

        var distinctSites = Workstations
            .Where(w => w.IsSelected)
            .Select(w => w.SiteId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinctSites == 1;
    }

    private void OnWorkstationSelectionChanged()
    {
        UpdateTargetPreview();
    }

    private void UpdateTargetPreview()
    {
        var selected = Workstations.Where(w => w.IsSelected).ToList();
        SelectedTargetCount = selected.Count;

        if (selected.Count == 0)
        {
            TargetPreviewSummaryText = SelectedRelease is null
                ? "Select an approved release and one or more workstations to preview targets."
                : $"Release: {SelectedRelease.DisplayLabel} · Policy: {SelectedPolicy} · 0 targets selected";
            TargetPreviewHostnamesText = "No workstations selected.";
        }
        else
        {
            var sites = selected.Select(w => w.SiteId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var siteNote = sites.Count == 1
                ? $"Site: {sites[0]}"
                : $"Multiple sites selected ({string.Join(", ", sites)}) — select workstations from a single site";
            var releaseLabel = SelectedRelease?.DisplayLabel ?? "(No release selected)";
            TargetPreviewSummaryText = $"{selected.Count} target(s) · {siteNote} · {releaseLabel} · Policy: {SelectedPolicy}";
            TargetPreviewHostnamesText = string.Join(", ", selected.Select(w => w.Hostname));
        }

        OnPropertyChanged(nameof(CanCreateDeployment));
        CreateDeploymentCommand.NotifyCanExecuteChanged();
    }

    private void ApplySelectedJob(DeploymentJobItemViewModel? job)
    {
        SelectedJobTargets.Clear();
        if (job is null)
        {
            SelectedJobHeaderText = "Select a deployment job to inspect target states";
            EmptyTargetsVisibility = Visibility.Visible;
            CancelSelectedJobCommand.NotifyCanExecuteChanged();
            return;
        }

        SelectedJobHeaderText = $"{job.TitleText} · {job.Policy} · {job.TargetSummaryText}";
        foreach (var target in job.Targets)
        {
            SelectedJobTargets.Add(target);
        }

        EmptyTargetsVisibility = SelectedJobTargets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelSelectedJobCommand.NotifyCanExecuteChanged();
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
            meResult.ErrorMessage ?? "Sign in to load deployments.");
        return null;
    }

    private void UpdateCapabilities(ActorIdentity actor)
    {
        CanManageDeployments = !actor.IsDevice && actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.Administrator);
        ManageDeploymentsVisibility = CanManageDeployments ? Visibility.Visible : Visibility.Collapsed;

        var rolesDisplay = actor.Roles.Count > 0
            ? string.Join(", ", actor.Roles.Select(r => r.ToString()))
            : "None";

        RoleScopeSummaryText = CanManageDeployments
            ? $"Safe Deployment Orchestration · Roles: {rolesDisplay}"
            : $"Deployment Visibility (Read-Only) · Roles: {rolesDisplay}";
    }

    private void ApplyUnauthenticatedState()
    {
        CanManageDeployments = false;
        ManageDeploymentsVisibility = Visibility.Collapsed;
        RoleScopeSummaryText = "Not signed in";
        ApprovedReleases.Clear();
        Workstations.Clear();
        DeploymentJobs.Clear();
        SelectedJobTargets.Clear();
        EmptyReleasesVisibility = Visibility.Visible;
        EmptyWorkstationsVisibility = Visibility.Visible;
        EmptyJobsVisibility = Visibility.Visible;
        EmptyTargetsVisibility = Visibility.Visible;
        SelectedJobHeaderText = "Sign in required";
        UpdateTargetPreview();
    }

    private void SetFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackSeverity = severity;
        FeedbackTitle = title;
        FeedbackMessage = message;
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
