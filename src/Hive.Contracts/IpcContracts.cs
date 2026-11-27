namespace Hive.Contracts;

public static class IpcMessageTypes
{
    public const string Hello = "hello";
    public const string GetStatus = "get_status";
    public const string GetLocalTelemetry = "get_local_telemetry";
    public const string SetProtectionLock = "set_protection_lock";
    public const string BrokerInstall = "broker_install";
    public const string Error = "error";
}

public sealed record IpcEnvelope(
    int Version,
    string MessageType,
    string CorrelationId,
    string PayloadJson);

public sealed record IpcHelloRequest(
    int ClientVersion,
    string ClientName);

public sealed record IpcHelloResponse(
    int ServerVersion,
    string AgentVersion,
    string AuthenticatedCallerSid);

public sealed record IpcAgentStatusResponse(
    Guid? DeviceId,
    string EnrollmentState,
    string ConnectivityState,
    DateTimeOffset? LastHeartbeatUtc,
    long PolicyRevision,
    bool ProtectedModeActive,
    int PendingQueueCount);

public sealed record IpcProtectionLockRequest(
    bool Enable,
    string ReasonCode);

/// <summary>
/// Strictly typed install permit for the restricted broker.
/// Never accepts executable paths, shell strings, or arbitrary arguments.
/// </summary>
public sealed record BrokerInstallRequest(
    Guid TargetId,
    Guid ReleaseId,
    string ManifestDigest,
    Guid LeaseId);

public sealed record BrokerInstallResult(
    bool Accepted,
    string ReasonCode,
    string? Message);

public sealed record IpcLocalTelemetryResponse(
    MetricSampleDto? LatestSample,
    IReadOnlyList<VolumeSnapshotDto> Volumes,
    ProcessSnapshotDto? LatestProcessSnapshot,
    string ConnectivityState,
    DateTimeOffset? LastHeartbeatUtc);
