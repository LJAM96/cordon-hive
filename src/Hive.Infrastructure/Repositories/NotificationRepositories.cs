using Hive.Contracts;
using Hive.Notifications;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public sealed record RecipientSyncItem(
    NotificationEntity Notification,
    NotificationRecipientState Recipient,
    long ChangeSeq);

public sealed record NotificationSyncResult(
    IReadOnlyList<RecipientSyncItem> Items,
    long LatestChangeSeq,
    bool SnapshotReset);

public sealed record NotificationActionResult(
    NotificationActionStatus Status,
    NotificationEntity? Notification,
    NotificationRecipientState? Recipient);

public interface INotificationRepository
{
    Task CreateWithRecipientsAsync(
        NotificationEntity notification,
        IReadOnlyList<string> recipientKeys,
        CancellationToken ct = default);

    Task<NotificationSyncResult> SyncForRecipientKeysAsync(
        IReadOnlyCollection<string> recipientKeys,
        long? afterChangeSeq,
        CancellationToken ct = default);

    Task<IReadOnlyList<AdminNotificationSummaryDto>> ListAdminSummariesAsync(
        CancellationToken ct = default);

    Task<NotificationEntity?> GetNotificationAsync(
        Guid notificationId,
        CancellationToken ct = default);

    Task<NotificationActionResult> ApplyRecipientActionAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationActionKind actionKind,
        long expectedVersion,
        string? idempotencyKey,
        string actorSid,
        DateTimeOffset atUtc,
        CancellationToken ct = default);

    Task<bool> RecordDeliveryAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationDeliveryRecord delivery,
        CancellationToken ct = default);

    Task<NotificationEntity?> CancelNotificationAsync(
        Guid notificationId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken ct = default);
}

internal static class NotificationRepositoryHelpers
{
    public static string FormatIdempotencyAction(Guid notificationId, NotificationActionKind actionKind) =>
        $"{notificationId:D}:{actionKind}";

    public static NotificationRecipientState ApplyAction(
        NotificationRecipientState recipient,
        NotificationActionKind actionKind,
        DateTimeOffset atUtc) =>
        actionKind switch
        {
            NotificationActionKind.Read => recipient.MarkRead(atUtc),
            NotificationActionKind.Acknowledge => recipient.MarkAcknowledged(atUtc),
            NotificationActionKind.Complete => recipient.MarkCompleted(atUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(actionKind), actionKind, "Unsupported notification action.")
        };

    public static AdminNotificationSummaryDto BuildAdminSummary(
        NotificationEntity notification,
        IReadOnlyCollection<NotificationRecipientState> recipients)
    {
        var delivered = 0;
        var read = 0;
        var acknowledged = 0;
        var completed = 0;
        var unresolved = 0;

        foreach (var recipient in recipients)
        {
            if (recipient.DeliveredAtUtc is not null) delivered++;
            if (recipient.ReadAtUtc is not null) read++;
            if (recipient.AcknowledgedAtUtc is not null) acknowledged++;
            if (recipient.CompletedAtUtc is not null) completed++;
            if (recipient.IsActionableUnresolved()) unresolved++;
        }

        return new AdminNotificationSummaryDto(
            notification.NotificationId,
            notification.SourceId,
            notification.Type.ToString(),
            notification.Priority.ToString(),
            notification.Title,
            notification.SafeSummary,
            notification.TargetType.ToString(),
            notification.CreatedBySid,
            notification.CreatedAtUtc,
            notification.DueAtUtc,
            notification.Revision,
            recipients.Count,
            delivered,
            read,
            acknowledged,
            completed,
            unresolved,
            notification.CancelledAtUtc is not null);
    }

    public static NotificationRecipientState? SelectFirstMatchingRecipient(
        IReadOnlyCollection<string> callerRecipientKeys,
        Func<string, NotificationRecipientState?> lookup)
    {
        foreach (var key in callerRecipientKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var match = lookup(key);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}

public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private sealed record RecipientEntry(NotificationRecipientState State, long ChangeSeq);

    private sealed record IdempotencyEntry(string ActorSid, string Action, DateTimeOffset CreatedAtUtc);

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, NotificationEntity> _notifications = new();
    private readonly Dictionary<Guid, Dictionary<string, RecipientEntry>> _recipientsByNotification = new();
    private readonly List<NotificationDeliveryRecord> _deliveries = new();
    private readonly Dictionary<string, IdempotencyEntry> _idempotency = new(StringComparer.Ordinal);
    private long _changeSeq;

    public Task CreateWithRecipientsAsync(
        NotificationEntity notification,
        IReadOnlyList<string> recipientKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(recipientKeys);

        lock (_syncRoot)
        {
            _notifications[notification.NotificationId] = notification;

            var recipients = new Dictionary<string, RecipientEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawKey in recipientKeys)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    continue;
                }

                var key = rawKey.Trim();
                if (recipients.ContainsKey(key))
                {
                    continue;
                }

                var seq = ++_changeSeq;
                var state = new NotificationRecipientState(
                    notification.NotificationId,
                    key,
                    notification.Type,
                    DeliveredAtUtc: null,
                    ReadAtUtc: null,
                    AcknowledgedAtUtc: null,
                    CompletedAtUtc: null,
                    CancelledAtUtc: notification.CancelledAtUtc,
                    Version: 1);

                recipients[key] = new RecipientEntry(state, seq);
            }

            _recipientsByNotification[notification.NotificationId] = recipients;
        }

        return Task.CompletedTask;
    }

    public Task<NotificationSyncResult> SyncForRecipientKeysAsync(
        IReadOnlyCollection<string> recipientKeys,
        long? afterChangeSeq,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientKeys);

        var keySet = recipientKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_syncRoot)
        {
            var latest = _changeSeq;
            var snapshotReset = !afterChangeSeq.HasValue || afterChangeSeq.Value < 0 || afterChangeSeq.Value > latest;

            if (keySet.Count == 0)
            {
                return Task.FromResult(new NotificationSyncResult(
                    Array.Empty<RecipientSyncItem>(),
                    latest,
                    snapshotReset));
            }

            var items = new List<RecipientSyncItem>();
            foreach (var (notificationId, recipients) in _recipientsByNotification)
            {
                if (!_notifications.TryGetValue(notificationId, out var notification))
                {
                    continue;
                }

                foreach (var entry in recipients.Values)
                {
                    if (!keySet.Contains(entry.State.RecipientKey))
                    {
                        continue;
                    }

                    if (!snapshotReset && entry.ChangeSeq <= afterChangeSeq!.Value)
                    {
                        continue;
                    }

                    items.Add(new RecipientSyncItem(notification, entry.State, entry.ChangeSeq));
                }
            }

            items.Sort((a, b) =>
            {
                var seqCmp = a.ChangeSeq.CompareTo(b.ChangeSeq);
                if (seqCmp != 0) return seqCmp;
                var idCmp = a.Notification.NotificationId.CompareTo(b.Notification.NotificationId);
                return idCmp != 0
                    ? idCmp
                    : string.Compare(a.Recipient.RecipientKey, b.Recipient.RecipientKey, StringComparison.OrdinalIgnoreCase);
            });

            return Task.FromResult(new NotificationSyncResult(items, latest, snapshotReset));
        }
    }

    public Task<IReadOnlyList<AdminNotificationSummaryDto>> ListAdminSummariesAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            var summaries = _notifications.Values
                .OrderByDescending(n => n.CreatedAtUtc)
                .ThenBy(n => n.NotificationId)
                .Select(n =>
                {
                    var recipients = _recipientsByNotification.TryGetValue(n.NotificationId, out var map)
                        ? map.Values.Select(e => e.State).ToArray()
                        : Array.Empty<NotificationRecipientState>();
                    return NotificationRepositoryHelpers.BuildAdminSummary(n, recipients);
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<AdminNotificationSummaryDto>>(summaries);
        }
    }

    public Task<NotificationEntity?> GetNotificationAsync(Guid notificationId, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _notifications.TryGetValue(notificationId, out var notification);
            return Task.FromResult(notification);
        }
    }

    public Task<NotificationActionResult> ApplyRecipientActionAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationActionKind actionKind,
        long expectedVersion,
        string? idempotencyKey,
        string actorSid,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerRecipientKeys);

        lock (_syncRoot)
        {
            if (!_notifications.TryGetValue(notificationId, out var notification) ||
                !_recipientsByNotification.TryGetValue(notificationId, out var recipients))
            {
                return Task.FromResult(new NotificationActionResult(NotificationActionStatus.NotFound, null, null));
            }

            var recipient = NotificationRepositoryHelpers.SelectFirstMatchingRecipient(
                callerRecipientKeys,
                key => recipients.TryGetValue(key.Trim(), out var entry) ? entry.State : null);

            if (recipient is null)
            {
                return Task.FromResult(new NotificationActionResult(NotificationActionStatus.NotFound, null, null));
            }

            var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
            var expectedAction = NotificationRepositoryHelpers.FormatIdempotencyAction(notificationId, actionKind);

            if (normalizedKey is not null && _idempotency.TryGetValue(normalizedKey, out var existingIdemp))
            {
                if (string.Equals(existingIdemp.ActorSid, actorSid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingIdemp.Action, expectedAction, StringComparison.Ordinal))
                {
                    return Task.FromResult(new NotificationActionResult(
                        NotificationActionStatus.IdempotentReplay,
                        notification,
                        recipient));
                }

                return Task.FromResult(new NotificationActionResult(
                    NotificationActionStatus.Conflict,
                    notification,
                    recipient));
            }

            if (notification.CancelledAtUtc is not null || recipient.CancelledAtUtc is not null)
            {
                return Task.FromResult(new NotificationActionResult(
                    NotificationActionStatus.AlreadyCancelled,
                    notification,
                    recipient));
            }

            if (recipient.Version != expectedVersion)
            {
                return Task.FromResult(new NotificationActionResult(
                    NotificationActionStatus.Conflict,
                    notification,
                    recipient));
            }

            var updated = NotificationRepositoryHelpers.ApplyAction(recipient, actionKind, atUtc);
            var seq = ++_changeSeq;
            recipients[recipient.RecipientKey] = new RecipientEntry(updated, seq);

            if (normalizedKey is not null)
            {
                _idempotency[normalizedKey] = new IdempotencyEntry(actorSid, expectedAction, atUtc);
            }

            return Task.FromResult(new NotificationActionResult(
                NotificationActionStatus.Success,
                notification,
                updated));
        }
    }

    public Task<bool> RecordDeliveryAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationDeliveryRecord delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerRecipientKeys);
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_syncRoot)
        {
            if (!_notifications.ContainsKey(notificationId) ||
                !_recipientsByNotification.TryGetValue(notificationId, out var recipients))
            {
                return Task.FromResult(false);
            }

            var recipient = NotificationRepositoryHelpers.SelectFirstMatchingRecipient(
                callerRecipientKeys,
                key => recipients.TryGetValue(key.Trim(), out var entry) ? entry.State : null);

            if (recipient is null)
            {
                return Task.FromResult(false);
            }

            _deliveries.Add(delivery with
            {
                NotificationId = notificationId,
                RecipientKey = recipient.RecipientKey
            });

            if (recipient.DeliveredAtUtc is null)
            {
                var updated = recipient with
                {
                    DeliveredAtUtc = delivery.RecordedAtUtc,
                    Version = recipient.Version + 1
                };
                recipients[recipient.RecipientKey] = new RecipientEntry(updated, ++_changeSeq);
            }

            return Task.FromResult(true);
        }
    }

    public Task<NotificationEntity?> CancelNotificationAsync(
        Guid notificationId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            if (!_notifications.TryGetValue(notificationId, out var notification))
            {
                return Task.FromResult<NotificationEntity?>(null);
            }

            if (notification.CancelledAtUtc is not null)
            {
                return Task.FromResult<NotificationEntity?>(notification);
            }

            var cancelled = notification with
            {
                CancelledAtUtc = cancelledAtUtc,
                Revision = notification.Revision + 1
            };
            _notifications[notificationId] = cancelled;

            if (_recipientsByNotification.TryGetValue(notificationId, out var recipients))
            {
                foreach (var key in recipients.Keys.ToArray())
                {
                    var current = recipients[key].State;
                    var updated = current with
                    {
                        CancelledAtUtc = current.CancelledAtUtc ?? cancelledAtUtc,
                        Version = current.Version + 1
                    };
                    recipients[key] = new RecipientEntry(updated, ++_changeSeq);
                }
            }

            return Task.FromResult<NotificationEntity?>(cancelled);
        }
    }
}

public sealed class PostgresNotificationRepository : INotificationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresNotificationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task CreateWithRecipientsAsync(
        NotificationEntity notification,
        IReadOnlyList<string> recipientKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(recipientKeys);

        var distinctKeys = recipientKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var insertNotif = conn.CreateCommand())
        {
            insertNotif.Transaction = tx;
            insertNotif.CommandText = """
                INSERT INTO notifications (
                    notification_id, source_id, notification_type, priority, title, safe_summary,
                    target_type, created_by_sid, created_at_utc, due_at_utc, revision, cancelled_at_utc)
                VALUES (
                    @notification_id, @source_id, @notification_type, @priority, @title, @safe_summary,
                    @target_type, @created_by_sid, @created_at_utc, @due_at_utc, @revision, @cancelled_at_utc);
                """;
            insertNotif.Parameters.AddWithValue("notification_id", notification.NotificationId);
            insertNotif.Parameters.AddWithValue("source_id", notification.SourceId);
            insertNotif.Parameters.AddWithValue("notification_type", notification.Type.ToString());
            insertNotif.Parameters.AddWithValue("priority", notification.Priority.ToString());
            insertNotif.Parameters.AddWithValue("title", notification.Title);
            insertNotif.Parameters.AddWithValue("safe_summary", notification.SafeSummary);
            insertNotif.Parameters.AddWithValue("target_type", notification.TargetType.ToString());
            insertNotif.Parameters.AddWithValue("created_by_sid", notification.CreatedBySid);
            insertNotif.Parameters.AddWithValue("created_at_utc", notification.CreatedAtUtc);
            PostgresMapping.AddNullableParameter(insertNotif, "due_at_utc", notification.DueAtUtc);
            insertNotif.Parameters.AddWithValue("revision", notification.Revision);
            PostgresMapping.AddNullableParameter(insertNotif, "cancelled_at_utc", notification.CancelledAtUtc);
            await insertNotif.ExecuteNonQueryAsync(ct);
        }

        foreach (var key in distinctKeys)
        {
            await using var insertRecip = conn.CreateCommand();
            insertRecip.Transaction = tx;
            insertRecip.CommandText = """
                INSERT INTO notification_recipients (
                    notification_id, recipient_key, change_seq,
                    delivered_at_utc, read_at_utc, acknowledged_at_utc, completed_at_utc, cancelled_at_utc, row_version)
                VALUES (
                    @notification_id, @recipient_key, nextval('notification_change_seq'),
                    NULL, NULL, NULL, NULL, @cancelled_at_utc, 1);
                """;
            insertRecip.Parameters.AddWithValue("notification_id", notification.NotificationId);
            insertRecip.Parameters.AddWithValue("recipient_key", key);
            PostgresMapping.AddNullableParameter(insertRecip, "cancelled_at_utc", notification.CancelledAtUtc);
            await insertRecip.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<NotificationSyncResult> SyncForRecipientKeysAsync(
        IReadOnlyCollection<string> recipientKeys,
        long? afterChangeSeq,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientKeys);

        var distinctKeys = recipientKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long latestChangeSeq;
        await using (var seqCmd = conn.CreateCommand())
        {
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(change_seq), 0) FROM notification_recipients;";
            var scalar = await seqCmd.ExecuteScalarAsync(ct);
            latestChangeSeq = scalar is long l ? l : Convert.ToInt64(scalar);
        }

        var snapshotReset = !afterChangeSeq.HasValue || afterChangeSeq.Value < 0 || afterChangeSeq.Value > latestChangeSeq;

        if (distinctKeys.Length == 0)
        {
            await tx.CommitAsync(ct);
            return new NotificationSyncResult(Array.Empty<RecipientSyncItem>(), latestChangeSeq, snapshotReset);
        }

        var items = new List<RecipientSyncItem>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = snapshotReset
                ? """
                  SELECT n.notification_id, n.source_id, n.notification_type, n.priority, n.title, n.safe_summary,
                         n.target_type, n.created_by_sid, n.created_at_utc, n.due_at_utc, n.revision, n.cancelled_at_utc,
                         nr.recipient_key, nr.change_seq, nr.delivered_at_utc, nr.read_at_utc, nr.acknowledged_at_utc,
                         nr.completed_at_utc, nr.cancelled_at_utc, nr.row_version
                  FROM notification_recipients nr
                  INNER JOIN notifications n ON n.notification_id = nr.notification_id
                  WHERE nr.recipient_key = ANY(@recipient_keys)
                  ORDER BY nr.change_seq ASC, nr.notification_id ASC, nr.recipient_key ASC;
                  """
                : """
                  SELECT n.notification_id, n.source_id, n.notification_type, n.priority, n.title, n.safe_summary,
                         n.target_type, n.created_by_sid, n.created_at_utc, n.due_at_utc, n.revision, n.cancelled_at_utc,
                         nr.recipient_key, nr.change_seq, nr.delivered_at_utc, nr.read_at_utc, nr.acknowledged_at_utc,
                         nr.completed_at_utc, nr.cancelled_at_utc, nr.row_version
                  FROM notification_recipients nr
                  INNER JOIN notifications n ON n.notification_id = nr.notification_id
                  WHERE nr.recipient_key = ANY(@recipient_keys)
                    AND nr.change_seq > @after_change_seq
                  ORDER BY nr.change_seq ASC, nr.notification_id ASC, nr.recipient_key ASC;
                  """;
            cmd.Parameters.AddWithValue("recipient_keys", distinctKeys);
            if (!snapshotReset)
            {
                cmd.Parameters.AddWithValue("after_change_seq", afterChangeSeq!.Value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var notification = ReadNotification(reader, offset: 0);
                var recipient = ReadRecipientState(reader, notification.Type, notification.NotificationId, offset: 12);
                var changeSeq = reader.GetInt64(13);
                items.Add(new RecipientSyncItem(notification, recipient, changeSeq));
            }
        }

        await tx.CommitAsync(ct);
        return new NotificationSyncResult(items, latestChangeSeq, snapshotReset);
    }

    public async Task<IReadOnlyList<AdminNotificationSummaryDto>> ListAdminSummariesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.notification_id, n.source_id, n.notification_type, n.priority, n.title, n.safe_summary,
                   n.target_type, n.created_by_sid, n.created_at_utc, n.due_at_utc, n.revision, n.cancelled_at_utc,
                   nr.recipient_key, nr.change_seq, nr.delivered_at_utc, nr.read_at_utc, nr.acknowledged_at_utc,
                   nr.completed_at_utc, nr.cancelled_at_utc, nr.row_version
            FROM notifications n
            LEFT JOIN notification_recipients nr ON nr.notification_id = n.notification_id
            ORDER BY n.created_at_utc DESC, n.notification_id ASC;
            """;

        var notifications = new List<NotificationEntity>();
        var recipientsByNotification = new Dictionary<Guid, List<NotificationRecipientState>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var notification = ReadNotification(reader, offset: 0);
            if (!recipientsByNotification.TryGetValue(notification.NotificationId, out var list))
            {
                list = new List<NotificationRecipientState>();
                recipientsByNotification[notification.NotificationId] = list;
                notifications.Add(notification);
            }

            if (!reader.IsDBNull(12))
            {
                list.Add(ReadRecipientState(reader, notification.Type, notification.NotificationId, offset: 12));
            }
        }

        return notifications
            .Select(n => NotificationRepositoryHelpers.BuildAdminSummary(n, recipientsByNotification[n.NotificationId]))
            .ToList();
    }

    public async Task<NotificationEntity?> GetNotificationAsync(Guid notificationId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notification_id, source_id, notification_type, priority, title, safe_summary,
                   target_type, created_by_sid, created_at_utc, due_at_utc, revision, cancelled_at_utc
            FROM notifications
            WHERE notification_id = @notification_id;
            """;
        cmd.Parameters.AddWithValue("notification_id", notificationId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNotification(reader, offset: 0) : null;
    }

    public async Task<NotificationActionResult> ApplyRecipientActionAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationActionKind actionKind,
        long expectedVersion,
        string? idempotencyKey,
        string actorSid,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerRecipientKeys);

        var distinctKeys = callerRecipientKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctKeys.Length == 0)
        {
            return new NotificationActionResult(NotificationActionStatus.NotFound, null, null);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        NotificationEntity? notification = null;
        await using (var notifCmd = conn.CreateCommand())
        {
            notifCmd.Transaction = tx;
            notifCmd.CommandText = """
                SELECT notification_id, source_id, notification_type, priority, title, safe_summary,
                       target_type, created_by_sid, created_at_utc, due_at_utc, revision, cancelled_at_utc
                FROM notifications
                WHERE notification_id = @notification_id
                FOR UPDATE;
                """;
            notifCmd.Parameters.AddWithValue("notification_id", notificationId);

            await using var reader = await notifCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                notification = ReadNotification(reader, offset: 0);
            }
        }

        if (notification is null)
        {
            return new NotificationActionResult(NotificationActionStatus.NotFound, null, null);
        }

        var matchedByKey = new Dictionary<string, NotificationRecipientState>(StringComparer.OrdinalIgnoreCase);
        await using (var recipCmd = conn.CreateCommand())
        {
            recipCmd.Transaction = tx;
            recipCmd.CommandText = """
                SELECT recipient_key, change_seq, delivered_at_utc, read_at_utc, acknowledged_at_utc,
                       completed_at_utc, cancelled_at_utc, row_version
                FROM notification_recipients
                WHERE notification_id = @notification_id
                  AND recipient_key = ANY(@recipient_keys)
                FOR UPDATE;
                """;
            recipCmd.Parameters.AddWithValue("notification_id", notificationId);
            recipCmd.Parameters.AddWithValue("recipient_keys", distinctKeys);

            await using var reader = await recipCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var state = ReadRecipientState(reader, notification.Type, notification.NotificationId, offset: 0);
                matchedByKey[state.RecipientKey] = state;
            }
        }

        var recipient = NotificationRepositoryHelpers.SelectFirstMatchingRecipient(
            callerRecipientKeys,
            key => matchedByKey.TryGetValue(key.Trim(), out var state) ? state : null);

        if (recipient is null)
        {
            return new NotificationActionResult(NotificationActionStatus.NotFound, null, null);
        }

        var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        var expectedAction = NotificationRepositoryHelpers.FormatIdempotencyAction(notificationId, actionKind);

        if (normalizedKey is not null)
        {
            await using var idempCmd = conn.CreateCommand();
            idempCmd.Transaction = tx;
            idempCmd.CommandText = """
                SELECT actor_sid, action
                FROM notification_idempotency
                WHERE idempotency_key = @idempotency_key;
                """;
            idempCmd.Parameters.AddWithValue("idempotency_key", normalizedKey);

            await using var reader = await idempCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var rowActor = reader.GetString(0);
                var rowAction = reader.GetString(1);
                if (string.Equals(rowActor, actorSid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rowAction, expectedAction, StringComparison.Ordinal))
                {
                    await tx.CommitAsync(ct);
                    return new NotificationActionResult(
                        NotificationActionStatus.IdempotentReplay,
                        notification,
                        recipient);
                }

                return new NotificationActionResult(
                    NotificationActionStatus.Conflict,
                    notification,
                    recipient);
            }
        }

        if (notification.CancelledAtUtc is not null || recipient.CancelledAtUtc is not null)
        {
            return new NotificationActionResult(
                NotificationActionStatus.AlreadyCancelled,
                notification,
                recipient);
        }

        if (recipient.Version != expectedVersion)
        {
            return new NotificationActionResult(
                NotificationActionStatus.Conflict,
                notification,
                recipient);
        }

        var updated = NotificationRepositoryHelpers.ApplyAction(recipient, actionKind, atUtc);

        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE notification_recipients
                SET read_at_utc = @read_at_utc,
                    acknowledged_at_utc = @acknowledged_at_utc,
                    completed_at_utc = @completed_at_utc,
                    change_seq = nextval('notification_change_seq'),
                    row_version = @new_version
                WHERE notification_id = @notification_id
                  AND recipient_key = @recipient_key
                  AND row_version = @expected_version;
                """;
            PostgresMapping.AddNullableParameter(updateCmd, "read_at_utc", updated.ReadAtUtc);
            PostgresMapping.AddNullableParameter(updateCmd, "acknowledged_at_utc", updated.AcknowledgedAtUtc);
            PostgresMapping.AddNullableParameter(updateCmd, "completed_at_utc", updated.CompletedAtUtc);
            updateCmd.Parameters.AddWithValue("new_version", updated.Version);
            updateCmd.Parameters.AddWithValue("notification_id", notificationId);
            updateCmd.Parameters.AddWithValue("recipient_key", recipient.RecipientKey);
            updateCmd.Parameters.AddWithValue("expected_version", expectedVersion);

            var rows = await updateCmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
            {
                return new NotificationActionResult(NotificationActionStatus.Conflict, notification, recipient);
            }
        }

        if (normalizedKey is not null)
        {
            await using var insertIdemp = conn.CreateCommand();
            insertIdemp.Transaction = tx;
            insertIdemp.CommandText = """
                INSERT INTO notification_idempotency (idempotency_key, actor_sid, action, created_at_utc)
                VALUES (@idempotency_key, @actor_sid, @action, @created_at_utc)
                ON CONFLICT (idempotency_key) DO NOTHING;
                """;
            insertIdemp.Parameters.AddWithValue("idempotency_key", normalizedKey);
            insertIdemp.Parameters.AddWithValue("actor_sid", actorSid);
            insertIdemp.Parameters.AddWithValue("action", expectedAction);
            insertIdemp.Parameters.AddWithValue("created_at_utc", atUtc);
            await insertIdemp.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new NotificationActionResult(NotificationActionStatus.Success, notification, updated);
    }

    public async Task<bool> RecordDeliveryAsync(
        Guid notificationId,
        IReadOnlyCollection<string> callerRecipientKeys,
        NotificationDeliveryRecord delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerRecipientKeys);
        ArgumentNullException.ThrowIfNull(delivery);

        var distinctKeys = callerRecipientKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctKeys.Length == 0)
        {
            return false;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        NotificationType? notificationType = null;
        var matchedByKey = new Dictionary<string, NotificationRecipientState>(StringComparer.OrdinalIgnoreCase);

        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT n.notification_type,
                       nr.recipient_key, nr.change_seq, nr.delivered_at_utc, nr.read_at_utc,
                       nr.acknowledged_at_utc, nr.completed_at_utc, nr.cancelled_at_utc, nr.row_version
                FROM notification_recipients nr
                INNER JOIN notifications n ON n.notification_id = nr.notification_id
                WHERE nr.notification_id = @notification_id
                  AND nr.recipient_key = ANY(@recipient_keys)
                FOR UPDATE OF nr;
                """;
            selectCmd.Parameters.AddWithValue("notification_id", notificationId);
            selectCmd.Parameters.AddWithValue("recipient_keys", distinctKeys);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                notificationType ??= Enum.Parse<NotificationType>(reader.GetString(0), ignoreCase: true);
                var state = ReadRecipientState(reader, notificationType.Value, notificationId, offset: 1);
                matchedByKey[state.RecipientKey] = state;
            }
        }

        var recipient = NotificationRepositoryHelpers.SelectFirstMatchingRecipient(
            callerRecipientKeys,
            key => matchedByKey.TryGetValue(key.Trim(), out var state) ? state : null);

        if (recipient is null)
        {
            return false;
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO notification_deliveries (
                    delivery_id, notification_id, recipient_key, device_id, session_id, outcome, recorded_at_utc)
                VALUES (
                    @delivery_id, @notification_id, @recipient_key, @device_id, @session_id, @outcome, @recorded_at_utc);
                """;
            insertCmd.Parameters.AddWithValue("delivery_id", delivery.DeliveryId);
            insertCmd.Parameters.AddWithValue("notification_id", notificationId);
            insertCmd.Parameters.AddWithValue("recipient_key", recipient.RecipientKey);
            PostgresMapping.AddNullableParameter(insertCmd, "device_id", delivery.DeviceId);
            PostgresMapping.AddNullableParameter(insertCmd, "session_id", delivery.SessionId);
            insertCmd.Parameters.AddWithValue("outcome", delivery.Outcome);
            insertCmd.Parameters.AddWithValue("recorded_at_utc", delivery.RecordedAtUtc);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        if (recipient.DeliveredAtUtc is null)
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE notification_recipients
                SET delivered_at_utc = @delivered_at_utc,
                    change_seq = nextval('notification_change_seq'),
                    row_version = row_version + 1
                WHERE notification_id = @notification_id
                  AND recipient_key = @recipient_key
                  AND delivered_at_utc IS NULL;
                """;
            updateCmd.Parameters.AddWithValue("delivered_at_utc", delivery.RecordedAtUtc);
            updateCmd.Parameters.AddWithValue("notification_id", notificationId);
            updateCmd.Parameters.AddWithValue("recipient_key", recipient.RecipientKey);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<NotificationEntity?> CancelNotificationAsync(
        Guid notificationId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        NotificationEntity? existing = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT notification_id, source_id, notification_type, priority, title, safe_summary,
                       target_type, created_by_sid, created_at_utc, due_at_utc, revision, cancelled_at_utc
                FROM notifications
                WHERE notification_id = @notification_id
                FOR UPDATE;
                """;
            selectCmd.Parameters.AddWithValue("notification_id", notificationId);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existing = ReadNotification(reader, offset: 0);
            }
        }

        if (existing is null)
        {
            return null;
        }

        if (existing.CancelledAtUtc is not null)
        {
            await tx.CommitAsync(ct);
            return existing;
        }

        await using (var updateNotif = conn.CreateCommand())
        {
            updateNotif.Transaction = tx;
            updateNotif.CommandText = """
                UPDATE notifications
                SET cancelled_at_utc = @cancelled_at_utc,
                    revision = revision + 1
                WHERE notification_id = @notification_id;
                """;
            updateNotif.Parameters.AddWithValue("cancelled_at_utc", cancelledAtUtc);
            updateNotif.Parameters.AddWithValue("notification_id", notificationId);
            await updateNotif.ExecuteNonQueryAsync(ct);
        }

        await using (var updateRecips = conn.CreateCommand())
        {
            updateRecips.Transaction = tx;
            updateRecips.CommandText = """
                UPDATE notification_recipients
                SET cancelled_at_utc = COALESCE(cancelled_at_utc, @cancelled_at_utc),
                    change_seq = nextval('notification_change_seq'),
                    row_version = row_version + 1
                WHERE notification_id = @notification_id;
                """;
            updateRecips.Parameters.AddWithValue("cancelled_at_utc", cancelledAtUtc);
            updateRecips.Parameters.AddWithValue("notification_id", notificationId);
            await updateRecips.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return existing with
        {
            CancelledAtUtc = cancelledAtUtc,
            Revision = existing.Revision + 1
        };
    }

    private static NotificationEntity ReadNotification(NpgsqlDataReader reader, int offset) =>
        new(
            reader.GetGuid(offset),
            reader.GetString(offset + 1),
            Enum.Parse<NotificationType>(reader.GetString(offset + 2), ignoreCase: true),
            Enum.Parse<NotificationPriority>(reader.GetString(offset + 3), ignoreCase: true),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5),
            Enum.Parse<NotificationTargetType>(reader.GetString(offset + 6), ignoreCase: true),
            reader.GetString(offset + 7),
            reader.GetFieldValue<DateTimeOffset>(offset + 8),
            reader.IsDBNull(offset + 9) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 9),
            reader.GetInt64(offset + 10),
            reader.IsDBNull(offset + 11) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 11));

    // Expected columns starting at offset:
    // +0 recipient_key, +1 change_seq, +2 delivered_at_utc, +3 read_at_utc,
    // +4 acknowledged_at_utc, +5 completed_at_utc, +6 cancelled_at_utc, +7 row_version
    private static NotificationRecipientState ReadRecipientState(
        NpgsqlDataReader reader,
        NotificationType type,
        Guid notificationId,
        int offset) =>
        new(
            notificationId,
            reader.GetString(offset),
            type,
            reader.IsDBNull(offset + 2) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 3),
            reader.IsDBNull(offset + 4) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 6),
            reader.GetInt64(offset + 7));
}
