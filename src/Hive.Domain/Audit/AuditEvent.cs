namespace Hive.Domain.Audit;

public sealed record AuditEvent(
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
    string? SummaryJson)
{
    public static AuditEvent Create(
        string actorType,
        string actorId,
        string action,
        string objectType,
        string objectId,
        string result,
        string? siteId,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        string? summaryJson = null) =>
        new(
            Guid.NewGuid(),
            actorType,
            actorId,
            action,
            objectType,
            objectId,
            result,
            siteId,
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            occurredAtUtc,
            summaryJson);
}
