using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Identity;
using Hive.Domain.Software;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public static class CompliancePresentation
{
    public static ComplianceStatus ParseStatus(string? rawStatus)
    {
        if (!string.IsNullOrWhiteSpace(rawStatus) &&
            Enum.TryParse<ComplianceStatus>(rawStatus.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return ComplianceStatus.Unknown;
    }

    public static string GetStatusText(ComplianceStatus status) => status switch
    {
        ComplianceStatus.Compliant => "Compliant",
        ComplianceStatus.UpdateRequired => "Update Required",
        ComplianceStatus.AheadUnapproved => "Ahead (Unapproved)",
        ComplianceStatus.NotInstalled => "Not Installed",
        ComplianceStatus.Exempt => "Exempt",
        ComplianceStatus.PendingReboot => "Pending Reboot",
        _ => "Unknown"
    };

    public static string GetStatusGlyph(ComplianceStatus status) => status switch
    {
        ComplianceStatus.Compliant => "\uE73E",        // CheckMark
        ComplianceStatus.UpdateRequired => "\uE7BA",   // Warning
        ComplianceStatus.AheadUnapproved => "\uE783",  // Error
        ComplianceStatus.NotInstalled => "\uE711",     // Cancel
        ComplianceStatus.Exempt => "\uE72E",           // Lock / Exemption
        ComplianceStatus.PendingReboot => "\uE777",    // UpdateRestore
        _ => "\uE9CE"                                  // Unknown
    };

    public static string FormatLocalTimestamp(DateTimeOffset? utc, string fallback = "Never") =>
        utc is { } value ? value.ToLocalTime().ToString("g") : fallback;
}

public sealed class DeviceComplianceItemViewModel
{
    public DeviceComplianceItemViewModel(DeviceComplianceSummaryDto dto)
    {
        Dto = dto;
        DeviceId = dto.DeviceId;
        Hostname = dto.Hostname;
        SiteId = dto.SiteId;
        LastInventoryLocalText = $"Inventory {CompliancePresentation.FormatLocalTimestamp(dto.LastInventoryAtUtc, "Never reported")}";

        var overallStatus = ResolveSummaryStatus(dto);
        SummaryStatusText = CompliancePresentation.GetStatusText(overallStatus);
        SummaryStatusGlyph = CompliancePresentation.GetStatusGlyph(overallStatus);
        SummaryCountsText =
            $"{dto.CompliantCount} compliant · {dto.UpdateRequiredCount} update · {dto.AheadUnapprovedCount} ahead · {dto.NotInstalledCount} missing · {dto.ExemptCount} exempt · {dto.UnknownCount} unknown";
        AccessibilityName = $"{Hostname} ({SiteId}): {SummaryStatusText}. {SummaryCountsText}";
    }

    public DeviceComplianceSummaryDto Dto { get; }
    public Guid DeviceId { get; }
    public string Hostname { get; }
    public string SiteId { get; }
    public string SummaryStatusText { get; }
    public string SummaryStatusGlyph { get; }
    public string SummaryCountsText { get; }
    public string LastInventoryLocalText { get; }
    public string AccessibilityName { get; }

    private static ComplianceStatus ResolveSummaryStatus(DeviceComplianceSummaryDto dto)
    {
        if (dto.UnknownCount > 0) return ComplianceStatus.Unknown;
        if (dto.AheadUnapprovedCount > 0) return ComplianceStatus.AheadUnapproved;
        if (dto.UpdateRequiredCount > 0) return ComplianceStatus.UpdateRequired;
        if (dto.NotInstalledCount > 0) return ComplianceStatus.NotInstalled;
        if (dto.ExemptCount > 0) return ComplianceStatus.Exempt;
        if (dto.CompliantCount > 0) return ComplianceStatus.Compliant;
        return ComplianceStatus.Unknown;
    }
}

public sealed class ProductComplianceItemViewModel
{
    public ProductComplianceItemViewModel(DeviceProductComplianceDto dto)
    {
        ProductId = dto.ProductId;
        ProductName = dto.ProductName;
        Hostname = dto.Hostname;
        SiteId = dto.SiteId;

        var status = CompliancePresentation.ParseStatus(dto.ComplianceStatus);
        StatusText = CompliancePresentation.GetStatusText(status);
        StatusGlyph = CompliancePresentation.GetStatusGlyph(status);

        ObservedVersionDisplay = string.IsNullOrWhiteSpace(dto.ObservedVersionRaw) ? "Not detected" : dto.ObservedVersionRaw;
        ApprovedVersionDisplay = string.IsNullOrWhiteSpace(dto.ApprovedVersionRaw) ? "None approved" : dto.ApprovedVersionRaw;
        VersionsSummaryText = $"Observed: {ObservedVersionDisplay}  |  Approved: {ApprovedVersionDisplay}";
        Reason = string.IsNullOrWhiteSpace(dto.Reason) ? "No evaluation details." : dto.Reason;

        ExemptionExpiryLocalText = dto.ExemptionExpiresAtUtc is { } exp
            ? $"Exemption expires {exp.ToLocalTime():g}"
            : string.Empty;
        ExemptionExpiryVisibility = dto.ExemptionExpiresAtUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;

        LastInventoryLocalText = $"Checked {CompliancePresentation.FormatLocalTimestamp(dto.LastInventoryAtUtc, "Never")}";
        StatusAccessibilityName = $"{ProductName}: {StatusText}, {VersionsSummaryText}";
    }

    public Guid ProductId { get; }
    public string ProductName { get; }
    public string Hostname { get; }
    public string SiteId { get; }
    public string StatusText { get; }
    public string StatusGlyph { get; }
    public string ObservedVersionDisplay { get; }
    public string ApprovedVersionDisplay { get; }
    public string VersionsSummaryText { get; }
    public string Reason { get; }
    public string ExemptionExpiryLocalText { get; }
    public Visibility ExemptionExpiryVisibility { get; }
    public string LastInventoryLocalText { get; }
    public string StatusAccessibilityName { get; }
}

public sealed class DiscoveredSoftwareItemViewModel
{
    public DiscoveredSoftwareItemViewModel(InstalledSoftwareItemDto dto)
    {
        DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? "(Unnamed software)" : dto.DisplayName;
        PublisherDisplay = string.IsNullOrWhiteSpace(dto.Publisher) ? "Unknown publisher" : dto.Publisher;
        VersionDisplay = string.IsNullOrWhiteSpace(dto.ObservedVersionRaw) ? "Version unknown" : dto.ObservedVersionRaw;
        ProvenanceText = $"{dto.DetectionSource} · {dto.InstallScope} · Confidence: {dto.ConfidenceState}";
        ObservedLocalText = CompliancePresentation.FormatLocalTimestamp(dto.ObservedAtUtc, "Unknown");
    }

    public string DisplayName { get; }
    public string PublisherDisplay { get; }
    public string VersionDisplay { get; }
    public string ProvenanceText { get; }
    public string ObservedLocalText { get; }
}

public sealed class SoftwareProductItemViewModel
{
    public SoftwareProductItemViewModel(SoftwareProductDto dto)
    {
        Dto = dto;
        ProductId = dto.ProductId;
        Name = dto.Name;
        Publisher = dto.Publisher;
        VersionScheme = dto.VersionScheme;
        MatchPattern = dto.MatchPattern;
        DetectionRevision = dto.DetectionRevision;
        ProtectedProcessesDisplay = dto.ProtectedProcessNames.Count > 0
            ? $"Protected: {string.Join(", ", dto.ProtectedProcessNames)}"
            : "Protected: None";
        Subtitle = $"{Publisher} · Scheme: {VersionScheme} · Pattern: \"{MatchPattern}\" · Rev {DetectionRevision}";
    }

    public SoftwareProductDto Dto { get; }
    public Guid ProductId { get; }
    public string Name { get; }
    public string Publisher { get; }
    public string VersionScheme { get; }
    public string MatchPattern { get; }
    public long DetectionRevision { get; }
    public string ProtectedProcessesDisplay { get; }
    public string Subtitle { get; }
}

public sealed class SoftwareReleaseItemViewModel
{
    public SoftwareReleaseItemViewModel(SoftwareReleaseDto dto, string productName, string? currentUserSid)
    {
        Dto = dto;
        ReleaseId = dto.ReleaseId;
        ProductId = dto.ProductId;
        ProductName = productName;
        VersionRaw = dto.VersionRaw;
        ApprovalState = dto.ApprovalState;
        IsDraft = string.Equals(dto.ApprovalState, nameof(ReleaseApprovalState.Draft), StringComparison.OrdinalIgnoreCase);
        IsSelfAuthored = !string.IsNullOrWhiteSpace(currentUserSid) &&
                         string.Equals(dto.CreatedBySid, currentUserSid, StringComparison.OrdinalIgnoreCase);

        StatusText = ApprovalState;
        StatusGlyph = ApprovalState switch
        {
            nameof(ReleaseApprovalState.Approved) => "\uE73E",
            nameof(ReleaseApprovalState.Draft) => "\uE70F",
            nameof(ReleaseApprovalState.Withdrawn) => "\uE711",
            _ => "\uE9CE"
        };

        var authorNote = IsSelfAuthored ? $"{dto.CreatedBySid} (you — 403 if self-approved)" : dto.CreatedBySid;
        var approverNote = string.IsNullOrWhiteSpace(dto.ApprovedBySid)
            ? "Pending approval"
            : $"Approved by {dto.ApprovedBySid} ({CompliancePresentation.FormatLocalTimestamp(dto.ApprovedAtUtc, "Unknown")})";

        MetadataText = $"Validation: {dto.ValidationReference} · Scheme: {dto.VersionScheme} · Created by {authorNote}";
        ApprovalDetailText = $"{approverNote} · Created {CompliancePresentation.FormatLocalTimestamp(dto.CreatedAtUtc, "Unknown")}";
        StatusAccessibilityName = $"{ProductName} {VersionRaw}: {StatusText}";
    }

    public SoftwareReleaseDto Dto { get; }
    public Guid ReleaseId { get; }
    public Guid ProductId { get; }
    public string ProductName { get; }
    public string VersionRaw { get; }
    public string ApprovalState { get; }
    public bool IsDraft { get; }
    public bool IsSelfAuthored { get; }
    public string StatusText { get; }
    public string StatusGlyph { get; }
    public string MetadataText { get; }
    public string ApprovalDetailText { get; }
    public string StatusAccessibilityName { get; }
}

public sealed class SoftwareExemptionItemViewModel
{
    public SoftwareExemptionItemViewModel(SoftwareExemptionDto dto, string productName, string scopeLabel)
    {
        Dto = dto;
        ExemptionId = dto.ExemptionId;
        ProductName = productName;
        ScopeLabel = scopeLabel;
        AllowedVersionDisplay = string.IsNullOrWhiteSpace(dto.AllowedVersionRaw)
            ? "Any version"
            : $"Version {dto.AllowedVersionRaw}";
        Reason = dto.Reason;
        IsActive = dto.IsActive;

        if (dto.RevokedAtUtc is not null)
        {
            StatusText = "Revoked";
            StatusGlyph = "\uE711";
        }
        else if (dto.IsActive)
        {
            StatusText = "Active";
            StatusGlyph = "\uE72E";
        }
        else
        {
            StatusText = "Expired";
            StatusGlyph = "\uE823";
        }

        WindowText =
            $"{CompliancePresentation.FormatLocalTimestamp(dto.StartsAtUtc, "—")} → {CompliancePresentation.FormatLocalTimestamp(dto.ExpiresAtUtc, "—")}";
        ApprovedByText = $"Approved by {dto.ApprovedBySid}";
        StatusAccessibilityName = $"{ProductName} ({ScopeLabel}): {StatusText}";
    }

    public SoftwareExemptionDto Dto { get; }
    public Guid ExemptionId { get; }
    public string ProductName { get; }
    public string ScopeLabel { get; }
    public string AllowedVersionDisplay { get; }
    public string Reason { get; }
    public bool IsActive { get; }
    public string StatusText { get; }
    public string StatusGlyph { get; }
    public string WindowText { get; }
    public string ApprovedByText { get; }
    public string StatusAccessibilityName { get; }
}

public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string RoleScopeSummaryText { get; set; } = "Checking permissions...";

    [ObservableProperty]
    public partial string LastRefreshedLocalText { get; set; } = "Never";

    [ObservableProperty]
    public partial string SelectedDeviceHeaderText { get; set; } = "No workstation selected";

    [ObservableProperty]
    public partial bool IsFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity FeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string FeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanViewFleetCompliance { get; set; }

    [ObservableProperty]
    public partial bool CanManageCatalogue { get; set; }

    [ObservableProperty]
    public partial bool CanApproveReleases { get; set; }

    [ObservableProperty]
    public partial bool CanManageExemptions { get; set; }

    [ObservableProperty]
    public partial Visibility FleetSectionVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ManageCatalogueVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ApproveReleasesVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ManageExemptionsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility EmptyDevicesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyComplianceVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyDiscoveredVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyReleasesVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyExemptionsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial DeviceComplianceItemViewModel? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial SoftwareReleaseItemViewModel? SelectedRelease { get; set; }

    [ObservableProperty]
    public partial SoftwareExemptionItemViewModel? SelectedExemption { get; set; }

    public bool IsNotBusy => !IsBusy;

    public ObservableCollection<DeviceComplianceItemViewModel> Devices { get; } = [];
    public ObservableCollection<ProductComplianceItemViewModel> ProductComplianceRows { get; } = [];
    public ObservableCollection<DiscoveredSoftwareItemViewModel> DiscoveredSoftware { get; } = [];
    public ObservableCollection<SoftwareProductItemViewModel> Products { get; } = [];
    public ObservableCollection<SoftwareReleaseItemViewModel> Releases { get; } = [];
    public ObservableCollection<SoftwareExemptionItemViewModel> Exemptions { get; } = [];

    public SoftwareViewModel(HiveApiClient apiClient, MainViewModel mainViewModel)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
    }

    partial void OnIsBusyChanged(bool value)
    {
        ApproveSelectedReleaseCommand.NotifyCanExecuteChanged();
        RevokeSelectedExemptionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDeviceChanged(DeviceComplianceItemViewModel? value)
    {
        ApplySelectedDeviceCompliance(value);
    }

    partial void OnSelectedReleaseChanged(SoftwareReleaseItemViewModel? value)
    {
        ApproveSelectedReleaseCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedExemptionChanged(SoftwareExemptionItemViewModel? value)
    {
        RevokeSelectedExemptionCommand.NotifyCanExecuteChanged();
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

            if (CanViewFleetCompliance)
            {
                await LoadFleetComplianceViewAsync(actor);
            }
            else
            {
                await LoadExaminerComplianceViewAsync();
            }

            LastRefreshedLocalText = $"Updated {DateTimeOffset.Now:g}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> AddProductAsync(
        string name,
        string publisher,
        string versionScheme,
        string matchPattern,
        string? protectedProcessNamesCsv)
    {
        if (!CanManageCatalogue)
        {
            const string denied = "Only SoftwareAdmin or Administrator roles can add software products.";
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", denied);
            return denied;
        }

        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(publisher) ||
            string.IsNullOrWhiteSpace(matchPattern))
        {
            return "Name, Publisher, and Match Pattern are required.";
        }

        var processes = string.IsNullOrWhiteSpace(protectedProcessNamesCsv)
            ? Array.Empty<string>()
            : protectedProcessNamesCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var request = new CreateSoftwareProductRequest(
            name.Trim(),
            publisher.Trim(),
            versionScheme,
            matchPattern.Trim(),
            processes);

        var result = await _apiClient.CreateSoftwareProductAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            var msg = result.ErrorMessage ?? "Unable to create software product.";
            SetFeedback(InfoBarSeverity.Error, "Add Product Failed", msg);
            return msg;
        }

        await LoadAsync();
        SetFeedback(InfoBarSeverity.Success, "Product Added", $"Added '{result.Value.Name}' ({result.Value.VersionScheme}).");
        return null;
    }

    public async Task<string?> DraftReleaseAsync(
        Guid productId,
        string versionRaw,
        string validationReference,
        string? packageSha256,
        string? manifestDigest)
    {
        if (!CanManageCatalogue)
        {
            const string denied = "Only SoftwareAdmin or Administrator roles can draft software releases.";
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", denied);
            return denied;
        }

        if (productId == Guid.Empty ||
            string.IsNullOrWhiteSpace(versionRaw) ||
            string.IsNullOrWhiteSpace(validationReference))
        {
            return "Product, Version, and Validation Reference are required.";
        }

        var request = new CreateSoftwareReleaseRequest(
            productId,
            versionRaw.Trim(),
            validationReference.Trim(),
            string.IsNullOrWhiteSpace(packageSha256) ? null : packageSha256.Trim(),
            string.IsNullOrWhiteSpace(manifestDigest) ? null : manifestDigest.Trim());

        var result = await _apiClient.CreateSoftwareReleaseAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            var msg = result.ErrorMessage ?? "Unable to draft software release.";
            SetFeedback(InfoBarSeverity.Error, "Draft Release Failed", msg);
            return msg;
        }

        await LoadAsync();
        SetFeedback(InfoBarSeverity.Success, "Release Drafted", $"Drafted release {result.Value.VersionRaw} (pending independent approval).");
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanApproveSelectedRelease))]
    private async Task ApproveSelectedReleaseAsync()
    {
        if (SelectedRelease is null)
        {
            return;
        }

        if (!CanApproveReleases)
        {
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", "Only SoftwareApprover or Administrator roles can approve releases.");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.ApproveSoftwareReleaseAsync(
                SelectedRelease.ReleaseId,
                new ApproveSoftwareReleaseRequest("Approved via Cordon Hive UI"));

            if (!result.IsSuccess || result.Value is null)
            {
                var title = result.ErrorKind == ApiErrorKind.Forbidden
                    ? "Approval Denied (403 Forbidden)"
                    : "Release Approval Failed";
                var msg = result.ErrorMessage ?? "Release approval was rejected by the server.";
                SetFeedback(InfoBarSeverity.Error, title, msg);
                return;
            }

            await LoadAsync();
            SetFeedback(
                InfoBarSeverity.Success,
                "Release Approved",
                $"Approved {result.Value.VersionRaw} ({result.Value.ValidationReference}).");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApproveSelectedRelease() =>
        !IsBusy &&
        CanApproveReleases &&
        SelectedRelease is { IsDraft: true };

    public async Task<string?> AddExemptionAsync(
        Guid? deviceId,
        Guid productId,
        string? allowedVersionRaw,
        string reason,
        int durationDays)
    {
        if (!CanManageExemptions)
        {
            const string denied = "Only SoftwareApprover or Administrator roles can create exemptions.";
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", denied);
            return denied;
        }

        if (productId == Guid.Empty || string.IsNullOrWhiteSpace(reason))
        {
            return "Product and Reason are required.";
        }

        var safeDays = Math.Clamp(durationDays, 1, 365);
        var startsAtUtc = DateTimeOffset.UtcNow;
        var expiresAtUtc = startsAtUtc.AddDays(safeDays);

        var request = new CreateSoftwareExemptionRequest(
            deviceId,
            productId,
            string.IsNullOrWhiteSpace(allowedVersionRaw) ? null : allowedVersionRaw.Trim(),
            reason.Trim(),
            startsAtUtc,
            expiresAtUtc);

        var result = await _apiClient.CreateSoftwareExemptionAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            var msg = result.ErrorMessage ?? "Unable to create software exemption.";
            SetFeedback(InfoBarSeverity.Error, "Add Exemption Failed", msg);
            return msg;
        }

        await LoadAsync();
        SetFeedback(
            InfoBarSeverity.Success,
            "Exemption Created",
            $"Exemption active until {result.Value.ExpiresAtUtc.ToLocalTime():g}.");
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRevokeSelectedExemption))]
    private async Task RevokeSelectedExemptionAsync()
    {
        if (SelectedExemption is null || !CanManageExemptions)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.RevokeSoftwareExemptionAsync(
                SelectedExemption.ExemptionId,
                new RevokeSoftwareExemptionRequest("Revoked via Cordon Hive UI"));

            if (!result.IsSuccess)
            {
                SetFeedback(
                    InfoBarSeverity.Error,
                    "Revoke Exemption Failed",
                    result.ErrorMessage ?? "Unable to revoke exemption.");
                return;
            }

            await LoadAsync();
            SetFeedback(InfoBarSeverity.Success, "Exemption Revoked", "The software exemption has been revoked.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRevokeSelectedExemption() =>
        !IsBusy &&
        CanManageExemptions &&
        SelectedExemption is { IsActive: true };

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
            meResult.ErrorMessage ?? "Sign in to load software compliance data.");
        return null;
    }

    private void UpdateCapabilities(ActorIdentity actor)
    {
        CanViewFleetCompliance = ScopeAuthorizationService.CanViewFleetCompliance(actor);
        CanManageCatalogue = ScopeAuthorizationService.CanManageSoftwareCatalogue(actor);
        CanApproveReleases = !actor.IsDevice && actor.HasAnyRole(HiveRole.SoftwareApprover, HiveRole.Administrator);
        CanManageExemptions = ScopeAuthorizationService.CanManageSoftwareExemptions(actor);

        FleetSectionVisibility = CanViewFleetCompliance ? Visibility.Visible : Visibility.Collapsed;
        ManageCatalogueVisibility = CanManageCatalogue ? Visibility.Visible : Visibility.Collapsed;
        ApproveReleasesVisibility = CanApproveReleases ? Visibility.Visible : Visibility.Collapsed;
        ManageExemptionsVisibility = CanManageExemptions ? Visibility.Visible : Visibility.Collapsed;

        var rolesDisplay = actor.Roles.Count > 0
            ? string.Join(", ", actor.Roles.Select(r => r.ToString()))
            : "None";

        RoleScopeSummaryText = CanViewFleetCompliance
            ? $"Fleet Compliance View · Roles: {rolesDisplay}"
            : $"Examiner Assigned Workstation View · Roles: {rolesDisplay}";

        ApproveSelectedReleaseCommand.NotifyCanExecuteChanged();
        RevokeSelectedExemptionCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadFleetComplianceViewAsync(ActorIdentity actor)
    {
        var complianceTask = _apiClient.GetFleetComplianceAsync();
        var productsTask = _apiClient.GetSoftwareProductsAsync();
        var releasesTask = _apiClient.GetSoftwareReleasesAsync();
        var exemptionsTask = _apiClient.GetSoftwareExemptionsAsync();

        await Task.WhenAll(complianceTask, productsTask, releasesTask, exemptionsTask);

        var errors = new List<string>();

        Products.Clear();
        var productNames = new Dictionary<Guid, string>();
        if (productsTask.Result.IsSuccess && productsTask.Result.Value is not null)
        {
            foreach (var product in productsTask.Result.Value.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                productNames[product.ProductId] = product.Name;
                Products.Add(new SoftwareProductItemViewModel(product));
            }
        }
        else if (!productsTask.Result.IsSuccess && productsTask.Result.ErrorMessage is { } prodErr)
        {
            errors.Add($"Products: {prodErr}");
        }

        var selectedDeviceId = SelectedDevice?.DeviceId;
        Devices.Clear();
        var deviceNames = new Dictionary<Guid, string>();
        DeviceComplianceItemViewModel? restoredDevice = null;

        if (complianceTask.Result.IsSuccess && complianceTask.Result.Value is not null)
        {
            foreach (var device in complianceTask.Result.Value.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                deviceNames[device.DeviceId] = $"{device.Hostname} ({device.SiteId})";
                var vm = new DeviceComplianceItemViewModel(device);
                Devices.Add(vm);
                if (selectedDeviceId == device.DeviceId)
                {
                    restoredDevice = vm;
                }
            }
        }
        else if (!complianceTask.Result.IsSuccess && complianceTask.Result.ErrorMessage is { } compErr)
        {
            errors.Add($"Compliance: {compErr}");
        }

        EmptyDevicesVisibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedDevice = restoredDevice ?? Devices.FirstOrDefault();
        ApplySelectedDeviceCompliance(SelectedDevice);

        var selectedReleaseId = SelectedRelease?.ReleaseId;
        Releases.Clear();
        SoftwareReleaseItemViewModel? restoredRelease = null;
        if (releasesTask.Result.IsSuccess && releasesTask.Result.Value is not null)
        {
            foreach (var release in releasesTask.Result.Value
                         .OrderByDescending(r => r.CreatedAtUtc))
            {
                var productName = productNames.TryGetValue(release.ProductId, out var name)
                    ? name
                    : release.ProductId.ToString("D");
                var vm = new SoftwareReleaseItemViewModel(release, productName, actor.UserSid);
                Releases.Add(vm);
                if (selectedReleaseId == release.ReleaseId)
                {
                    restoredRelease = vm;
                }
            }
        }
        else if (!releasesTask.Result.IsSuccess && releasesTask.Result.ErrorMessage is { } relErr)
        {
            errors.Add($"Releases: {relErr}");
        }

        EmptyReleasesVisibility = Releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedRelease = restoredRelease;

        var selectedExemptionId = SelectedExemption?.ExemptionId;
        Exemptions.Clear();
        SoftwareExemptionItemViewModel? restoredExemption = null;
        if (exemptionsTask.Result.IsSuccess && exemptionsTask.Result.Value is not null)
        {
            foreach (var exemption in exemptionsTask.Result.Value
                         .OrderByDescending(e => e.IsActive)
                         .ThenBy(e => e.ExpiresAtUtc))
            {
                var productName = productNames.TryGetValue(exemption.ProductId, out var name)
                    ? name
                    : exemption.ProductId.ToString("D");
                var scopeLabel = exemption.DeviceId is { } did
                    ? deviceNames.TryGetValue(did, out var host) ? host : did.ToString("D")
                    : "All Workstations (Global)";
                var vm = new SoftwareExemptionItemViewModel(exemption, productName, scopeLabel);
                Exemptions.Add(vm);
                if (selectedExemptionId == exemption.ExemptionId)
                {
                    restoredExemption = vm;
                }
            }
        }
        else if (!exemptionsTask.Result.IsSuccess && exemptionsTask.Result.ErrorMessage is { } exErr)
        {
            errors.Add($"Exemptions: {exErr}");
        }

        EmptyExemptionsVisibility = Exemptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedExemption = restoredExemption;

        if (errors.Count > 0)
        {
            SetFeedback(InfoBarSeverity.Warning, "Partial Load Warning", string.Join(" | ", errors));
        }
        else if (IsFeedbackOpen && FeedbackSeverity == InfoBarSeverity.Warning)
        {
            IsFeedbackOpen = false;
        }
    }

    private async Task LoadExaminerComplianceViewAsync()
    {
        Products.Clear();
        Releases.Clear();
        Exemptions.Clear();
        EmptyReleasesVisibility = Visibility.Visible;
        EmptyExemptionsVisibility = Visibility.Visible;

        var myWorkstationsResult = await _apiClient.GetMyWorkstationsAsync();
        if (!myWorkstationsResult.IsSuccess || myWorkstationsResult.Value is null)
        {
            Devices.Clear();
            EmptyDevicesVisibility = Visibility.Visible;
            ApplySelectedDeviceCompliance(null);
            SetFeedback(
                InfoBarSeverity.Warning,
                "Workstations Unavailable",
                myWorkstationsResult.ErrorMessage ?? "Unable to load your assigned workstations.");
            return;
        }

        var complianceTasks = myWorkstationsResult.Value
            .Select(w => _apiClient.GetWorkstationComplianceAsync(w.DeviceId))
            .ToArray();

        await Task.WhenAll(complianceTasks);

        var selectedDeviceId = SelectedDevice?.DeviceId;
        Devices.Clear();
        DeviceComplianceItemViewModel? restoredDevice = null;
        var errors = new List<string>();

        foreach (var task in complianceTasks)
        {
            if (task.Result.IsSuccess && task.Result.Value is not null)
            {
                var vm = new DeviceComplianceItemViewModel(task.Result.Value);
                Devices.Add(vm);
                if (selectedDeviceId == vm.DeviceId)
                {
                    restoredDevice = vm;
                }
            }
            else if (!task.Result.IsSuccess && task.Result.ErrorMessage is { } err)
            {
                errors.Add(err);
            }
        }

        EmptyDevicesVisibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedDevice = restoredDevice ?? Devices.FirstOrDefault();
        ApplySelectedDeviceCompliance(SelectedDevice);

        if (errors.Count > 0)
        {
            SetFeedback(InfoBarSeverity.Warning, "Compliance Load Warning", string.Join(" | ", errors));
        }
        else if (Devices.Count == 0)
        {
            SetFeedback(
                InfoBarSeverity.Informational,
                "No Assigned Workstations",
                "No workstations are currently assigned to your account.");
        }
    }

    private void ApplySelectedDeviceCompliance(DeviceComplianceItemViewModel? device)
    {
        ProductComplianceRows.Clear();
        DiscoveredSoftware.Clear();

        if (device is null)
        {
            SelectedDeviceHeaderText = "Select a workstation to inspect compliance and discovered software";
            EmptyComplianceVisibility = Visibility.Visible;
            EmptyDiscoveredVisibility = Visibility.Visible;
            return;
        }

        SelectedDeviceHeaderText = $"{device.Hostname} ({device.SiteId}) — {device.LastInventoryLocalText}";

        foreach (var item in device.Dto.Products.OrderBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase))
        {
            ProductComplianceRows.Add(new ProductComplianceItemViewModel(item));
        }

        foreach (var item in device.Dto.UnmanagedDiscovered.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            DiscoveredSoftware.Add(new DiscoveredSoftwareItemViewModel(item));
        }

        EmptyComplianceVisibility = ProductComplianceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDiscoveredVisibility = DiscoveredSoftware.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyUnauthenticatedState()
    {
        CanViewFleetCompliance = false;
        CanManageCatalogue = false;
        CanApproveReleases = false;
        CanManageExemptions = false;
        FleetSectionVisibility = Visibility.Collapsed;
        ManageCatalogueVisibility = Visibility.Collapsed;
        ApproveReleasesVisibility = Visibility.Collapsed;
        ManageExemptionsVisibility = Visibility.Collapsed;
        RoleScopeSummaryText = "Not signed in";

        Devices.Clear();
        ProductComplianceRows.Clear();
        DiscoveredSoftware.Clear();
        Products.Clear();
        Releases.Clear();
        Exemptions.Clear();

        EmptyDevicesVisibility = Visibility.Visible;
        EmptyComplianceVisibility = Visibility.Visible;
        EmptyDiscoveredVisibility = Visibility.Visible;
        EmptyReleasesVisibility = Visibility.Visible;
        EmptyExemptionsVisibility = Visibility.Visible;
        SelectedDeviceHeaderText = "Sign in required";
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
