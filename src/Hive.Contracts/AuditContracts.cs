namespace Hive.Contracts;

public sealed record AuditEventDto(
    Guid EventId,
    string ActorType,
    string ActorId,
    string Action,
    string ObjectType,
    string ObjectId,
    string Result,
    string? SiteId,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string? SummaryJson);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
