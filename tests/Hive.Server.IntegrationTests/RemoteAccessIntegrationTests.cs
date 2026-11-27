using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Domain.RemoteAccess;
using Hive.Infrastructure.Migrations;
using Hive.Server.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class RemoteAccessIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedRemoteAccessEscrowMigrationAfterNotifications()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0006_RemoteAccessEscrow.sql", StringComparison.Ordinal));
        var notifIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0005_Notifications.sql", StringComparison.Ordinal));
        var escrowIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0006_RemoteAccessEscrow.sql", StringComparison.Ordinal));
        Assert.True(notifIdx >= 0 && escrowIdx > notifIdx);
    }

    [Fact]
    public async Task EscrowLifecycle_AssignedExaminerCanEscrow_StrictCustodyBoundaryOnReveal_AndNoPlaintextInAudit()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supervisorA = factory.CreateUserClient("S-1-5-21-sup-a", "Supervisor", "site-a");
        using var auditorAll = factory.CreateUserClient("S-1-5-21-auditor", "Auditor", "*");
        using var remoteAccessA = factory.CreateUserClient("S-1-5-21-ra-a", "RemoteAccess", "site-a");
        using var adminSiteA = factory.CreateUserClient("S-1-5-21-admin-a", "Administrator", "site-a");
        using var custodianA = factory.CreateUserClient("S-1-5-21-custodian-a", "BreakGlassCustodian", "site-a");
        using var examiner1 = factory.CreateUserClient("S-1-5-21-examiner-1", "Examiner", "site-a");
        using var examiner2 = factory.CreateUserClient("S-1-5-21-examiner-2", "Examiner", "site-a");

        var ws1 = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-ESCROW-01", "THUMB-ESC-01", "ASSET-ESC-01");
        var ws2 = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-ESCROW-02", "THUMB-ESC-02", "ASSET-ESC-02");
        var wsSiteB = await EnrollDeviceAsync(factory, adminAll, "site-b", "WS-ESCROW-B1", "THUMB-ESC-B1", "ASSET-ESC-B1");

        await AssignDeviceAsync(adminAll, ws1.DeviceId, "ASSET-ESC-01", "S-1-5-21-examiner-1", 1);
        await AssignDeviceAsync(adminAll, ws2.DeviceId, "ASSET-ESC-02", "S-1-5-21-examiner-2", 1);

        // Initial metadata shows no escrowed credential
        var initialStatus = await examiner1.GetFromJsonAsync<EscrowCredentialStatusDto>(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.NotNull(initialStatus);
        Assert.False(initialStatus.HasEscrowedCredential);
        Assert.Equal(0, initialStatus.KeyVersion);

        // Validation rejects empty username or password
        var badPutResp = await examiner1.PutAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest("   ", "SomePass123!"));
        Assert.Equal(HttpStatusCode.BadRequest, badPutResp.StatusCode);

        const string secretV1 = "Sup3rS3cret!LabPass#1";
        var putResp = await examiner1.PutAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest(".\\LabExaminer1", secretV1));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var savedStatus = await putResp.Content.ReadFromJsonAsync<EscrowCredentialStatusDto>();
        Assert.NotNull(savedStatus);
        Assert.True(savedStatus.HasEscrowedCredential);
        Assert.Equal(".\\LabExaminer1", savedStatus.AccountUsername);
        Assert.Equal("S-1-5-21-examiner-1", savedStatus.UpdatedBySid);
        Assert.Equal(1, savedStatus.KeyVersion);

        // GET metadata succeeds without leaking password
        var getMetadataResp = await examiner1.GetAsync($"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.OK, getMetadataResp.StatusCode);
        var metadataBody = await getMetadataResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretV1, metadataBody, StringComparison.Ordinal);

        // Examiner 1 cannot GET or PUT another examiner's workstation
        var otherGet = await examiner1.GetAsync($"/api/v1/workstations/{ws2.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.Forbidden, otherGet.StatusCode);
        var otherPut = await examiner1.PutAsJsonAsync(
            $"/api/v1/workstations/{ws2.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest(".\\Other", "Pass123!"));
        Assert.Equal(HttpStatusCode.Forbidden, otherPut.StatusCode);

        // Strict custody boundary on /reveal: Examiner, Supervisor, Auditor, RemoteAccess, and Administrator ALL get 403
        HttpClient[] forbiddenRevealCallers = [examiner1, examiner2, supervisorA, auditorAll, remoteAccessA, adminSiteA, adminAll];
        foreach (var caller in forbiddenRevealCallers)
        {
            var deniedReveal = await caller.PostAsJsonAsync(
                $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential/reveal",
                new RevealEscrowCredentialRequest("Legitimate incident support", EscrowRevealPurposes.PhysicalSignIn));
            Assert.Equal(HttpStatusCode.Forbidden, deniedReveal.StatusCode);
        }

        // BreakGlassCustodian validation: short reason -> 400; invalid purpose -> 400
        var shortReasonResp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("ok", EscrowRevealPurposes.PhysicalSignIn));
        Assert.Equal(HttpStatusCode.BadRequest, shortReasonResp.StatusCode);

        var badPurposeResp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Console recovery for locked account", "InvalidPurpose"));
        Assert.Equal(HttpStatusCode.BadRequest, badPurposeResp.StatusCode);

        // Site isolation: custodian in site-a gets 403 for site-b workstation
        var crossSiteReveal = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{wsSiteB.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Console recovery for site-b", EscrowRevealPurposes.PhysicalSignIn));
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteReveal.StatusCode);

        // BreakGlassCustodian in site-a reveals exact decrypted password with Cache-Control: no-store
        const string revealReason = "Physical console recovery for stuck smartcard reader";
        const string correlationId = "corr-breakglass-001";
        using var revealReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential/reveal")
        {
            Content = JsonContent.Create(new RevealEscrowCredentialRequest(revealReason, EscrowRevealPurposes.PhysicalSignIn))
        };
        revealReq.Headers.Add(ProtocolConstants.CorrelationIdHeader, correlationId);

        using var revealResp = await custodianA.SendAsync(revealReq);
        Assert.Equal(HttpStatusCode.OK, revealResp.StatusCode);
        Assert.True(revealResp.Headers.CacheControl?.NoStore ?? false);
        Assert.Contains("no-cache", revealResp.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);

        var revealed = await revealResp.Content.ReadFromJsonAsync<RevealEscrowCredentialResponse>();
        Assert.NotNull(revealed);
        Assert.Equal(ws1.DeviceId, revealed.DeviceId);
        Assert.Equal("WS-ESCROW-01", revealed.Hostname);
        Assert.Equal(".\\LabExaminer1", revealed.AccountUsername);
        Assert.Equal(secretV1, revealed.Password);
        Assert.Equal(EscrowRevealPurposes.PhysicalSignIn, revealed.Purpose);
        Assert.Equal(correlationId, revealed.AuditCorrelationId);
        Assert.Equal(60, revealed.RecommendedClearSeconds);

        // Re-escrowing updates ciphertext & CredentialId AAD cleanly and reveals new password
        const string secretV2 = "Rot@tedPass!2026#99";
        var rotateResp = await examiner1.PutAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest(".\\LabExaminer1", secretV2));
        Assert.Equal(HttpStatusCode.OK, rotateResp.StatusCode);

        var revealV2Resp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Verify rotated break-glass secret", EscrowRevealPurposes.PhysicalSignIn));
        Assert.Equal(HttpStatusCode.OK, revealV2Resp.StatusCode);
        var revealedV2 = await revealV2Resp.Content.ReadFromJsonAsync<RevealEscrowCredentialResponse>();
        Assert.NotNull(revealedV2);
        Assert.Equal(secretV2, revealedV2.Password);

        // Audit verification: entry exists with reason & correlationId, and NO plaintext password appears anywhere in /api/v1/audit
        var auditHttpResp = await auditorAll.GetAsync("/api/v1/audit?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, auditHttpResp.StatusCode);
        var rawAuditJson = await auditHttpResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretV1, rawAuditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretV2, rawAuditJson, StringComparison.Ordinal);

        var auditPage = await auditHttpResp.Content.ReadFromJsonAsync<PagedResponse<AuditEventDto>>();
        Assert.NotNull(auditPage);
        var revealAudit = Assert.Single(
            auditPage.Items,
            e => e.Action == "WorkstationCredentialRevealed" && e.CorrelationId == correlationId);
        Assert.Equal("Success", revealAudit.Result);
        Assert.Equal("S-1-5-21-custodian-a", revealAudit.ActorId);
        Assert.NotNull(revealAudit.SummaryJson);
        Assert.Contains(revealReason, revealAudit.SummaryJson, StringComparison.Ordinal);

        // Delete authorization & lifecycle
        var forbiddenDelete = await examiner1.DeleteAsync($"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        var supervisorDelete = await supervisorA.DeleteAsync($"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.Forbidden, supervisorDelete.StatusCode);

        var adminDelete = await adminSiteA.DeleteAsync($"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.NoContent, adminDelete.StatusCode);

        var afterDeleteStatus = await examiner1.GetFromJsonAsync<EscrowCredentialStatusDto>(
            $"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.NotNull(afterDeleteStatus);
        Assert.False(afterDeleteStatus.HasEscrowedCredential);

        var secondDelete = await adminSiteA.DeleteAsync($"/api/v1/workstations/{ws1.DeviceId}/escrow-credential");
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
    }

    [Fact]
    public async Task RdpRequestAndProtectedMode_EnforcesPolicyAndAssistedRdpGate()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var remoteAccessA = factory.CreateUserClient("S-1-5-21-ra-a", "RemoteAccess", "site-a");
        using var custodianA = factory.CreateUserClient("S-1-5-21-custodian-a", "BreakGlassCustodian", "site-a");
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner-1", "Examiner", "site-a");

        var ws = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-RDP-01", "THUMB-RDP-01", "ASSET-RDP-01");
        await AssignDeviceAsync(adminAll, ws.DeviceId, "ASSET-RDP-01", "S-1-5-21-examiner-1", 1);

        var putResp = await examiner.PutAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest(".\\ForensicBreakGlass", "RdpVaultSecret#2026"));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // 1. Offline device (no heartbeat yet) -> Allowed = false
        var offlineRdpResp = await remoteAccessA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("Check offline workstation"));
        Assert.Equal(HttpStatusCode.OK, offlineRdpResp.StatusCode);
        var offlineRdp = await offlineRdpResp.Content.ReadFromJsonAsync<RdpAuthorizationResponse>();
        Assert.NotNull(offlineRdp);
        Assert.False(offlineRdp.Allowed);
        Assert.Equal("Offline", offlineRdp.ConnectivityState);
        Assert.False(string.IsNullOrWhiteSpace(offlineRdp.BlockedReason));

        // 2. Online unprotected device with active session -> Allowed = true + session warning
        using var deviceClient = factory.CreateDeviceClient("THUMB-RDP-01");
        var bootId = Guid.NewGuid();
        var hbOnlineResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(
                bootId,
                "1.2.3",
                new[] { "telemetry" },
                new[]
                {
                    new ObservedSessionDto(2, "S-1-5-21-examiner-1", "Active", factory.Clock.GetUtcNow())
                },
                "WS-RDP-01",
                ProtectedModeActive: false,
                ProtectedModeReason: null));
        Assert.Equal(HttpStatusCode.OK, hbOnlineResp.StatusCode);

        // Examiner calling /rdp-request -> 403
        var examinerRdpResp = await examiner.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("Unauthorized RDP request"));
        Assert.Equal(HttpStatusCode.Forbidden, examinerRdpResp.StatusCode);

        // Empty reason -> 400
        var emptyReasonRdpResp = await remoteAccessA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, emptyReasonRdpResp.StatusCode);

        var onlineRdpResp = await remoteAccessA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("Remote assistance for software check", UseEscrowedCredential: true));
        Assert.Equal(HttpStatusCode.OK, onlineRdpResp.StatusCode);
        var onlineRdp = await onlineRdpResp.Content.ReadFromJsonAsync<RdpAuthorizationResponse>();
        Assert.NotNull(onlineRdp);
        Assert.True(onlineRdp.Allowed);
        Assert.Equal("WS-RDP-01", onlineRdp.ApprovedHostname);
        Assert.Equal(3389, onlineRdp.RdpPort);
        Assert.Equal("Online", onlineRdp.ConnectivityState);
        Assert.False(onlineRdp.ProtectedModeActive);
        Assert.True(onlineRdp.HasEscrowedCredential);
        Assert.Single(onlineRdp.ActiveSessions);
        Assert.Contains(onlineRdp.Warnings, w => w.Contains("Active session detected", StringComparison.Ordinal));

        // 3. Heartbeat with ProtectedModeActive = true -> /rdp-request returns Allowed = false AND AssistedRdp reveal returns 409 Conflict
        factory.Clock.Advance(TimeSpan.FromSeconds(15));
        const string protectionReason = "Active forensic imaging job in progress";
        var hbProtectedResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(
                bootId,
                "1.2.3",
                new[] { "telemetry" },
                new[]
                {
                    new ObservedSessionDto(2, "S-1-5-21-examiner-1", "Active", factory.Clock.GetUtcNow())
                },
                "WS-RDP-01",
                ProtectedModeActive: true,
                ProtectedModeReason: protectionReason));
        Assert.Equal(HttpStatusCode.OK, hbProtectedResp.StatusCode);

        var protectedRdpResp = await remoteAccessA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("Emergency maintenance"));
        Assert.Equal(HttpStatusCode.OK, protectedRdpResp.StatusCode);
        var protectedRdp = await protectedRdpResp.Content.ReadFromJsonAsync<RdpAuthorizationResponse>();
        Assert.NotNull(protectedRdp);
        Assert.False(protectedRdp.Allowed);
        Assert.True(protectedRdp.ProtectedModeActive);
        Assert.Contains(protectionReason, protectedRdp.BlockedReason, StringComparison.Ordinal);

        var assistedRevealResp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Attempt assisted RDP during protected work", EscrowRevealPurposes.AssistedRdp));
        Assert.Equal(HttpStatusCode.Conflict, assistedRevealResp.StatusCode);

        // PhysicalSignIn reveal still returns 200 for physical console access
        var physicalRevealResp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Physical console sign-in at lab bench", EscrowRevealPurposes.PhysicalSignIn));
        Assert.Equal(HttpStatusCode.OK, physicalRevealResp.StatusCode);
        var physicalReveal = await physicalRevealResp.Content.ReadFromJsonAsync<RevealEscrowCredentialResponse>();
        Assert.NotNull(physicalReveal);
        Assert.Equal("RdpVaultSecret#2026", physicalReveal.Password);

        // 4. Advance clock so device becomes Offline -> /rdp-request returns Allowed = false
        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        var staleOfflineRdpResp = await remoteAccessA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/rdp-request",
            new RdpRequest("Check after workstation powered off"));
        Assert.Equal(HttpStatusCode.OK, staleOfflineRdpResp.StatusCode);
        var staleOfflineRdp = await staleOfflineRdpResp.Content.ReadFromJsonAsync<RdpAuthorizationResponse>();
        Assert.NotNull(staleOfflineRdp);
        Assert.False(staleOfflineRdp.Allowed);
        Assert.Equal("Offline", staleOfflineRdp.ConnectivityState);
    }

    [Fact]
    public async Task EscrowVault_UnconfiguredMasterKey_Returns503OnPutAndReveal()
    {
        await using var baseFactory = new HiveServerFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.UseSetting("EscrowVault:MasterKey", string.Empty));

        using var adminAll = CreateUserClient(factory, "S-1-5-21-admin", "Administrator", "*");
        using var custodianA = CreateUserClient(factory, "S-1-5-21-custodian-a", "BreakGlassCustodian", "site-a");
        var ws = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-NOVK-01", "THUMB-NOVK-01", null);

        var putResp = await custodianA.PutAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/escrow-credential",
            new UpsertEscrowCredentialRequest(".\\BreakGlass", "SecretPass123!"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, putResp.StatusCode);

        var revealResp = await custodianA.PostAsJsonAsync(
            $"/api/v1/workstations/{ws.DeviceId}/escrow-credential/reveal",
            new RevealEscrowCredentialRequest("Attempt reveal without master key", EscrowRevealPurposes.PhysicalSignIn));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, revealResp.StatusCode);
    }

    private static HttpClient CreateUserClient(
        WebApplicationFactory<Program> factory,
        string userSid,
        string roles,
        string sites)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.UserSidHeader, userSid);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.UserNameHeader, userSid);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.RolesHeader, roles);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.SitesHeader, sites);
        return client;
    }

    private static async Task<EnrollDeviceResponse> EnrollDeviceAsync(
        WebApplicationFactory<Program> factory,
        HttpClient adminClient,
        string siteId,
        string hostname,
        string thumbprint,
        string? assetNumber)
    {
        var tokenResp = await adminClient.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest(siteId, 30));
        tokenResp.EnsureSuccessStatusCode();
        var token = await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>();
        Assert.NotNull(token);

        using var anon = factory.CreateClient();
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(token.Token, hostname, thumbprint, assetNumber, "1.2.3"));
        enrollResp.EnsureSuccessStatusCode();
        var enrolled = await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        Assert.NotNull(enrolled);
        return enrolled;
    }

    private static async Task AssignDeviceAsync(
        HttpClient adminClient,
        Guid deviceId,
        string? assetNumber,
        string? assignedUserSid,
        long expectedRowVersion)
    {
        var resp = await adminClient.PatchAsJsonAsync(
            $"/api/v1/workstations/{deviceId}/assignment",
            new UpdateAssignmentRequest(assetNumber, assignedUserSid, expectedRowVersion));
        resp.EnsureSuccessStatusCode();
    }
}
