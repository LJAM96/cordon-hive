namespace Hive.Notifications;

public enum NotificationType
{
    Informational,
    Announcement,
    RequiredAction,
    SoftwareUpdate,
    Maintenance,
    CriticalCondition
}

public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Critical
}

public enum NotificationTargetType
{
    User,
    Device,
    Site,
    AllUsers
}

public enum NotificationActionKind
{
    Read,
    Acknowledge,
    Complete
}

public enum NotificationActionStatus
{
    Success,
    NotFound,
    Conflict,
    AlreadyCancelled,
    IdempotentReplay
}

public sealed record NotificationRecord(
    Guid NotificationId,
    string SourceId,
    NotificationType Type,
    NotificationPriority Priority,
    string Title,
    string SafeSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DueAtUtc,
    long Revision);

public sealed record NotificationEntity(
    Guid NotificationId,
    string SourceId,
    NotificationType Type,
    NotificationPriority Priority,
    string Title,
    string SafeSummary,
    NotificationTargetType TargetType,
    string CreatedBySid,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DueAtUtc,
    long Revision,
    DateTimeOffset? CancelledAtUtc = null);

public sealed record NotificationDeliveryRecord(
    Guid DeliveryId,
    Guid NotificationId,
    string RecipientKey,
    Guid? DeviceId,
    int? SessionId,
    string Outcome,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Separate durable timestamps per recipient; acknowledging an update or required action
/// never satisfies completion semantics.
/// </summary>
public sealed record NotificationRecipientState(
    Guid NotificationId,
    string RecipientKey,
    NotificationType Type,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    long Version)
{
    public bool IsActionableUnresolved()
    {
        if (CancelledAtUtc is not null)
        {
            return false;
        }

        return Type switch
        {
            NotificationType.Informational => ReadAtUtc is null,
            NotificationType.Announcement => AcknowledgedAtUtc is null,
            NotificationType.RequiredAction => CompletedAtUtc is null,
            NotificationType.SoftwareUpdate => CompletedAtUtc is null,
            NotificationType.Maintenance => CompletedAtUtc is null,
            NotificationType.CriticalCondition => CompletedAtUtc is null,
            _ => true
        };
    }

    public NotificationRecipientState MarkRead(DateTimeOffset atUtc) =>
        this with { ReadAtUtc = ReadAtUtc ?? atUtc, Version = Version + 1 };

    public NotificationRecipientState MarkAcknowledged(DateTimeOffset atUtc) =>
        this with { AcknowledgedAtUtc = AcknowledgedAtUtc ?? atUtc, Version = Version + 1 };

    public NotificationRecipientState MarkCompleted(DateTimeOffset atUtc) =>
        this with { CompletedAtUtc = CompletedAtUtc ?? atUtc, Version = Version + 1 };
}

public sealed record PublishNotificationRequest(
    string SourceId,
    NotificationType Type,
    NotificationPriority Priority,
    string Title,
    string SafeSummary,
    string CorrelationId,
    string TargetType,
    string TargetKey,
    DateTimeOffset? DueAtUtc);

/// <summary>
/// Publisher contract seam for P4 and future Cordon suite publishers (P9).
/// </summary>
public interface INotificationPublisherContract
{
    Task<Guid> PublishAsync(PublishNotificationRequest request, CancellationToken cancellationToken = default);
}
