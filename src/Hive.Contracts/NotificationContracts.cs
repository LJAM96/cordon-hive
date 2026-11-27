namespace Hive.Contracts;

public sealed record CreateNotificationRequest(
    string Type,
    string Priority,
    string Title,
    string SafeSummary,
    string TargetType,
    IReadOnlyList<string> TargetKeys,
    DateTimeOffset? DueAtUtc = null,
    string? SourceId = null);

public sealed record UserNotificationDto(
    Guid NotificationId,
    string SourceId,
    string Type,
    string Priority,
    string Title,
    string SafeSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DueAtUtc,
    long Revision,
    string RecipientKey,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    long RecipientVersion,
    bool IsActionableUnresolved);

public sealed record NotificationSyncResponse(
    string Cursor,
    bool SnapshotReset,
    IReadOnlyList<UserNotificationDto> Items,
    int UnresolvedActionableCount,
    DateTimeOffset ServerTimeUtc);

public sealed record NotificationActionRequest(
    long ExpectedVersion,
    string? Notes = null);

public sealed record RecordDeliveryRequest(
    Guid? DeviceId,
    int? SessionId,
    string Outcome);

public sealed record AdminNotificationSummaryDto(
    Guid NotificationId,
    string SourceId,
    string Type,
    string Priority,
    string Title,
    string SafeSummary,
    string TargetType,
    string CreatedBySid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DueAtUtc,
    long Revision,
    int TotalRecipients,
    int DeliveredCount,
    int ReadCount,
    int AcknowledgedCount,
    int CompletedCount,
    int UnresolvedCount,
    bool IsCancelled);

public sealed record CancelNotificationRequest(
    string Reason);
