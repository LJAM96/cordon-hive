using System.Security.Cryptography;
using System.Text;
using Hive.Contracts;
using Hive.Notifications;

namespace Hive.App.Services;

public sealed record PendingNotificationAction(
    Guid NotificationId,
    string ActionKind,
    long ExpectedVersion,
    string IdempotencyKey,
    DateTimeOffset QueuedAtUtc);

public sealed record CachedNotificationStore(
    string? Cursor,
    DateTimeOffset? LastSyncedAtUtc,
    List<UserNotificationDto> Items,
    List<PendingNotificationAction> PendingActions);

/// <summary>
/// Per-user DPAPI-encrypted offline notification cache partitioned by SHA-256 of UserSid.
/// User B loading their SID never reads User A's cached notification file.
/// </summary>
public sealed class UserNotificationCache
{
    private readonly string _directory;

    public UserNotificationCache(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? DpapiStorageHelper.DefaultAppDataDirectory
            : directory;
    }

    public string GetCacheFilePath(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var normalized = userSid.Trim().ToUpperInvariant();
        var hashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(_directory, $"notifications_{hashHex}.dat");
    }

    public CachedNotificationStore Load(string userSid)
    {
        var filePath = GetCacheFilePath(userSid);
        var loaded = DpapiStorageHelper.LoadProtectedJson<CachedNotificationStore>(filePath);
        if (loaded is null)
        {
            return new CachedNotificationStore(null, null, [], []);
        }

        return new CachedNotificationStore(
            loaded.Cursor,
            loaded.LastSyncedAtUtc,
            loaded.Items ?? [],
            loaded.PendingActions ?? []);
    }

    public void Save(string userSid, CachedNotificationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var filePath = GetCacheFilePath(userSid);
        var normalized = new CachedNotificationStore(
            store.Cursor,
            store.LastSyncedAtUtc,
            store.Items ?? [],
            store.PendingActions ?? []);
        DpapiStorageHelper.SaveProtectedJson(filePath, normalized);
    }

    public void Clear(string userSid) =>
        DpapiStorageHelper.DeleteQuietly(GetCacheFilePath(userSid));

    public CachedNotificationStore ApplySync(CachedNotificationStore store, NotificationSyncResponse sync)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sync);

        var byId = new Dictionary<Guid, UserNotificationDto>();
        if (!sync.SnapshotReset && store.Items is not null)
        {
            foreach (var existing in store.Items)
            {
                byId[existing.NotificationId] = existing;
            }
        }

        foreach (var incoming in sync.Items)
        {
            byId[incoming.NotificationId] = incoming;
        }

        var mergedItems = byId.Values
            .OrderByDescending(i => i.CreatedAtUtc)
            .ThenBy(i => i.NotificationId)
            .ToList();

        var remainingPending = (store.PendingActions ?? [])
            .Where(p => byId.TryGetValue(p.NotificationId, out var current) && !IsSatisfiedByServer(p, current))
            .ToList();

        return new CachedNotificationStore(
            sync.Cursor,
            sync.ServerTimeUtc,
            mergedItems,
            remainingPending);
    }

    public int ComputeUnresolvedCount(CachedNotificationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var items = store.Items ?? [];
        var pendingByNotification = (store.PendingActions ?? [])
            .GroupBy(p => p.NotificationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var count = 0;
        foreach (var item in items)
        {
            if (IsItemUnresolved(item, pendingByNotification.GetValueOrDefault(item.NotificationId)))
            {
                count++;
            }
        }

        return count;
    }

    internal static bool IsItemUnresolved(
        UserNotificationDto item,
        IReadOnlyList<PendingNotificationAction>? pendingActions = null)
    {
        if (item.CancelledAtUtc is not null || !item.IsActionableUnresolved)
        {
            return false;
        }

        if (!Enum.TryParse<NotificationType>(item.Type, ignoreCase: true, out var notificationType))
        {
            return item.IsActionableUnresolved;
        }

        var hasPendingRead = pendingActions?.Any(p => IsActionKind(p.ActionKind, NotificationActionKind.Read)) == true;
        var hasPendingAck = pendingActions?.Any(p => IsActionKind(p.ActionKind, NotificationActionKind.Acknowledge)) == true;
        var hasPendingComplete = pendingActions?.Any(p => IsActionKind(p.ActionKind, NotificationActionKind.Complete)) == true;

        var now = DateTimeOffset.UtcNow;
        var state = new NotificationRecipientState(
            item.NotificationId,
            item.RecipientKey,
            notificationType,
            item.DeliveredAtUtc,
            item.ReadAtUtc ?? (hasPendingRead ? now : null),
            item.AcknowledgedAtUtc ?? (hasPendingAck ? now : null),
            item.CompletedAtUtc ?? (hasPendingComplete ? now : null),
            item.CancelledAtUtc,
            item.RecipientVersion);

        return state.IsActionableUnresolved();
    }

    private static bool IsSatisfiedByServer(PendingNotificationAction pending, UserNotificationDto serverItem)
    {
        if (serverItem.CancelledAtUtc is not null)
        {
            return true;
        }

        if (IsActionKind(pending.ActionKind, NotificationActionKind.Read))
        {
            return serverItem.ReadAtUtc is not null;
        }

        if (IsActionKind(pending.ActionKind, NotificationActionKind.Acknowledge))
        {
            return serverItem.AcknowledgedAtUtc is not null;
        }

        if (IsActionKind(pending.ActionKind, NotificationActionKind.Complete))
        {
            return serverItem.CompletedAtUtc is not null;
        }

        return false;
    }

    private static bool IsActionKind(string actionKind, NotificationActionKind expected) =>
        string.Equals(actionKind, expected.ToString(), StringComparison.OrdinalIgnoreCase);
}
