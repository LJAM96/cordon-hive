namespace Hive.Contracts;

public sealed record InstalledSoftwareItemDto(
    string DisplayName,
    string? Publisher,
    string? ObservedVersionRaw,
    string DetectionSource,
    string InstallScope,
    string ConfidenceState,
    DateTimeOffset ObservedAtUtc);

public sealed record InventorySnapshotRequest(
    long DetectionRevision,
    bool IsComplete,
    IReadOnlyList<InstalledSoftwareItemDto> Items,
    DateTimeOffset CapturedAtUtc);

public sealed record InventorySnapshotResponse(
    int StoredCount,
    DateTimeOffset ServerTimeUtc);

public sealed record SoftwareProductDto(
    Guid ProductId,
    string Name,
    string Publisher,
    string VersionScheme,
    string MatchPattern,
    long DetectionRevision,
    IReadOnlyList<string> ProtectedProcessNames,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateSoftwareProductRequest(
    string Name,
    string Publisher,
    string VersionScheme,
    string MatchPattern,
    IReadOnlyList<string>? ProtectedProcessNames = null);

public sealed record SoftwareReleaseDto(
    Guid ReleaseId,
    Guid ProductId,
    string VersionRaw,
    string VersionScheme,
    string ApprovalState,
    string ValidationReference,
    string? PackageSha256,
    string? ManifestDigest,
    string CreatedBySid,
    string? ApprovedBySid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? WithdrawnAtUtc,
    string? WithdrawnReason);

public sealed record CreateSoftwareReleaseRequest(
    Guid ProductId,
    string VersionRaw,
    string ValidationReference,
    string? PackageSha256 = null,
    string? ManifestDigest = null);

public sealed record ApproveSoftwareReleaseRequest(
    string? Notes = null);

public sealed record WithdrawSoftwareReleaseRequest(
    string Reason);

public sealed record SoftwareExemptionDto(
    Guid ExemptionId,
    Guid? DeviceId,
    Guid ProductId,
    string? AllowedVersionRaw,
    string Reason,
    string ApprovedBySid,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedBySid,
    string? RevokedReason,
    bool IsActive);

public sealed record CreateSoftwareExemptionRequest(
    Guid? DeviceId,
    Guid ProductId,
    string? AllowedVersionRaw,
    string Reason,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record RevokeSoftwareExemptionRequest(
    string Reason);

public sealed record DeviceProductComplianceDto(
    Guid DeviceId,
    string Hostname,
    string SiteId,
    Guid ProductId,
    string ProductName,
    string? ApprovedVersionRaw,
    string? ObservedVersionRaw,
    string ComplianceStatus,
    string Reason,
    Guid? ActiveExemptionId,
    DateTimeOffset? ExemptionExpiresAtUtc,
    DateTimeOffset? LastInventoryAtUtc);

public sealed record DeviceComplianceSummaryDto(
    Guid DeviceId,
    string Hostname,
    string SiteId,
    int CompliantCount,
    int UpdateRequiredCount,
    int AheadUnapprovedCount,
    int NotInstalledCount,
    int ExemptCount,
    int UnknownCount,
    DateTimeOffset? LastInventoryAtUtc,
    IReadOnlyList<DeviceProductComplianceDto> Products,
    IReadOnlyList<InstalledSoftwareItemDto> UnmanagedDiscovered);
