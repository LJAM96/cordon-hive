namespace Hive.Contracts;

public sealed record OperationalStatusDto(
    string Status,
    DateTimeOffset ServerUtc,
    string ProtocolVersion,
    string StorageProvider,
    bool DeploymentsPaused,
    string? PauseReason,
    DateTimeOffset? PausedAtUtc,
    string? PausedBySid,
    int ActiveDeploymentLeaseCount,
    long PendingOutboxCount,
    DateTimeOffset? LastTelemetryPruneUtc);

public sealed record EmergencyStopRequest(
    bool PauseDeployments,
    bool InvalidateActiveLeases,
    string Reason);

public sealed record EmergencyStopResponse(
    bool DeploymentsPaused,
    int InvalidatedLeaseCount,
    string Reason,
    DateTimeOffset UpdatedUtc);

public sealed record TelemetryRetentionPruneResultDto(
    int DeletedMetricSamples,
    int DeletedProcessSnapshots,
    DateTimeOffset PrunedAtUtc);
