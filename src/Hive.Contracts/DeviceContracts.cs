namespace Hive.Contracts;

public sealed record ObservedSessionDto(
    int SessionId,
    string? UserSid,
    string SessionState,
    DateTimeOffset ObservedAtUtc);

public sealed record HeartbeatRequest(
    Guid BootId,
    string AgentVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ObservedSessionDto> Sessions,
    string? Hostname);

public sealed record HeartbeatResponse(
    long PolicyRevision,
    DateTimeOffset ServerTimeUtc,
    int NextHeartbeatSeconds);

public sealed record DeviceSummaryDto(
    Guid DeviceId,
    string SiteId,
    string Hostname,
    string? AssetNumber,
    string? AssignedUserSid,
    string EnrollmentState,
    string ConnectivityState,
    DateTimeOffset? LastSeenUtc,
    long PolicyRevision,
    long RowVersion,
    string? AgentVersion);

public sealed record DeviceAssignmentHistoryDto(
    Guid AssignmentId,
    Guid DeviceId,
    string? UserSid,
    string AssignedBySid,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc);

public sealed record DeviceDetailDto(
    Guid DeviceId,
    string SiteId,
    string Hostname,
    string? AssetNumber,
    string? AssignedUserSid,
    string EnrollmentState,
    string ConnectivityState,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset EnrolledAtUtc,
    Guid? BootId,
    string? AgentVersion,
    long PolicyRevision,
    long RowVersion,
    IReadOnlyList<ObservedSessionDto> ActiveSessions,
    IReadOnlyList<DeviceAssignmentHistoryDto> AssignmentHistory);

public sealed record UpdateAssignmentRequest(
    string? AssetNumber,
    string? AssignedUserSid,
    long ExpectedRowVersion);

public sealed record RevokeDeviceRequest(
    string Reason);
