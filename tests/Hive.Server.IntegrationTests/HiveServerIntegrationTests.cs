using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Server.Authentication;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class HiveServerIntegrationTests
{
    [Fact]
    public async Task Health_IsAnonymous_AndEchoesCorrelationId()
    {
        await using var factory = new HiveServerFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(ProtocolConstants.CorrelationIdHeader, "corr-health-123");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ProtocolConstants.CorrelationIdHeader, out var values));
        Assert.Contains("corr-health-123", values);
    }

    [Theory]
    [InlineData("GET", "/api/v1/me")]
    [InlineData("GET", "/api/v1/workstations")]
    [InlineData("POST", "/api/v1/agent/heartbeat")]
    [InlineData("GET", "/api/v1/audit")]
    public async Task UnauthenticatedRequests_Return401(string method, string path)
    {
        await using var factory = new HiveServerFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new HeartbeatRequest(
                Guid.NewGuid(),
                "1.0.0",
                Array.Empty<string>(),
                Array.Empty<ObservedSessionDto>(),
                "HOST-1"));
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Examiner_CallingRestrictedEndpoints_Returns403()
    {
        await using var factory = new HiveServerFactory();
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner-1", "Examiner", "site-a");

        var listWorkstations = await examiner.GetAsync("/api/v1/workstations");
        Assert.Equal(HttpStatusCode.Forbidden, listWorkstations.StatusCode);

        var createToken = await examiner.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest("site-a", 15));
        Assert.Equal(HttpStatusCode.Forbidden, createToken.StatusCode);

        var listAudit = await examiner.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, listAudit.StatusCode);
    }

    [Fact]
    public async Task CrossSiteIsolation_SupervisorInSiteA_CannotViewOrAssignWorkstationInSiteB()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supSiteA = factory.CreateUserClient("S-1-5-21-sup-a", "Supervisor", "site-a");

        var enrolledB = await EnrollDeviceAsync(factory, adminAll, "site-b", "WS-SITE-B-01", "THUMB-SITEB-01", "ASSET-B-01");

        var listA = await supSiteA.GetFromJsonAsync<List<DeviceSummaryDto>>("/api/v1/workstations");
        Assert.NotNull(listA);
        Assert.DoesNotContain(listA, d => d.DeviceId == enrolledB.DeviceId);

        var filterSiteB = await supSiteA.GetAsync("/api/v1/workstations?siteId=site-b");
        Assert.Equal(HttpStatusCode.Forbidden, filterSiteB.StatusCode);

        var getDetail = await supSiteA.GetAsync($"/api/v1/workstations/{enrolledB.DeviceId}");
        Assert.Equal(HttpStatusCode.Forbidden, getDetail.StatusCode);

        var patchAssign = await supSiteA.PatchAsJsonAsync(
            $"/api/v1/workstations/{enrolledB.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-B-NEW", "S-1-5-21-user-b", 1));
        Assert.Equal(HttpStatusCode.Forbidden, patchAssign.StatusCode);
    }

    [Fact]
    public async Task EnrollmentLifecycle_TokenSingleUse_Expiration_Heartbeat_AndOnlineStatus()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        using var anon = factory.CreateClient();

        // 1. Admin creates token
        var tokenResp = await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("site-a", 15));
        Assert.Equal(HttpStatusCode.Created, tokenResp.StatusCode);
        var tokenPayload = await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>();
        Assert.NotNull(tokenPayload);
        Assert.False(string.IsNullOrWhiteSpace(tokenPayload.Token));

        // 2. Device enrolls with token
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(tokenPayload.Token, "WS-A-01", "AA:BB:CC:01", "ASSET-100", "1.2.3"));
        Assert.Equal(HttpStatusCode.Created, enrollResp.StatusCode);
        var enrolled = await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        Assert.NotNull(enrolled);
        Assert.Equal("site-a", enrolled.SiteId);

        // 3. Token reuse fails (401/422)
        var reuseResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(tokenPayload.Token, "WS-A-02", "AA:BB:CC:02", "ASSET-101", "1.2.3"));
        Assert.True(reuseResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity);

        // 4. Expired token rejected
        var expiringTokenResp = await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("site-a", 5));
        Assert.Equal(HttpStatusCode.Created, expiringTokenResp.StatusCode);
        var expiringToken = await expiringTokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>();
        Assert.NotNull(expiringToken);

        factory.Clock.Advance(TimeSpan.FromMinutes(6));

        var expiredEnrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(expiringToken.Token, "WS-A-03", "AA:BB:CC:03", null, "1.2.3"));
        Assert.True(expiredEnrollResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity);

        // 5. Device sends heartbeat via enrolled cert thumbprint
        using var deviceClient = factory.CreateDeviceClient("AA:BB:CC:01");
        var bootId = Guid.NewGuid();
        var hbResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(
                bootId,
                "1.2.3",
                new[] { "telemetry", "software-install" },
                new[]
                {
                    new ObservedSessionDto(1, "S-1-5-21-examiner-1", "Active", factory.Clock.GetUtcNow())
                },
                "WS-A-01"));
        Assert.Equal(HttpStatusCode.OK, hbResp.StatusCode);
        var hbPayload = await hbResp.Content.ReadFromJsonAsync<HeartbeatResponse>();
        Assert.NotNull(hbPayload);
        Assert.Equal(1, hbPayload.PolicyRevision);

        // 6. User token cannot call agent heartbeat
        var userHbResp = await admin.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(bootId, "1.2.3", Array.Empty<string>(), Array.Empty<ObservedSessionDto>(), "WS-A-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, userHbResp.StatusCode);

        // 7. GET /api/v1/workstations shows Online
        var list = await admin.GetFromJsonAsync<List<DeviceSummaryDto>>("/api/v1/workstations");
        Assert.NotNull(list);
        var ws = Assert.Single(list, d => d.DeviceId == enrolled.DeviceId);
        Assert.Equal("Online", ws.ConnectivityState);
        Assert.Equal("Active", ws.EnrollmentState);
    }

    [Fact]
    public async Task SpoofingXDeviceId_WithoutValidEnrolledCert_Returns401()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        var enrolled = await EnrollDeviceAsync(factory, admin, "site-a", "WS-SPOOF-01", "THUMB-REAL-01", null);

        using var spoofClient = factory.CreateClient();
        spoofClient.DefaultRequestHeaders.Add("X-Device-Id", enrolled.DeviceId.ToString("D"));

        var resp = await spoofClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(Guid.NewGuid(), "1.0.0", Array.Empty<string>(), Array.Empty<ObservedSessionDto>(), "WS-SPOOF-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        using var wrongThumbClient = factory.CreateDeviceClient("THUMB-UNKNOWN");
        wrongThumbClient.DefaultRequestHeaders.Add("X-Device-Id", enrolled.DeviceId.ToString("D"));

        var resp2 = await wrongThumbClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(Guid.NewGuid(), "1.0.0", Array.Empty<string>(), Array.Empty<ObservedSessionDto>(), "WS-SPOOF-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);
    }

    [Fact]
    public async Task AssetAssignment_EnforcesUniqueAsset_RowVersionConcurrency_AndPreservesHistory()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup", "Supervisor", "site-a");

        var dev1 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-1", "THUMB-1", "ASSET-001");
        var dev2 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-2", "THUMB-2", null);

        // Duplicate asset number on second device returns 409
        var dupResp = await supervisor.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev2.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-001", "S-1-5-21-ex-2", ExpectedRowVersion: 1));
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        // Assign dev1 to examiner 1
        var assign1Resp = await supervisor.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev1.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-001", "S-1-5-21-ex-1", ExpectedRowVersion: 1));
        Assert.Equal(HttpStatusCode.OK, assign1Resp.StatusCode);
        var detail1 = await assign1Resp.Content.ReadFromJsonAsync<DeviceDetailDto>();
        Assert.NotNull(detail1);
        Assert.Equal(2, detail1.RowVersion);
        Assert.Equal("S-1-5-21-ex-1", detail1.AssignedUserSid);

        // Stale ExpectedRowVersion (1 instead of 2) returns 409
        var staleResp = await supervisor.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev1.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-001", "S-1-5-21-ex-3", ExpectedRowVersion: 1));
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

        // Reassign dev1 to examiner 2 with current RowVersion=2 -> history preserved
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var assign2Resp = await supervisor.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev1.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-001-UPDATED", "S-1-5-21-ex-2", ExpectedRowVersion: 2));
        Assert.Equal(HttpStatusCode.OK, assign2Resp.StatusCode);
        var detail2 = await assign2Resp.Content.ReadFromJsonAsync<DeviceDetailDto>();
        Assert.NotNull(detail2);
        Assert.Equal(3, detail2.RowVersion);
        Assert.Equal("ASSET-001-UPDATED", detail2.AssetNumber);
        Assert.Equal("S-1-5-21-ex-2", detail2.AssignedUserSid);
        Assert.Equal(2, detail2.AssignmentHistory.Count);
        Assert.Contains(detail2.AssignmentHistory, h => h.UserSid == "S-1-5-21-ex-1" && h.ValidToUtc is not null);
        Assert.Contains(detail2.AssignmentHistory, h => h.UserSid == "S-1-5-21-ex-2" && h.ValidToUtc is null);

        // Examiner 2 can see dev1 via /me/workstations and /workstations/{id}, while Examiner 1 gets 403
        using var ex2Client = factory.CreateUserClient("S-1-5-21-ex-2", "Examiner", "site-a");
        using var ex1Client = factory.CreateUserClient("S-1-5-21-ex-1", "Examiner", "site-a");

        var myList = await ex2Client.GetFromJsonAsync<List<DeviceSummaryDto>>("/api/v1/me/workstations");
        Assert.NotNull(myList);
        Assert.Single(myList, d => d.DeviceId == dev1.DeviceId);

        var ex2Detail = await ex2Client.GetAsync($"/api/v1/workstations/{dev1.DeviceId}");
        Assert.Equal(HttpStatusCode.OK, ex2Detail.StatusCode);

        var ex1Detail = await ex1Client.GetAsync($"/api/v1/workstations/{dev1.DeviceId}");
        Assert.Equal(HttpStatusCode.Forbidden, ex1Detail.StatusCode);
    }

    [Fact]
    public async Task DeviceRevocation_CausesSubsequentHeartbeatToFailClosed()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");

        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-REVOKE-01", "THUMB-REV-01", null);
        using var deviceClient = factory.CreateDeviceClient("THUMB-REV-01");

        // Heartbeat works before revocation
        var beforeRevoke = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(Guid.NewGuid(), "1.0.0", Array.Empty<string>(), Array.Empty<ObservedSessionDto>(), "WS-REVOKE-01"));
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        // Admin revokes device
        var revokeResp = await admin.PostAsJsonAsync(
            $"/api/v1/workstations/{dev.DeviceId}/revoke",
            new RevokeDeviceRequest("Decommissioned hardware"));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
        var revokedSummary = await revokeResp.Content.ReadFromJsonAsync<DeviceSummaryDto>();
        Assert.NotNull(revokedSummary);
        Assert.Equal("Revoked", revokedSummary.EnrollmentState);

        // Subsequent heartbeat fails closed (401/403)
        var afterRevoke = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(Guid.NewGuid(), "1.0.0", Array.Empty<string>(), Array.Empty<ObservedSessionDto>(), "WS-REVOKE-01"));
        Assert.True(afterRevoke.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuditLog_RecordsLifecycleEventsWithCorrelationId_AndBlocksNonAuditors()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        using var auditor = factory.CreateUserClient("S-1-5-21-auditor", "Auditor", "site-a");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup", "Supervisor", "site-a");

        // Supervisor is not Auditor/Administrator -> 403
        var supAudit = await supervisor.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, supAudit.StatusCode);

        // Perform enrollment, assignment, revocation with explicit correlation IDs
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrollment-tokens")
        {
            Content = JsonContent.Create(new CreateEnrollmentTokenRequest("site-a", 15))
        };
        tokenReq.Headers.Add(ProtocolConstants.CorrelationIdHeader, "corr-token-1");
        var tokenResp = await admin.SendAsync(tokenReq);
        Assert.Equal(HttpStatusCode.Created, tokenResp.StatusCode);
        var token = (await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>())!;

        using var anon = factory.CreateClient();
        var enrollReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/enroll")
        {
            Content = JsonContent.Create(new EnrollDeviceRequest(token.Token, "WS-AUD-01", "THUMB-AUD-01", null, "1.0.0"))
        };
        enrollReq.Headers.Add(ProtocolConstants.CorrelationIdHeader, "corr-enroll-1");
        var enrollResp = await anon.SendAsync(enrollReq);
        Assert.Equal(HttpStatusCode.Created, enrollResp.StatusCode);
        var dev = (await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>())!;

        var assignReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/workstations/{dev.DeviceId}/assignment")
        {
            Content = JsonContent.Create(new UpdateAssignmentRequest("ASSET-AUD-1", "S-1-5-21-ex-9", 1))
        };
        assignReq.Headers.Add(ProtocolConstants.CorrelationIdHeader, "corr-assign-1");
        var assignResp = await supervisor.SendAsync(assignReq);
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);

        var revokeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workstations/{dev.DeviceId}/revoke")
        {
            Content = JsonContent.Create(new RevokeDeviceRequest("Audit test revoke"))
        };
        revokeReq.Headers.Add(ProtocolConstants.CorrelationIdHeader, "corr-revoke-1");
        var revokeResp = await admin.SendAsync(revokeReq);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // Auditor queries audit log
        var auditPage = await auditor.GetFromJsonAsync<PagedResponse<AuditEventDto>>("/api/v1/audit?page=1&pageSize=20");
        Assert.NotNull(auditPage);
        Assert.True(auditPage.TotalCount >= 4);

        var enrollEvent = Assert.Single(auditPage.Items, e => e.Action == "DeviceEnrolled" && e.ObjectId == dev.DeviceId.ToString("D"));
        Assert.Equal("corr-enroll-1", enrollEvent.CorrelationId);
        Assert.Equal(dev.DeviceId.ToString("D"), enrollEvent.ActorId);

        var assignEvent = Assert.Single(auditPage.Items, e => e.Action == "WorkstationAssignmentUpdated" && e.ObjectId == dev.DeviceId.ToString("D"));
        Assert.Equal("corr-assign-1", assignEvent.CorrelationId);
        Assert.Equal("S-1-5-21-sup", assignEvent.ActorId);

        var revokeEvent = Assert.Single(auditPage.Items, e => e.Action == "DeviceRevoked" && e.ObjectId == dev.DeviceId.ToString("D"));
        Assert.Equal("corr-revoke-1", revokeEvent.CorrelationId);
        Assert.Equal("S-1-5-21-admin", revokeEvent.ActorId);
    }

    private static async Task<EnrollDeviceResponse> EnrollDeviceAsync(
        HiveServerFactory factory,
        HttpClient adminClient,
        string siteId,
        string hostname,
        string thumbprint,
        string? assetNumber)
    {
        var tokenResp = await adminClient.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest(siteId, 15));
        tokenResp.EnsureSuccessStatusCode();
        var token = (await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>())!;

        using var anon = factory.CreateClient();
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(token.Token, hostname, thumbprint, assetNumber, "1.0.0"));
        enrollResp.EnsureSuccessStatusCode();
        return (await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>())!;
    }
}
