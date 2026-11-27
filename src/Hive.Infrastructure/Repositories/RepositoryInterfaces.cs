using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Devices;
using Hive.Domain.Enrollment;

namespace Hive.Infrastructure.Repositories;

public sealed record OutboxEventRecord(
    Guid EventId,
    string EventType,
    Guid ResourceId,
    long Revision,
    string? SiteId,
    string? TargetUserSid,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? DispatchedAtUtc = null)
{
    public StateChangedEnvelope ToEnvelope() =>
        new(
            ProtocolConstants.ProtocolVersion,
            EventId,
            EventType,
            ResourceId,
            Revision,
            OccurredAtUtc);
}

public enum EnrollmentStatus
{
    Success,
    InvalidOrUnknownToken,
    TokenExpired,
    TokenAlreadyRedeemed,
    DuplicateAssetNumber,
    DuplicateCertificateThumbprint
}

public sealed record EnrollDeviceResult(
    EnrollmentStatus Status,
    Device? Device,
    string? ErrorMessage = null);

public enum AssignmentUpdateStatus
{
    Success,
    NotFound,
    ConcurrencyConflict,
    DuplicateAssetNumber
}

public sealed record UpdateAssignmentResult(
    AssignmentUpdateStatus Status,
    Device? Device,
    DeviceAssignment? NewAssignment = null);

public enum RevokeDeviceStatus
{
    Success,
    NotFound,
    AlreadyRevoked
}

public sealed record RevokeDeviceResult(
    RevokeDeviceStatus Status,
    Device? Device);

public interface IDeviceRepository
{
    Task<Device?> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<Device?> GetByThumbprintAsync(string certificateThumbprint, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Device>> ListAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        string? assignedUserSid = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceSession>> GetSessionsAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceAssignment>> GetAssignmentHistoryAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<Device?> RecordHeartbeatAsync(
        Guid deviceId,
        Guid bootId,
        string agentVersion,
        string? hostname,
        IReadOnlyList<DeviceSession> sessions,
        DateTimeOffset seenAtUtc,
        CancellationToken cancellationToken = default);

    Task<UpdateAssignmentResult> UpdateAssignmentAsync(
        Guid deviceId,
        string? assetNumber,
        string? assignedUserSid,
        string assignedBySid,
        long expectedRowVersion,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default);

    Task<RevokeDeviceResult> RevokeAsync(
        Guid deviceId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IEnrollmentTokenRepository
{
    Task CreateAsync(EnrollmentToken token, CancellationToken cancellationToken = default);

    Task<EnrollmentToken?> GetByHashAsync(string tokenHashSha256, CancellationToken cancellationToken = default);

    Task<EnrollDeviceResult> RedeemAndEnrollAsync(
        string tokenHashSha256,
        string hostname,
        string certificateThumbprint,
        string? assetNumber,
        string agentVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public interface IAuditRepository
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<AuditEvent> Items, int TotalCount)> QueryAsync(
        int page,
        int pageSize,
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        CancellationToken cancellationToken = default);
}

public interface IOutboxRepository
{
    Task EnqueueAsync(OutboxEventRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxEventRecord>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task MarkDispatchedAsync(Guid eventId, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default);
}
