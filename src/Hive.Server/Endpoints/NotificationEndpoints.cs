using System.Globalization;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Devices;
using Hive.Domain.Identity;
using Hive.Infrastructure.Repositories;
using Hive.Notifications;
using Hive.Server.Authentication;
using Hive.Server.Middleware;

namespace Hive.Server.Endpoints;

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/notifications", PublishNotification)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/notifications", ListAdminNotifications)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/notifications/{id:guid}/cancel", CancelNotification)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/me/notifications", SyncMyNotifications)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/me/notifications/{id:guid}/delivery", RecordDelivery)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/me/notifications/{id:guid}/read", (
                Guid id,
                NotificationActionRequest request,
                HttpContext context,
                INotificationRepository notificationRepository,
                IDeviceRepository deviceRepository,
                IAuditRepository auditRepository,
                IOutboxRepository outboxRepository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            ApplyRecipientActionAsync(
                id,
                NotificationActionKind.Read,
                "NotificationRead",
                request,
                context,
                notificationRepository,
                deviceRepository,
                auditRepository,
                outboxRepository,
                timeProvider,
                cancellationToken))
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/me/notifications/{id:guid}/ack", (
                Guid id,
                NotificationActionRequest request,
                HttpContext context,
                INotificationRepository notificationRepository,
                IDeviceRepository deviceRepository,
                IAuditRepository auditRepository,
                IOutboxRepository outboxRepository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            ApplyRecipientActionAsync(
                id,
                NotificationActionKind.Acknowledge,
                "NotificationAcknowledged",
                request,
                context,
                notificationRepository,
                deviceRepository,
                auditRepository,
                outboxRepository,
                timeProvider,
                cancellationToken))
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/me/notifications/{id:guid}/complete", (
                Guid id,
                NotificationActionRequest request,
                HttpContext context,
                INotificationRepository notificationRepository,
                IDeviceRepository deviceRepository,
                IAuditRepository auditRepository,
                IOutboxRepository outboxRepository,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            ApplyRecipientActionAsync(
                id,
                NotificationActionKind.Complete,
                "NotificationCompleted",
                request,
                context,
                notificationRepository,
                deviceRepository,
                auditRepository,
                outboxRepository,
                timeProvider,
                cancellationToken))
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        return api;
    }

    private static async Task<IResult> PublishNotification(
        CreateNotificationRequest request,
        HttpContext context,
        INotificationRepository notificationRepository,
        IDeviceRepository deviceRepository,
        IUserAccountRepository userAccountRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanPublishNotifications(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to publish notifications.");
        }

        if (!Enum.TryParse<NotificationType>(request.Type?.Trim(), ignoreCase: true, out var notificationType) ||
            !Enum.IsDefined(notificationType))
        {
            return HiveApiEndpoints.ValidationProblem("Invalid notification Type.");
        }

        if (!Enum.TryParse<NotificationPriority>(request.Priority?.Trim(), ignoreCase: true, out var priority) ||
            !Enum.IsDefined(priority))
        {
            return HiveApiEndpoints.ValidationProblem("Invalid notification Priority.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.SafeSummary))
        {
            return HiveApiEndpoints.ValidationProblem("Title and SafeSummary are required.");
        }

        if (!Enum.TryParse<NotificationTargetType>(request.TargetType?.Trim(), ignoreCase: true, out var targetType) ||
            !Enum.IsDefined(targetType))
        {
            return HiveApiEndpoints.ValidationProblem("TargetType must be User, Device, Site, or AllUsers.");
        }

        var rawTargets = (request.TargetKeys ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToArray();

        var resolvedKeys = new List<string>();
        switch (targetType)
        {
            case NotificationTargetType.User:
                foreach (var target in rawTargets)
                {
                    var key = target.StartsWith("user:", StringComparison.OrdinalIgnoreCase)
                        ? $"user:{target[5..].Trim()}"
                        : $"user:{target}";
                    if (key.Length > 5)
                    {
                        resolvedKeys.Add(key);
                    }
                }
                break;

            case NotificationTargetType.Device:
                foreach (var target in rawTargets)
                {
                    var rawGuid = target.StartsWith("device:", StringComparison.OrdinalIgnoreCase)
                        ? target[7..].Trim()
                        : target;
                    if (!Guid.TryParse(rawGuid, out var deviceGuid))
                    {
                        return HiveApiEndpoints.ValidationProblem($"Invalid device target GUID '{target}'.");
                    }

                    resolvedKeys.Add($"device:{deviceGuid:D}");
                }
                break;

            case NotificationTargetType.Site:
                if (rawTargets.Length == 0)
                {
                    return HiveApiEndpoints.ValidationProblem("At least one site ID is required for Site target.");
                }

                var siteSet = new HashSet<string>(rawTargets, StringComparer.OrdinalIgnoreCase);
                var siteDevices = await deviceRepository.ListAsync(
                    allowedSiteIds: siteSet,
                    allSites: false,
                    cancellationToken: cancellationToken);
                foreach (var device in siteDevices)
                {
                    if (device.EnrollmentState == EnrollmentState.Active)
                    {
                        resolvedKeys.Add($"device:{device.DeviceId:D}");
                    }
                }

                var siteUsers = await userAccountRepository.ListAsync(cancellationToken);
                foreach (var user in siteUsers)
                {
                    if (user.IsActive && (user.AllSitesAccess || user.AllowedSiteIds.Overlaps(siteSet)))
                    {
                        resolvedKeys.Add($"user:{user.SubjectId}");
                    }
                }
                break;

            case NotificationTargetType.AllUsers:
                var allUsers = await userAccountRepository.ListAsync(cancellationToken);
                foreach (var user in allUsers)
                {
                    if (user.IsActive)
                    {
                        resolvedKeys.Add($"user:{user.SubjectId}");
                    }
                }

                foreach (var target in rawTargets)
                {
                    var key = target.StartsWith("user:", StringComparison.OrdinalIgnoreCase)
                        ? $"user:{target[5..].Trim()}"
                        : $"user:{target}";
                    if (key.Length > 5)
                    {
                        resolvedKeys.Add(key);
                    }
                }
                break;
        }

        var distinctKeys = resolvedKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctKeys.Length == 0)
        {
            return HiveApiEndpoints.ValidationProblem("No recipients resolved for the specified notification target.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var entity = new NotificationEntity(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.SourceId) ? "hive-admin" : request.SourceId.Trim(),
            notificationType,
            priority,
            request.Title.Trim(),
            request.SafeSummary.Trim(),
            targetType,
            actor.UserSid,
            nowUtc,
            request.DueAtUtc,
            Revision: 1,
            CancelledAtUtc: null);

        await notificationRepository.CreateWithRecipientsAsync(entity, distinctKeys, cancellationToken);

        await EnqueueNotificationChangedAsync(outboxRepository, entity.NotificationId, entity.Revision, distinctKeys, nowUtc, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "NotificationPublished",
                "Notification",
                entity.NotificationId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    type = entity.Type.ToString(),
                    priority = entity.Priority.ToString(),
                    targetType = entity.TargetType.ToString(),
                    recipientCount = distinctKeys.Length
                })),
            cancellationToken);

        var summary = new AdminNotificationSummaryDto(
            entity.NotificationId,
            entity.SourceId,
            entity.Type.ToString(),
            entity.Priority.ToString(),
            entity.Title,
            entity.SafeSummary,
            entity.TargetType.ToString(),
            entity.CreatedBySid,
            entity.CreatedAtUtc,
            entity.DueAtUtc,
            entity.Revision,
            distinctKeys.Length,
            0,
            0,
            0,
            0,
            distinctKeys.Length,
            false);

        return Results.Created($"{ProtocolConstants.ApiPrefix}/notifications/{entity.NotificationId:D}", summary);
    }

    private static async Task<IResult> ListAdminNotifications(
        HttpContext context,
        INotificationRepository notificationRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanPublishNotifications(actor) && !actor.HasRole(HiveRole.Auditor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to list notifications.");
        }

        var summaries = await notificationRepository.ListAdminSummariesAsync(cancellationToken);
        return Results.Ok(summaries);
    }

    private static async Task<IResult> CancelNotification(
        Guid id,
        CancelNotificationRequest? request,
        HttpContext context,
        INotificationRepository notificationRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existing = await notificationRepository.GetNotificationAsync(id, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Notification not found",
                $"Notification '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanCancelNotification(actor, existing.CreatedBySid))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to cancel this notification.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var cancelled = await notificationRepository.CancelNotificationAsync(id, nowUtc, cancellationToken);
        if (cancelled is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Notification not found",
                $"Notification '{id}' was not found.");
        }

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "notification.changed",
                cancelled.NotificationId,
                cancelled.Revision,
                null,
                null,
                nowUtc),
            cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "NotificationCancelled",
                "Notification",
                cancelled.NotificationId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim()
                })),
            cancellationToken);

        var summaries = await notificationRepository.ListAdminSummariesAsync(cancellationToken);
        var updatedSummary = summaries.FirstOrDefault(s => s.NotificationId == id);
        return Results.Ok(updatedSummary);
    }

    private static async Task<IResult> SyncMyNotifications(
        string? cursor,
        HttpContext context,
        INotificationRepository notificationRepository,
        IDeviceRepository deviceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        var callerKeys = await ResolveCallerRecipientKeysAsync(actor, deviceRepository, cancellationToken);

        long? afterChangeSeq = null;
        if (!string.IsNullOrWhiteSpace(cursor) &&
            long.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            afterChangeSeq = parsed;
        }

        var syncResult = await notificationRepository.SyncForRecipientKeysAsync(
            callerKeys,
            afterChangeSeq,
            cancellationToken);

        int unresolvedActionableCount;
        if (syncResult.SnapshotReset)
        {
            unresolvedActionableCount = syncResult.Items.Count(i => i.Recipient.IsActionableUnresolved());
        }
        else
        {
            var fullSnapshot = await notificationRepository.SyncForRecipientKeysAsync(
                callerKeys,
                afterChangeSeq: null,
                cancellationToken);
            unresolvedActionableCount = fullSnapshot.Items.Count(i => i.Recipient.IsActionableUnresolved());
        }

        var dtos = syncResult.Items
            .Select(i => ToUserNotificationDto(i.Notification, i.Recipient))
            .ToArray();

        return Results.Ok(new NotificationSyncResponse(
            syncResult.LatestChangeSeq.ToString(CultureInfo.InvariantCulture),
            syncResult.SnapshotReset,
            dtos,
            unresolvedActionableCount,
            timeProvider.GetUtcNow()));
    }

    private static async Task<IResult> RecordDelivery(
        Guid id,
        RecordDeliveryRequest request,
        HttpContext context,
        INotificationRepository notificationRepository,
        IDeviceRepository deviceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Outcome))
        {
            return HiveApiEndpoints.ValidationProblem("Outcome is required.");
        }

        var actor = context.User.ToActorIdentity();
        var callerKeys = await ResolveCallerRecipientKeysAsync(actor, deviceRepository, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();

        var delivery = new NotificationDeliveryRecord(
            Guid.NewGuid(),
            id,
            string.Empty,
            request.DeviceId,
            request.SessionId,
            request.Outcome.Trim(),
            nowUtc);

        var recorded = await notificationRepository.RecordDeliveryAsync(id, callerKeys, delivery, cancellationToken);
        if (!recorded)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Notification not found",
                $"Notification '{id}' was not found for the current user.");
        }

        return Results.Ok(new { notificationId = id, recordedAtUtc = nowUtc });
    }

    private static async Task<IResult> ApplyRecipientActionAsync(
        Guid id,
        NotificationActionKind actionKind,
        string auditAction,
        NotificationActionRequest request,
        HttpContext context,
        INotificationRepository notificationRepository,
        IDeviceRepository deviceRepository,
        IAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        var existing = await notificationRepository.GetNotificationAsync(id, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Notification not found",
                $"Notification '{id}' was not found.");
        }

        if (actionKind == NotificationActionKind.Complete &&
            !ScopeAuthorizationService.CanCompleteNotification(actor, existing.Type.ToString()))
        {
            return HiveApiEndpoints.ForbiddenProblem(
                $"Caller is not authorized to complete notifications of type '{existing.Type}'.");
        }

        var callerKeys = await ResolveCallerRecipientKeysAsync(actor, deviceRepository, cancellationToken);
        var idempotencyKey = context.Request.Headers.TryGetValue(ProtocolConstants.IdempotencyKeyHeader, out var values)
            ? values.FirstOrDefault()
            : null;
        var nowUtc = timeProvider.GetUtcNow();

        var result = await notificationRepository.ApplyRecipientActionAsync(
            id,
            callerKeys,
            actionKind,
            request.ExpectedVersion,
            idempotencyKey,
            actor.UserSid,
            nowUtc,
            cancellationToken);

        switch (result.Status)
        {
            case NotificationActionStatus.NotFound:
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status404NotFound,
                    "Notification not found",
                    $"Notification '{id}' was not found for the current user.");

            case NotificationActionStatus.AlreadyCancelled:
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status409Conflict,
                    "Notification cancelled",
                    "This notification has already been cancelled.");

            case NotificationActionStatus.Conflict:
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status409Conflict,
                    "Version conflict",
                    "The notification recipient state was modified or the idempotency key conflicts with another action.");

            case NotificationActionStatus.IdempotentReplay:
                return Results.Ok(ToUserNotificationDto(result.Notification!, result.Recipient!));

            case NotificationActionStatus.Success:
                await auditRepository.AppendAsync(
                    AuditEvent.Create(
                        "User",
                        actor.UserSid,
                        auditAction,
                        "Notification",
                        id.ToString("D"),
                        "Success",
                        null,
                        CorrelationIdMiddleware.GetCorrelationId(context),
                        nowUtc,
                        JsonSerializer.Serialize(new
                        {
                            recipientKey = result.Recipient!.RecipientKey,
                            recipientVersion = result.Recipient.Version,
                            notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
                        })),
                    cancellationToken);

                await outboxRepository.EnqueueAsync(
                    new OutboxEventRecord(
                        Guid.NewGuid(),
                        "notification.changed",
                        id,
                        result.Recipient.Version,
                        null,
                        actor.UserSid,
                        nowUtc),
                    cancellationToken);

                return Results.Ok(ToUserNotificationDto(result.Notification!, result.Recipient!));

            default:
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status500InternalServerError,
                    "Unexpected notification action result",
                    null);
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveCallerRecipientKeysAsync(
        ActorIdentity actor,
        IDeviceRepository deviceRepository,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(actor.UserSid))
        {
            keys.Add($"user:{actor.UserSid.Trim()}");
        }

        var assignedDevices = await deviceRepository.ListAsync(
            assignedUserSid: actor.UserSid,
            allSites: true,
            cancellationToken: cancellationToken);

        foreach (var device in assignedDevices)
        {
            keys.Add($"device:{device.DeviceId:D}");
        }

        return keys;
    }

    private static async Task EnqueueNotificationChangedAsync(
        IOutboxRepository outboxRepository,
        Guid notificationId,
        long revision,
        IReadOnlyList<string> recipientKeys,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var userSids = recipientKeys
            .Where(k => k.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[5..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (userSids.Length == 0)
        {
            await outboxRepository.EnqueueAsync(
                new OutboxEventRecord(
                    Guid.NewGuid(),
                    "notification.changed",
                    notificationId,
                    revision,
                    null,
                    null,
                    nowUtc),
                cancellationToken);
            return;
        }

        foreach (var userSid in userSids)
        {
            await outboxRepository.EnqueueAsync(
                new OutboxEventRecord(
                    Guid.NewGuid(),
                    "notification.changed",
                    notificationId,
                    revision,
                    null,
                    userSid,
                    nowUtc),
                cancellationToken);
        }
    }

    private static UserNotificationDto ToUserNotificationDto(
        NotificationEntity notification,
        NotificationRecipientState recipient) =>
        new(
            notification.NotificationId,
            notification.SourceId,
            notification.Type.ToString(),
            notification.Priority.ToString(),
            notification.Title,
            notification.SafeSummary,
            notification.CreatedAtUtc,
            notification.DueAtUtc,
            notification.Revision,
            recipient.RecipientKey,
            recipient.DeliveredAtUtc,
            recipient.ReadAtUtc,
            recipient.AcknowledgedAtUtc,
            recipient.CompletedAtUtc,
            recipient.CancelledAtUtc ?? notification.CancelledAtUtc,
            recipient.Version,
            recipient.IsActionableUnresolved());
}
