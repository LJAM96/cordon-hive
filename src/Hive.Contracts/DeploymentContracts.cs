namespace Hive.Contracts;

public sealed record PackageUploadResponse(
    Guid Id,
    string FileName,
    string Sha256,
    long SizeBytes,
    string ContentType,
    DateTimeOffset UploadedUtc);

public sealed record SignedReleaseManifestDto(
    Guid ReleaseId,
    Guid ProductId,
    string ProductName,
    string Version,
    string InstallerType,
    string PackageSha256,
    long PackageSizeBytes,
    string DownloadRelativeUrl,
    string SilentArgs,
    string VerificationType,
    string VerificationTarget,
    string ExpectedVersion,
    IReadOnlyList<string> ProtectedProcesses,
    bool RebootAllowed,
    string InstallProfile,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    string KeyId);

public sealed record SignedManifestEnvelopeDto(
    SignedReleaseManifestDto Manifest,
    string ManifestCanonicalJson,
    string SignatureBase64,
    string Algorithm,
    string PublicKeyPem);

public sealed record SignReleaseRequest(
    IReadOnlyList<string>? ProtectedProcesses = null,
    bool RebootAllowed = false,
    int ValidDays = 90);

public sealed record CreateDeploymentJobRequest(
    string SiteId,
    Guid ReleaseId,
    IReadOnlyList<Guid> TargetDeviceIds,
    string Policy,
    bool Confirmed);

public sealed record DeploymentTargetDto(
    Guid TargetId,
    Guid JobId,
    Guid DeviceId,
    string Hostname,
    string State,
    int AttemptCount,
    string? LastReasonCode,
    string? LastMessage,
    DateTimeOffset? DeferredUntilUtc,
    DateTimeOffset UpdatedUtc);

public sealed record DeploymentJobDto(
    Guid JobId,
    string SiteId,
    Guid ReleaseId,
    string ProductName,
    string Version,
    string Policy,
    string CreatedBySid,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<DeploymentTargetDto> Targets);

public sealed record AgentPendingDeploymentDto(
    Guid TargetId,
    Guid JobId,
    Guid ReleaseId,
    string Policy,
    string State,
    SignedManifestEnvelopeDto Envelope);

public sealed record AgentSyncResponse(
    DateTimeOffset ServerUtc,
    IReadOnlyList<AgentPendingDeploymentDto> PendingDeployments);

public sealed record DeploymentLeaseResponse(
    bool Granted,
    Guid TargetId,
    Guid? LeaseId,
    DateTimeOffset? LeaseExpiresUtc,
    string ReasonCode,
    string? Message);

public sealed record DeploymentEventRequest(
    string State,
    string ReasonCode,
    string? Message = null,
    Guid? LeaseId = null,
    int? ExitCode = null,
    DateTimeOffset? DeferredUntilUtc = null);

public sealed record ProtectionLockStatusDto(
    bool ProtectedModeActive,
    string ReasonCode,
    string? LockedBySid,
    DateTimeOffset? UpdatedUtc);
