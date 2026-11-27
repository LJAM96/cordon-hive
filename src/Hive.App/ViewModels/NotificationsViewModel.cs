using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cordon.Shared.Windows;
using Hive.App.Services;
using Hive.App.Views;
using Hive.Contracts;
using Hive.Domain.Identity;
using Hive.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public static class NotificationPresentation
{
    public static string GetPriorityLabel(string? rawPriority) =>
        Enum.TryParse<NotificationPriority>(rawPriority, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : rawPriority ?? "Normal";

    public static string GetPriorityGlyph(string? rawPriority)
    {
        if (!Enum.TryParse<NotificationPriority>(rawPriority, ignoreCase: true, out var parsed))
        {
            return "\uE946";
        }

        return parsed switch
        {
            NotificationPriority.Critical => "\uE783", // Error / Critical
            NotificationPriority.High => "\uE7BA",     // Warning / High
            NotificationPriority.Normal => "\uE946",   // Info
            NotificationPriority.Low => "\uE74B",      // Low / ChevronDown
            _ => "\uE946"
        };
    }

    public static string GetTypeLabel(string? rawType)
    {
        if (!Enum.TryParse<NotificationType>(rawType, ignoreCase: true, out var parsed))
        {
            return rawType ?? "Notification";
        }

        return parsed switch
        {
            NotificationType.Informational => "Informational",
            NotificationType.Announcement => "Announcement",
            NotificationType.RequiredAction => "Required Action",
            NotificationType.SoftwareUpdate => "Software Update",
            NotificationType.Maintenance => "Maintenance",
            NotificationType.CriticalCondition => "Critical Condition",
            _ => parsed.ToString()
        };
    }

    public static string GetTypeGlyph(string? rawType)
    {
        if (!Enum.TryParse<NotificationType>(rawType, ignoreCase: true, out var parsed))
        {
            return "\uEA8F";
        }

        return parsed switch
        {
            NotificationType.Informational => "\uE946",     // Info
            NotificationType.Announcement => "\uE789",      // Megaphone
            NotificationType.RequiredAction => "\uE73E",    // CheckMark / Action
            NotificationType.SoftwareUpdate => "\uE777",    // UpdateRestore
            NotificationType.Maintenance => "\uE90F",       // Repair
            NotificationType.CriticalCondition => "\uE783", // Error
            _ => "\uEA8F"
        };
    }

    public static string FormatLocalTimestamp(DateTimeOffset? utc, string fallback = "—") =>
        utc is { } value ? value.ToLocalTime().ToString("g") : fallback;
}

public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(
        UserNotificationDto dto,
        IReadOnlyList<PendingNotificationAction> pendingActions,
        bool canCompleteSoftwareUpdates)
    {
        Dto = dto;
        NotificationId = dto.NotificationId;
        Title = dto.Title;
        SafeSummary = dto.SafeSummary;
        Type = dto.Type;
        Priority = dto.Priority;
        RecipientVersion = dto.RecipientVersion;
        IsCancelled = dto.CancelledAtUtc is not null;

        TypeLabel = NotificationPresentation.GetTypeLabel(dto.Type);
        TypeGlyph = NotificationPresentation.GetTypeGlyph(dto.Type);
        PriorityLabel = NotificationPresentation.GetPriorityLabel(dto.Priority);
        PriorityGlyph = NotificationPresentation.GetPriorityGlyph(dto.Priority);

        CreatedLocalText = $"Sent {NotificationPresentation.FormatLocalTimestamp(dto.CreatedAtUtc)}";
        DueLocalText = dto.DueAtUtc is { } due
            ? $"Due {due.ToLocalTime():g}"
            : string.Empty;
        DueVisibility = dto.DueAtUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;

        var hasPendingRead = pendingActions.Any(p => MatchesKind(p, NotificationActionKind.Read));
        var hasPendingAck = pendingActions.Any(p => MatchesKind(p, NotificationActionKind.Acknowledge));
        var hasPendingComplete = pendingActions.Any(p => MatchesKind(p, NotificationActionKind.Complete));

        IsPendingSync = hasPendingRead || hasPendingAck || hasPendingComplete;
        PendingSyncText = IsPendingSync
            ? $"Pending Sync ({string.Join(", ", pendingActions.Select(p => p.ActionKind))})"
            : string.Empty;
        PendingSyncVisibility = IsPendingSync ? Visibility.Visible : Visibility.Collapsed;

        IsUnresolved = UserNotificationCache.IsItemUnresolved(dto, pendingActions);

        var readPart = dto.ReadAtUtc is { } readAt
            ? $"Read {readAt.ToLocalTime():g}"
            : hasPendingRead ? "Read (Pending Sync)" : "Unread";
        var ackPart = dto.AcknowledgedAtUtc is { } ackAt
            ? $"Ack {ackAt.ToLocalTime():g}"
            : hasPendingAck ? "Ack (Pending Sync)" : "Not Acked";
        var completePart = dto.CompletedAtUtc is { } compAt
            ? $"Completed {compAt.ToLocalTime():g}"
            : hasPendingComplete ? "Completed (Pending Sync)" : "Not Completed";

        LifecycleSummaryText = IsCancelled
            ? $"Cancelled {NotificationPresentation.FormatLocalTimestamp(dto.CancelledAtUtc)}"
            : $"{readPart}  ·  {ackPart}  ·  {completePart}";

        StatusBadgeText = IsCancelled
            ? "Cancelled"
            : IsUnresolved ? "Action Required" : "Resolved";

        CanMarkRead = !IsCancelled && dto.ReadAtUtc is null && !hasPendingRead;
        CanAcknowledge = !IsCancelled && dto.AcknowledgedAtUtc is null && !hasPendingAck;

        var isSoftwareUpdate = string.Equals(dto.Type, nameof(NotificationType.SoftwareUpdate), StringComparison.OrdinalIgnoreCase);
        var rolePermitsComplete = !isSoftwareUpdate || canCompleteSoftwareUpdates;
        CompleteVisibility = rolePermitsComplete ? Visibility.Visible : Visibility.Collapsed;
        CanComplete = rolePermitsComplete && !IsCancelled && dto.CompletedAtUtc is null && !hasPendingComplete;

        AccessibilityName = $"{PriorityLabel} {TypeLabel}: {Title}. {StatusBadgeText}. {LifecycleSummaryText}";
    }

    public UserNotificationDto Dto { get; }
    public Guid NotificationId { get; }
    public string Title { get; }
    public string SafeSummary { get; }
    public string Type { get; }
    public string Priority { get; }
    public long RecipientVersion { get; }
    public bool IsCancelled { get; }
    public bool IsUnresolved { get; }
    public bool IsPendingSync { get; }
    public string TypeLabel { get; }
    public string TypeGlyph { get; }
    public string PriorityLabel { get; }
    public string PriorityGlyph { get; }
    public string CreatedLocalText { get; }
    public string DueLocalText { get; }
    public Visibility DueVisibility { get; }
    public string PendingSyncText { get; }
    public Visibility PendingSyncVisibility { get; }
    public string LifecycleSummaryText { get; }
    public string StatusBadgeText { get; }
    public bool CanMarkRead { get; }
    public bool CanAcknowledge { get; }
    public bool CanComplete { get; }
    public Visibility CompleteVisibility { get; }
    public string AccessibilityName { get; }

    private static bool MatchesKind(PendingNotificationAction action, NotificationActionKind kind) =>
        string.Equals(action.ActionKind, kind.ToString(), StringComparison.OrdinalIgnoreCase);
}

public sealed class AdminNotificationSummaryItemViewModel
{
    public AdminNotificationSummaryItemViewModel(AdminNotificationSummaryDto dto, ActorIdentity actor)
    {
        Dto = dto;
        NotificationId = dto.NotificationId;
        Title = dto.Title;
        SafeSummary = dto.SafeSummary;
        TypeLabel = NotificationPresentation.GetTypeLabel(dto.Type);
        PriorityLabel = NotificationPresentation.GetPriorityLabel(dto.Priority);
        TargetSummaryText = $"Target: {dto.TargetType} · Created by {dto.CreatedBySid}";
        CreatedLocalText = $"Sent {NotificationPresentation.FormatLocalTimestamp(dto.CreatedAtUtc)}";
        DueLocalText = dto.DueAtUtc is { } due
            ? $"Due {due.ToLocalTime():g}"
            : "No due date";
        DeliveryCountsText =
            $"Delivered {dto.DeliveredCount}/{dto.TotalRecipients} · Read {dto.ReadCount} · Ack {dto.AcknowledgedCount} · Completed {dto.CompletedCount} · Unresolved {dto.UnresolvedCount}";
        StatusText = dto.IsCancelled ? "Cancelled" : "Active";
        CanCancel = !dto.IsCancelled && ScopeAuthorizationService.CanCancelNotification(actor, dto.CreatedBySid);
        CancelVisibility = CanCancel ? Visibility.Visible : Visibility.Collapsed;
        AccessibilityName = $"{Title} ({TypeLabel}, {PriorityLabel}): {StatusText}. {DeliveryCountsText}";
    }

    public AdminNotificationSummaryDto Dto { get; }
    public Guid NotificationId { get; }
    public string Title { get; }
    public string SafeSummary { get; }
    public string TypeLabel { get; }
    public string PriorityLabel { get; }
    public string TargetSummaryText { get; }
    public string CreatedLocalText { get; }
    public string DueLocalText { get; }
    public string DeliveryCountsText { get; }
    public string StatusText { get; }
    public bool CanCancel { get; }
    public Visibility CancelVisibility { get; }
    public string AccessibilityName { get; }
}

public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly HiveApiClient _apiClient;
    private readonly MainViewModel _mainViewModel;
    private readonly UserNotificationCache _cache;
    private readonly AppToastNotifier _toastNotifier;

    private string? _activeUserSid;
    private ActorIdentity? _activeActor;
    private CachedNotificationStore _currentStore = new(null, null, [], []);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int UnresolvedCount { get; set; }

    [ObservableProperty]
    public partial bool IsStaleOrOffline { get; set; }

    [ObservableProperty]
    public partial bool CanPublishNotifications { get; set; }

    [ObservableProperty]
    public partial bool CanCompleteSoftwareUpdates { get; set; }

    [ObservableProperty]
    public partial Visibility PublishSectionVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility EmptyNotificationsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility EmptyPublishedVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial string RoleScopeSummaryText { get; set; } = "Checking permissions...";

    [ObservableProperty]
    public partial string LastSyncedLocalText { get; set; } = "Never synced";

    [ObservableProperty]
    public partial string WidgetStatusText { get; set; } = "Widget: Closed";

    [ObservableProperty]
    public partial bool IsFeedbackOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity FeedbackSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string FeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackMessage { get; set; } = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public string BadgeText => BadgeDisplayFormatter.FormatCount(UnresolvedCount);

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    public ObservableCollection<AdminNotificationSummaryItemViewModel> PublishedNotifications { get; } = [];

    public event EventHandler? WidgetStateChanged;

    public NotificationsViewModel(
        HiveApiClient apiClient,
        MainViewModel mainViewModel,
        UserNotificationCache cache,
        AppToastNotifier toastNotifier)
    {
        _apiClient = apiClient;
        _mainViewModel = mainViewModel;
        _cache = cache;
        _toastNotifier = toastNotifier;

        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var actor = await ResolveAuthenticatedActorAsync();
            if (actor is null)
            {
                if (!string.IsNullOrWhiteSpace(_activeUserSid) && _activeActor is not null)
                {
                    // Authenticated earlier in this session, now offline: show cached notifications + stale banner.
                    var offlineStore = _cache.Load(_activeUserSid);
                    _currentStore = offlineStore;
                    ProjectStoreToUi(offlineStore, _activeActor);
                    IsStaleOrOffline = true;
                    SetFeedback(
                        InfoBarSeverity.Warning,
                        "Offline — Showing Cached Notifications",
                        $"Server unreachable. Showing {Notifications.Count} cached notification(s) ({UnresolvedCount} unresolved) from {FormatLastSynced(offlineStore.LastSyncedAtUtc)}.");
                    NotifyWidgetChanged();
                    return;
                }

                ClearInMemoryState("Sign in to view your notifications.");
                return;
            }

            _activeUserSid = actor.UserSid;
            _activeActor = actor;
            UpdateCapabilities(actor);

            // Immediately load per-user DPAPI cache so UI has instant offline-resilient state.
            var store = _cache.Load(actor.UserSid);
            _currentStore = store;
            ProjectStoreToUi(store, actor);
            NotifyWidgetChanged();

            var flushOutcome = await FlushPendingActionsAsync(actor.UserSid, store);
            store = flushOutcome.Store;
            _currentStore = store;

            if (flushOutcome.StoppedOffline)
            {
                IsStaleOrOffline = true;
                ProjectStoreToUi(store, actor);
                SetFeedback(
                    InfoBarSeverity.Warning,
                    "Offline — Cached & Pending Sync",
                    $"Hive server is unreachable. {store.PendingActions.Count} queued action(s) will sync automatically on reconnect.");
                NotifyWidgetChanged();
                return;
            }

            var previousById = store.Items.ToDictionary(i => i.NotificationId);
            var syncResult = await _apiClient.SyncMyNotificationsAsync(store.Cursor);
            if (!syncResult.IsSuccess || syncResult.Value is null)
            {
                IsStaleOrOffline = true;
                ProjectStoreToUi(store, actor);
                SetFeedback(
                    syncResult.ErrorKind is ApiErrorKind.Unauthorized or ApiErrorKind.Forbidden
                        ? InfoBarSeverity.Error
                        : InfoBarSeverity.Warning,
                    syncResult.ErrorKind == ApiErrorKind.Offline
                        ? "Offline — Cached Notifications"
                        : "Notification Sync Failed",
                    $"{syncResult.ErrorMessage} Showing cached state ({UnresolvedCount} unresolved) from {FormatLastSynced(store.LastSyncedAtUtc)}.");
                NotifyWidgetChanged();
                return;
            }

            var sync = syncResult.Value;
            var mergedStore = _cache.ApplySync(store, sync);
            _cache.Save(actor.UserSid, mergedStore);
            _currentStore = mergedStore;

            IsStaleOrOffline = false;
            LastSyncedLocalText = $"Synced {sync.ServerTimeUtc.ToLocalTime():g}";
            ProjectStoreToUi(mergedStore, actor);

            await DeliverNewArrivalsAsync(sync.Items, previousById);

            if (CanPublishNotifications)
            {
                await LoadPublishedNotificationsAsync(actor);
            }
            else
            {
                PublishedNotifications.Clear();
                EmptyPublishedVisibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(flushOutcome.RejectionMessage))
            {
                SetFeedback(
                    InfoBarSeverity.Warning,
                    "Server Rejected Queued Action",
                    flushOutcome.RejectionMessage);
            }
            else if (IsFeedbackOpen && FeedbackSeverity == InfoBarSeverity.Warning)
            {
                IsFeedbackOpen = false;
            }

            NotifyWidgetChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task MarkReadAsync(NotificationItemViewModel item) =>
        ExecuteItemActionAsync(item, NotificationActionKind.Read);

    public Task AcknowledgeAsync(NotificationItemViewModel item) =>
        ExecuteItemActionAsync(item, NotificationActionKind.Acknowledge);

    public Task CompleteAsync(NotificationItemViewModel item) =>
        ExecuteItemActionAsync(item, NotificationActionKind.Complete);

    public async Task<string?> PublishNotificationAsync(
        string title,
        string safeSummary,
        string type,
        string priority,
        string targetType,
        string targetKeysCsv,
        DateTimeOffset? dueAtUtc)
    {
        if (!CanPublishNotifications)
        {
            const string denied = "Only Supervisor, SoftwareAdmin, or Administrator roles can publish notifications.";
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", denied);
            return denied;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(safeSummary))
        {
            return "Title and Safe Summary are required.";
        }

        var keys = string.IsNullOrWhiteSpace(targetKeysCsv)
            ? Array.Empty<string>()
            : targetKeysCsv
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (!string.Equals(targetType, nameof(NotificationTargetType.AllUsers), StringComparison.OrdinalIgnoreCase) &&
            keys.Length == 0)
        {
            return $"At least one target key is required for target type '{targetType}'.";
        }

        var request = new CreateNotificationRequest(
            type,
            priority,
            title.Trim(),
            safeSummary.Trim(),
            targetType,
            keys,
            dueAtUtc,
            SourceId: "hive-ui");

        var result = await _apiClient.PublishNotificationAsync(request);
        if (!result.IsSuccess || result.Value is null)
        {
            var err = result.ErrorMessage ?? "Unable to publish notification.";
            SetFeedback(InfoBarSeverity.Error, "Publish Failed", err);
            return err;
        }

        await LoadAsync();
        SetFeedback(
            InfoBarSeverity.Success,
            "Notification Published",
            $"Published '{result.Value.Title}' to {result.Value.TotalRecipients} recipient(s).");
        return null;
    }

    public async Task<string?> CancelPublishedNotificationAsync(
        AdminNotificationSummaryItemViewModel item,
        string reason)
    {
        if (item is null || !item.CanCancel)
        {
            const string denied = "You are not authorized to cancel this notification.";
            SetFeedback(InfoBarSeverity.Error, "Permission Denied", denied);
            return denied;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "A cancellation reason is required.";
        }

        var result = await _apiClient.CancelNotificationAsync(item.NotificationId, reason.Trim());
        if (!result.IsSuccess)
        {
            var err = result.ErrorMessage ?? "Unable to cancel notification.";
            SetFeedback(InfoBarSeverity.Error, "Cancel Failed", err);
            return err;
        }

        await LoadAsync();
        SetFeedback(InfoBarSeverity.Success, "Notification Cancelled", $"Cancelled '{item.Title}'.");
        return null;
    }

    public IReadOnlyList<WidgetPreviewItem> BuildWidgetPreviewItems(int maxCount = 5) =>
        Notifications
            .Where(n => n.IsUnresolved && !n.IsCancelled)
            .Take(maxCount)
            .Select(n => new WidgetPreviewItem(
                n.Title,
                $"{n.PriorityLabel} · {n.TypeLabel}" + (n.IsPendingSync ? " · Pending Sync" : string.Empty)))
            .ToList();

    public void UpdateWidgetStatusLabel(bool isOpen, bool isTrayFallback)
    {
        WidgetStatusText = !isOpen
            ? "Widget: Closed"
            : isTrayFallback
                ? "Widget: Tray Fallback"
                : "Widget: Docked";
    }

    private async Task ExecuteItemActionAsync(NotificationItemViewModel item, NotificationActionKind kind)
    {
        if (item is null || _activeActor is null || string.IsNullOrWhiteSpace(_activeUserSid))
        {
            return;
        }

        if (kind == NotificationActionKind.Complete &&
            !ScopeAuthorizationService.CanCompleteNotification(_activeActor, item.Type))
        {
            SetFeedback(
                InfoBarSeverity.Error,
                "Completion Restricted",
                "Only SoftwareAdmin or Administrator roles can complete SoftwareUpdate notifications.");
            return;
        }

        IsBusy = true;
        try
        {
            var idempotencyKey = Guid.NewGuid().ToString("N");
            var result = await SendActionAsync(item.NotificationId, kind, item.RecipientVersion, idempotencyKey);

            if (result.IsSuccess)
            {
                if (result.Value is { } updatedDto)
                {
                    UpsertDtoInStore(_currentStore, updatedDto);
                    _cache.Save(_activeUserSid, _currentStore);
                }

                await LoadAsync();
                return;
            }

            if (result.ErrorKind == ApiErrorKind.Offline)
            {
                QueuePendingAction(item, kind, idempotencyKey);
                ProjectStoreToUi(_currentStore, _activeActor);
                IsStaleOrOffline = true;
                SetFeedback(
                    InfoBarSeverity.Warning,
                    "Queued Offline (Pending Sync)",
                    $"'{item.Title}' action '{kind}' is queued locally with Pending Sync. Server state remains authoritative when reconnected.");
                NotifyWidgetChanged();
                return;
            }

            SetFeedback(
                InfoBarSeverity.Error,
                result.ErrorKind == ApiErrorKind.Conflict ? "Version Conflict (409)" : "Action Rejected by Server",
                result.ErrorMessage ?? "The server rejected this notification action.");
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void QueuePendingAction(NotificationItemViewModel item, NotificationActionKind kind, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(_activeUserSid))
        {
            return;
        }

        var kindName = kind.ToString();
        _currentStore.PendingActions.RemoveAll(p =>
            p.NotificationId == item.NotificationId &&
            string.Equals(p.ActionKind, kindName, StringComparison.OrdinalIgnoreCase));

        _currentStore.PendingActions.Add(new PendingNotificationAction(
            item.NotificationId,
            kindName,
            item.RecipientVersion,
            idempotencyKey,
            DateTimeOffset.UtcNow));

        _cache.Save(_activeUserSid, _currentStore);
    }

    private async Task<(CachedNotificationStore Store, bool StoppedOffline, string? RejectionMessage)> FlushPendingActionsAsync(
        string userSid,
        CachedNotificationStore store)
    {
        if (store.PendingActions.Count == 0)
        {
            return (store, false, null);
        }

        var remaining = new List<PendingNotificationAction>();
        var rejections = new List<string>();
        var stoppedOffline = false;

        for (var i = 0; i < store.PendingActions.Count; i++)
        {
            var pending = store.PendingActions[i];
            if (!Enum.TryParse<NotificationActionKind>(pending.ActionKind, ignoreCase: true, out var kind))
            {
                continue;
            }

            var result = await SendActionAsync(
                pending.NotificationId,
                kind,
                pending.ExpectedVersion,
                pending.IdempotencyKey);

            if (result.IsSuccess)
            {
                if (result.Value is { } updated)
                {
                    UpsertDtoInStore(store, updated);
                }
                continue;
            }

            if (result.ErrorKind == ApiErrorKind.Offline)
            {
                stoppedOffline = true;
                remaining.AddRange(store.PendingActions.Skip(i));
                break;
            }

            rejections.Add($"{pending.ActionKind}: {result.ErrorMessage ?? result.ErrorKind.ToString()}");
        }

        var updatedStore = new CachedNotificationStore(
            store.Cursor,
            store.LastSyncedAtUtc,
            store.Items,
            remaining);
        _cache.Save(userSid, updatedStore);

        var rejectionSummary = rejections.Count > 0 ? string.Join(" | ", rejections) : null;
        return (updatedStore, stoppedOffline, rejectionSummary);
    }

    private Task<ApiResult<UserNotificationDto?>> SendActionAsync(
        Guid notificationId,
        NotificationActionKind kind,
        long expectedVersion,
        string idempotencyKey) =>
        kind switch
        {
            NotificationActionKind.Read => _apiClient.MarkNotificationReadAsync(notificationId, expectedVersion, idempotencyKey),
            NotificationActionKind.Acknowledge => _apiClient.AcknowledgeNotificationAsync(notificationId, expectedVersion, idempotencyKey),
            NotificationActionKind.Complete => _apiClient.CompleteNotificationAsync(notificationId, expectedVersion, idempotencyKey),
            _ => Task.FromResult(ApiResult<UserNotificationDto?>.Failure(ApiErrorKind.BadRequest, "Unsupported action kind."))
        };

    private async Task DeliverNewArrivalsAsync(
        IReadOnlyList<UserNotificationDto> incomingItems,
        IReadOnlyDictionary<Guid, UserNotificationDto> previousById)
    {
        foreach (var item in incomingItems)
        {
            if (item.CancelledAtUtc is not null)
            {
                continue;
            }

            var isNewOrRevised = !previousById.TryGetValue(item.NotificationId, out var prev) ||
                                 prev.Revision < item.Revision;

            if (!isNewOrRevised && item.DeliveredAtUtc is not null)
            {
                continue;
            }

            var toastShown = item.ReadAtUtc is null && _toastNotifier.TryShowNotification(item);
            if (item.DeliveredAtUtc is null || toastShown)
            {
                var outcome = toastShown ? "ToastShown" : "DeliveredToApp";
                await _apiClient.RecordNotificationDeliveryAsync(
                    item.NotificationId,
                    new RecordDeliveryRequest(DeviceId: null, SessionId: Environment.ProcessId, Outcome: outcome));
            }
        }
    }

    private async Task LoadPublishedNotificationsAsync(ActorIdentity actor)
    {
        var adminResult = await _apiClient.GetAdminNotificationsAsync();
        PublishedNotifications.Clear();

        if (adminResult.IsSuccess && adminResult.Value is not null)
        {
            foreach (var summary in adminResult.Value.OrderByDescending(s => s.CreatedAtUtc))
            {
                PublishedNotifications.Add(new AdminNotificationSummaryItemViewModel(summary, actor));
            }
        }

        EmptyPublishedVisibility = PublishedNotifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<ActorIdentity?> ResolveAuthenticatedActorAsync()
    {
        if (_mainViewModel.CurrentUser is { } cachedMe)
        {
            return ToActorIdentity(cachedMe);
        }

        var meResult = await _apiClient.GetMeAsync();
        if (meResult.IsSuccess && meResult.Value is not null)
        {
            return ToActorIdentity(meResult.Value);
        }

        return null;
    }

    private void UpdateCapabilities(ActorIdentity actor)
    {
        CanPublishNotifications = ScopeAuthorizationService.CanPublishNotifications(actor);
        CanCompleteSoftwareUpdates = actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.Administrator);
        PublishSectionVisibility = CanPublishNotifications ? Visibility.Visible : Visibility.Collapsed;

        var rolesText = actor.Roles.Count > 0
            ? string.Join(", ", actor.Roles.Select(r => r.ToString()))
            : "None";
        RoleScopeSummaryText = $"Signed in as {actor.DisplayName} · Roles: {rolesText}";
    }

    private void ProjectStoreToUi(CachedNotificationStore store, ActorIdentity actor)
    {
        var canCompleteUpdates = actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.Administrator);
        var pendingByNotification = (store.PendingActions ?? [])
            .GroupBy(p => p.NotificationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PendingNotificationAction>)g.ToList());

        Notifications.Clear();
        foreach (var item in (store.Items ?? []).OrderByDescending(i => i.CreatedAtUtc))
        {
            var pending = pendingByNotification.TryGetValue(item.NotificationId, out var list)
                ? list
                : Array.Empty<PendingNotificationAction>();
            Notifications.Add(new NotificationItemViewModel(item, pending, canCompleteUpdates));
        }

        EmptyNotificationsVisibility = Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UnresolvedCount = _cache.ComputeUnresolvedCount(store);
        LastSyncedLocalText = store.LastSyncedAtUtc is { } synced
            ? $"Synced {synced.ToLocalTime():g}"
            : "Not yet synced";
    }

    private void ClearInMemoryState(string message)
    {
        _activeUserSid = null;
        _activeActor = null;
        _currentStore = new CachedNotificationStore(null, null, [], []);
        Notifications.Clear();
        PublishedNotifications.Clear();
        EmptyNotificationsVisibility = Visibility.Visible;
        EmptyPublishedVisibility = Visibility.Visible;
        CanPublishNotifications = false;
        CanCompleteSoftwareUpdates = false;
        PublishSectionVisibility = Visibility.Collapsed;
        UnresolvedCount = 0;
        IsStaleOrOffline = false;
        RoleScopeSummaryText = "Not authenticated";
        LastSyncedLocalText = "Not yet synced";
        SetFeedback(InfoBarSeverity.Informational, "Authentication Required", message);
        NotifyWidgetChanged();
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSignedIn) or nameof(MainViewModel.CurrentUser))
        {
            if (!_mainViewModel.IsSignedIn || _mainViewModel.CurrentUser is null)
            {
                _toastNotifier.ResetSessionTracking();
                ClearInMemoryState("Signed out. Notification cache remains encrypted and isolated per user.");
            }
            else if (!string.Equals(_activeUserSid, _mainViewModel.CurrentUser.UserSid, StringComparison.OrdinalIgnoreCase))
            {
                _toastNotifier.ResetSessionTracking();
                var actor = ToActorIdentity(_mainViewModel.CurrentUser);
                _activeUserSid = actor.UserSid;
                _activeActor = actor;
                UpdateCapabilities(actor);
                var store = _cache.Load(actor.UserSid);
                _currentStore = store;
                ProjectStoreToUi(store, actor);
                NotifyWidgetChanged();
            }
        }
    }

    private void NotifyWidgetChanged() => WidgetStateChanged?.Invoke(this, EventArgs.Empty);

    private void SetFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackSeverity = severity;
        FeedbackTitle = title;
        FeedbackMessage = message;
        IsFeedbackOpen = true;
    }

    private static void UpsertDtoInStore(CachedNotificationStore store, UserNotificationDto dto)
    {
        var idx = store.Items.FindIndex(i => i.NotificationId == dto.NotificationId);
        if (idx >= 0)
        {
            store.Items[idx] = dto;
        }
        else
        {
            store.Items.Insert(0, dto);
        }
    }

    private static string FormatLastSynced(DateTimeOffset? lastSynced) =>
        lastSynced is { } ts ? ts.ToLocalTime().ToString("g") : "never";

    private static ActorIdentity ToActorIdentity(MeResponse me)
    {
        var roles = new HashSet<HiveRole>();
        foreach (var raw in me.Roles)
        {
            if (Enum.TryParse<HiveRole>(raw, ignoreCase: true, out var role))
            {
                roles.Add(role);
            }
        }

        return new ActorIdentity(
            me.UserSid,
            me.DisplayName,
            roles,
            new HashSet<string>(me.AllowedSiteIds, StringComparer.OrdinalIgnoreCase),
            me.AllSitesAccess);
    }
}
