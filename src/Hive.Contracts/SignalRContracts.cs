namespace Hive.Contracts;

public sealed record StateChangedEnvelope(
    int SchemaVersion,
    Guid EventId,
    string Type,
    Guid ResourceId,
    long Revision,
    DateTimeOffset OccurredAtUtc);
