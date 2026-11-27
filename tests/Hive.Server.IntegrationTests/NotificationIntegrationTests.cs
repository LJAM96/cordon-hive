using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Infrastructure.Migrations;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class NotificationIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedNotificationsMigrationAfterSoftwareCompliance()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0005_Notifications.sql", StringComparison.Ordinal));
        var softwareIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0004_SoftwareCompliance.sql", StringComparison.Ordinal));
        var notifIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0005_Notifications.sql", StringComparison.Ordinal));
        Assert.True(softwareIdx >= 0 && notifIdx > softwareIdx);
    }

    [Fact]
    public async Task Notifications_PublishIsolationAckSemanticsAndSoftwareUpdateCompleteAuthorization()
    {
        await using var factory = new HiveServerFactory();
        using var supervisor = factory.CreateUserClient("S-1-5-21-supervisor", "Supervisor", "*");
        using var userA = factory.CreateUserClient("S-1-5-21-user-a", "Examiner", "site-a");
        using var userB = factory.CreateUserClient("S-1-5-21-user-b", "Examiner", "site-a");
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "site-a");

        // Supervisor publishes Announcement and SoftwareUpdate to User A (and SoftwareUpdate also to swAdmin).
        var announcementResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "Announcement",
                "Normal",
                "Scheduled Maintenance Window",
                "Lab server reboot scheduled for 22:00 UTC.",
                "User",
                new[] { "S-1-5-21-user-a" }));
        Assert.Equal(HttpStatusCode.Created, announcementResp.StatusCode);
        var announcement = await announcementResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>();
        Assert.NotNull(announcement);
        Assert.Equal(1, announcement.TotalRecipients);
        Assert.Equal(1, announcement.UnresolvedCount);

        var updateResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "SoftwareUpdate",
                "High",
                "AXIOM 8.5 Update Required",
                "Update Magnet AXIOM to approved release 8.5.0.",
                "User",
                new[] { "S-1-5-21-user-a", "S-1-5-21-sw-admin" }));
        Assert.Equal(HttpStatusCode.Created, updateResp.StatusCode);
        var update = await updateResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>();
        Assert.NotNull(update);
        Assert.Equal(2, update.TotalRecipients);

        // User A sees both via /api/v1/me/notifications.
        var syncA = await userA.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncA);
        Assert.True(syncA.SnapshotReset);
        Assert.Equal(2, syncA.Items.Count);
        Assert.Equal(2, syncA.UnresolvedActionableCount);

        var dtoAnnouncement = Assert.Single(syncA.Items, i => i.NotificationId == announcement.NotificationId);
        var dtoUpdate = Assert.Single(syncA.Items, i => i.NotificationId == update.NotificationId);
        Assert.True(dtoAnnouncement.IsActionableUnresolved);
        Assert.True(dtoUpdate.IsActionableUnresolved);

        // User B sees 0 items and gets 404 trying to ack User A's notification (V12).
        var syncB = await userB.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncB);
        Assert.Empty(syncB.Items);
        Assert.Equal(0, syncB.UnresolvedActionableCount);

        var userBAckResp = await userB.PostAsJsonAsync(
            $"/api/v1/me/notifications/{announcement.NotificationId}/ack",
            new NotificationActionRequest(dtoAnnouncement.RecipientVersion));
        Assert.Equal(HttpStatusCode.NotFound, userBAckResp.StatusCode);

        // Acknowledging Announcement drops UnresolvedActionableCount from 2 to 1.
        var ackAnnouncementResp = await userA.PostAsJsonAsync(
            $"/api/v1/me/notifications/{announcement.NotificationId}/ack",
            new NotificationActionRequest(dtoAnnouncement.RecipientVersion));
        Assert.Equal(HttpStatusCode.OK, ackAnnouncementResp.StatusCode);
        var ackedAnnouncement = await ackAnnouncementResp.Content.ReadFromJsonAsync<UserNotificationDto>();
        Assert.NotNull(ackedAnnouncement);
        Assert.NotNull(ackedAnnouncement.AcknowledgedAtUtc);
        Assert.False(ackedAnnouncement.IsActionableUnresolved);

        var syncAfterAckAnn = await userA.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncAfterAckAnn);
        Assert.Equal(1, syncAfterAckAnn.UnresolvedActionableCount);

        // Acknowledging SoftwareUpdate keeps IsActionableUnresolved == true (V10).
        var ackUpdateResp = await userA.PostAsJsonAsync(
            $"/api/v1/me/notifications/{update.NotificationId}/ack",
            new NotificationActionRequest(dtoUpdate.RecipientVersion));
        Assert.Equal(HttpStatusCode.OK, ackUpdateResp.StatusCode);
        var ackedUpdate = await ackUpdateResp.Content.ReadFromJsonAsync<UserNotificationDto>();
        Assert.NotNull(ackedUpdate);
        Assert.NotNull(ackedUpdate.AcknowledgedAtUtc);
        Assert.Null(ackedUpdate.CompletedAtUtc);
        Assert.True(ackedUpdate.IsActionableUnresolved);

        var syncAfterAckUpd = await userA.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncAfterAckUpd);
        Assert.Equal(1, syncAfterAckUpd.UnresolvedActionableCount);

        // Regular user calling /complete on SoftwareUpdate gets 403 Forbidden.
        var userACompleteResp = await userA.PostAsJsonAsync(
            $"/api/v1/me/notifications/{update.NotificationId}/complete",
            new NotificationActionRequest(ackedUpdate.RecipientVersion));
        Assert.Equal(HttpStatusCode.Forbidden, userACompleteResp.StatusCode);

        // SoftwareAdmin can complete SoftwareUpdate (200 OK).
        var syncSwAdmin = await swAdmin.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncSwAdmin);
        var swAdminDto = Assert.Single(syncSwAdmin.Items, i => i.NotificationId == update.NotificationId);

        var swAdminCompleteResp = await swAdmin.PostAsJsonAsync(
            $"/api/v1/me/notifications/{update.NotificationId}/complete",
            new NotificationActionRequest(swAdminDto.RecipientVersion, "Installed and verified"));
        Assert.Equal(HttpStatusCode.OK, swAdminCompleteResp.StatusCode);
        var completedUpdate = await swAdminCompleteResp.Content.ReadFromJsonAsync<UserNotificationDto>();
        Assert.NotNull(completedUpdate);
        Assert.NotNull(completedUpdate.CompletedAtUtc);
        Assert.False(completedUpdate.IsActionableUnresolved);

        var syncSwAdminAfter = await swAdmin.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncSwAdminAfter);
        Assert.Equal(0, syncSwAdminAfter.UnresolvedActionableCount);
    }

    [Fact]
    public async Task Notifications_CursorDeltaSync_AndInvalidCursorTriggersSnapshotReset()
    {
        await using var factory = new HiveServerFactory();
        using var supervisor = factory.CreateUserClient("S-1-5-21-supervisor", "Supervisor", "*");
        using var userA = factory.CreateUserClient("S-1-5-21-user-a", "Examiner", "site-a");

        var firstResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "Informational",
                "Low",
                "First Notice",
                "Initial notice",
                "User",
                new[] { "S-1-5-21-user-a" }));
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var first = (await firstResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>())!;

        var secondResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "Announcement",
                "Normal",
                "Second Notice",
                "Actionable announcement",
                "User",
                new[] { "S-1-5-21-user-a" }));
        Assert.Equal(HttpStatusCode.Created, secondResp.StatusCode);
        var second = (await secondResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>())!;

        // Initial full snapshot.
        var initialSync = await userA.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(initialSync);
        Assert.True(initialSync.SnapshotReset);
        Assert.Equal(2, initialSync.Items.Count);
        Assert.Equal(2, initialSync.UnresolvedActionableCount);

        // Read only the first notification -> changes its change_seq.
        var firstDto = Assert.Single(initialSync.Items, i => i.NotificationId == first.NotificationId);
        var readResp = await userA.PostAsJsonAsync(
            $"/api/v1/me/notifications/{first.NotificationId}/read",
            new NotificationActionRequest(firstDto.RecipientVersion));
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        // Delta sync after initial cursor returns ONLY the modified first notification,
        // while UnresolvedActionableCount still reflects full active state (1 remaining: second).
        var deltaSync = await userA.GetFromJsonAsync<NotificationSyncResponse>(
            $"/api/v1/me/notifications?cursor={Uri.EscapeDataString(initialSync.Cursor)}");
        Assert.NotNull(deltaSync);
        Assert.False(deltaSync.SnapshotReset);
        var deltaItem = Assert.Single(deltaSync.Items);
        Assert.Equal(first.NotificationId, deltaItem.NotificationId);
        Assert.NotNull(deltaItem.ReadAtUtc);
        Assert.Equal(1, deltaSync.UnresolvedActionableCount);

        // Delta sync with latest cursor and no new changes returns empty items.
        var idleDelta = await userA.GetFromJsonAsync<NotificationSyncResponse>(
            $"/api/v1/me/notifications?cursor={Uri.EscapeDataString(deltaSync.Cursor)}");
        Assert.NotNull(idleDelta);
        Assert.False(idleDelta.SnapshotReset);
        Assert.Empty(idleDelta.Items);
        Assert.Equal(1, idleDelta.UnresolvedActionableCount);

        // Invalid/expired cursors trigger SnapshotReset == true full snapshot (V11).
        foreach (var badCursor in new[] { "not-a-number", "-5", "9999999" })
        {
            var resetSync = await userA.GetFromJsonAsync<NotificationSyncResponse>(
                $"/api/v1/me/notifications?cursor={Uri.EscapeDataString(badCursor)}");
            Assert.NotNull(resetSync);
            Assert.True(resetSync.SnapshotReset);
            Assert.Equal(2, resetSync.Items.Count);
            Assert.Contains(resetSync.Items, i => i.NotificationId == first.NotificationId);
            Assert.Contains(resetSync.Items, i => i.NotificationId == second.NotificationId);
        }
    }

    [Fact]
    public async Task Notifications_IdempotencyKeyReplayAndStaleExpectedVersionConflict()
    {
        await using var factory = new HiveServerFactory();
        using var supervisor = factory.CreateUserClient("S-1-5-21-supervisor", "Supervisor", "*");
        using var userA = factory.CreateUserClient("S-1-5-21-user-a", "Examiner", "site-a");

        var createResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "Announcement",
                "Normal",
                "Policy Update",
                "Acknowledge new evidence handling policy.",
                "User",
                new[] { "S-1-5-21-user-a" }));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = (await createResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>())!;

        var sync = await userA.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        var initialDto = Assert.Single(sync!.Items);
        Assert.Equal(1, initialDto.RecipientVersion);

        // First /ack with Idempotency-Key succeeds and advances RecipientVersion to 2.
        using var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/notifications/{created.NotificationId}/ack");
        req1.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, "ack-key-001");
        req1.Content = JsonContent.Create(new NotificationActionRequest(initialDto.RecipientVersion));
        using var resp1 = await userA.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var acked1 = await resp1.Content.ReadFromJsonAsync<UserNotificationDto>();
        Assert.NotNull(acked1);
        Assert.Equal(2, acked1.RecipientVersion);

        // Replaying the same Idempotency-Key (even with original ExpectedVersion=1) is idempotent and returns same RecipientVersion.
        using var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/notifications/{created.NotificationId}/ack");
        req2.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, "ack-key-001");
        req2.Content = JsonContent.Create(new NotificationActionRequest(initialDto.RecipientVersion));
        using var resp2 = await userA.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var acked2 = await resp2.Content.ReadFromJsonAsync<UserNotificationDto>();
        Assert.NotNull(acked2);
        Assert.Equal(acked1.RecipientVersion, acked2.RecipientVersion);
        Assert.Equal(acked1.AcknowledgedAtUtc, acked2.AcknowledgedAtUtc);

        // Stale ExpectedVersion without a matching idempotency key returns 409 Conflict.
        using var staleReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/notifications/{created.NotificationId}/ack");
        staleReq.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, "ack-key-002");
        staleReq.Content = JsonContent.Create(new NotificationActionRequest(initialDto.RecipientVersion));
        using var staleResp = await userA.SendAsync(staleReq);
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

        // Stale ExpectedVersion with no idempotency key also returns 409 Conflict.
        var staleNoKeyResp = await userA.PostAsJsonAsync(
            $"/api/v1/me/notifications/{created.NotificationId}/ack",
            new NotificationActionRequest(initialDto.RecipientVersion));
        Assert.Equal(HttpStatusCode.Conflict, staleNoKeyResp.StatusCode);
    }

    [Fact]
    public async Task Notifications_CancelRemovesFromUnresolvedCount_AndExaminerDeniedPublish()
    {
        await using var factory = new HiveServerFactory();
        using var supervisor = factory.CreateUserClient("S-1-5-21-supervisor", "Supervisor", "*");
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner", "Examiner", "site-a");
        using var auditor = factory.CreateUserClient("S-1-5-21-auditor", "Auditor", "*");

        // Examiner denied 403 on POST /api/v1/notifications and GET /api/v1/notifications.
        var examinerPublishResp = await examiner.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "Announcement",
                "Normal",
                "Unauthorized",
                "Should fail",
                "User",
                new[] { "S-1-5-21-examiner" }));
        Assert.Equal(HttpStatusCode.Forbidden, examinerPublishResp.StatusCode);

        var examinerListResp = await examiner.GetAsync("/api/v1/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, examinerListResp.StatusCode);

        // Supervisor publishes a RequiredAction notification to examiner.
        var publishResp = await supervisor.PostAsJsonAsync(
            "/api/v1/notifications",
            new CreateNotificationRequest(
                "RequiredAction",
                "Critical",
                "Verify Case Export",
                "Confirm checksum on case export workstation.",
                "User",
                new[] { "S-1-5-21-examiner" }));
        Assert.Equal(HttpStatusCode.Created, publishResp.StatusCode);
        var created = (await publishResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>())!;

        // Record delivery for examiner.
        var deliveryResp = await examiner.PostAsJsonAsync(
            $"/api/v1/me/notifications/{created.NotificationId}/delivery",
            new RecordDeliveryRequest(null, null, "ToastDisplayed"));
        Assert.Equal(HttpStatusCode.OK, deliveryResp.StatusCode);

        var syncBeforeCancel = await examiner.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncBeforeCancel);
        Assert.Equal(1, syncBeforeCancel.UnresolvedActionableCount);
        Assert.NotNull(Assert.Single(syncBeforeCancel.Items).DeliveredAtUtc);

        // Supervisor cancels the notification.
        var cancelResp = await supervisor.PostAsJsonAsync(
            $"/api/v1/notifications/{created.NotificationId}/cancel",
            new CancelNotificationRequest("Superseded by new bulletin"));
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var cancelledSummary = await cancelResp.Content.ReadFromJsonAsync<AdminNotificationSummaryDto>();
        Assert.NotNull(cancelledSummary);
        Assert.True(cancelledSummary.IsCancelled);
        Assert.Equal(0, cancelledSummary.UnresolvedCount);

        // Cancellation removes it from UnresolvedActionableCount for the recipient.
        var syncAfterCancel = await examiner.GetFromJsonAsync<NotificationSyncResponse>("/api/v1/me/notifications");
        Assert.NotNull(syncAfterCancel);
        Assert.Equal(0, syncAfterCancel.UnresolvedActionableCount);
        var cancelledItem = Assert.Single(syncAfterCancel.Items);
        Assert.NotNull(cancelledItem.CancelledAtUtc);
        Assert.False(cancelledItem.IsActionableUnresolved);

        // Auditor can view admin summaries via GET /api/v1/notifications.
        var adminList = await auditor.GetFromJsonAsync<List<AdminNotificationSummaryDto>>("/api/v1/notifications");
        Assert.NotNull(adminList);
        var summary = Assert.Single(adminList);
        Assert.Equal(created.NotificationId, summary.NotificationId);
        Assert.Equal(1, summary.DeliveredCount);
        Assert.True(summary.IsCancelled);
        Assert.Equal(0, summary.UnresolvedCount);
    }
}
