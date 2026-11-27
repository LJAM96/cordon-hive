using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Enrollment;
using Hive.Infrastructure.Migrations;
using Hive.Infrastructure.Repositories;
using Npgsql;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class HardeningAndRecoveryIntegrationTests
{
    [Fact]
    public async Task EnrollmentPoP_RsaAndEcdsaPass_AndFailuresReturn401WithAudit()
    {
        await using var factory = new HiveServerFactory { RequireEnrollmentProofOfPossession = true };
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var auditor = factory.CreateUserClient("S-1-5-21-auditor", "Auditor", "*");
        using var anon = factory.CreateClient();

        var now = factory.Clock.GetUtcNow();
        var rsaToken = await CreateEnrollmentTokenAsync(admin, "site-a");
        var ecdsaToken = await CreateEnrollmentTokenAsync(admin, "site-a");
        var failToken = await CreateEnrollmentTokenAsync(admin, "site-a");

        // 1. Valid RSA certificate passes
        using (var rsaCert = CreateSelfSignedRsaCert("CN=WS-POP-RSA", now.AddDays(-1), now.AddDays(30)))
        {
            var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(rsaToken.Token);
            var sig = EnrollmentProofOfPossession.SignChallenge(rsaCert, tokenHash, "WS-POP-RSA");
            var pem = rsaCert.ExportCertificatePem();

            var resp = await anon.PostAsJsonAsync(
                "/api/v1/devices/enroll",
                new EnrollDeviceRequest(rsaToken.Token, "WS-POP-RSA", rsaCert.Thumbprint, "ASSET-POP-RSA", "1.2.3", pem, sig));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // 2. Valid ECDSA certificate passes
        using (var ecdsaCert = CreateSelfSignedEcdsaCert("CN=WS-POP-ECDSA", now.AddDays(-1), now.AddDays(30)))
        {
            var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(ecdsaToken.Token);
            var sig = EnrollmentProofOfPossession.SignChallenge(ecdsaCert, tokenHash, "WS-POP-ECDSA");
            var pem = ecdsaCert.ExportCertificatePem();

            var resp = await anon.PostAsJsonAsync(
                "/api/v1/devices/enroll",
                new EnrollDeviceRequest(ecdsaToken.Token, "WS-POP-ECDSA", ecdsaCert.Thumbprint, "ASSET-POP-EC", "1.2.3", pem, sig));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // 3. Missing PoP -> 401
        var missingResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(failToken.Token, "WS-POP-MISSING", "AABBCCDD", null, "1.2.3"));
        Assert.Equal(HttpStatusCode.Unauthorized, missingResp.StatusCode);

        // 4. Expired certificate -> 401
        using (var expiredCert = CreateSelfSignedRsaCert("CN=WS-POP-EXP", now.AddDays(-30), now.AddDays(-1)))
        {
            var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(failToken.Token);
            var sig = EnrollmentProofOfPossession.SignChallenge(expiredCert, tokenHash, "WS-POP-EXP");
            var resp = await anon.PostAsJsonAsync(
                "/api/v1/devices/enroll",
                new EnrollDeviceRequest(failToken.Token, "WS-POP-EXP", expiredCert.Thumbprint, null, "1.2.3", expiredCert.ExportCertificatePem(), sig));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 5. Tampered signature -> 401
        using (var validCert = CreateSelfSignedEcdsaCert("CN=WS-POP-TAMPER", now.AddDays(-1), now.AddDays(30)))
        {
            var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(failToken.Token);
            var sigBytes = Convert.FromBase64String(EnrollmentProofOfPossession.SignChallenge(validCert, tokenHash, "WS-POP-TAMPER"));
            sigBytes[0] ^= 0xFF;
            var tamperedSig = Convert.ToBase64String(sigBytes);

            var resp = await anon.PostAsJsonAsync(
                "/api/v1/devices/enroll",
                new EnrollDeviceRequest(failToken.Token, "WS-POP-TAMPER", validCert.Thumbprint, null, "1.2.3", validCert.ExportCertificatePem(), tamperedSig));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            // 6. Thumbprint mismatch -> 401
            var goodSig = EnrollmentProofOfPossession.SignChallenge(validCert, tokenHash, "WS-POP-MISMATCH");
            var mismatchResp = await anon.PostAsJsonAsync(
                "/api/v1/devices/enroll",
                new EnrollDeviceRequest(failToken.Token, "WS-POP-MISMATCH", "0000000000000000", null, "1.2.3", validCert.ExportCertificatePem(), goodSig));
            Assert.Equal(HttpStatusCode.Unauthorized, mismatchResp.StatusCode);
        }

        // Verify audit events emitted for failed PoP attempts
        var auditPage = await auditor.GetFromJsonAsync<PagedResponse<AuditEventDto>>(
            "/api/v1/audit?action=DeviceEnrollmentPoPFailed&result=Denied&siteId=site-a");
        Assert.NotNull(auditPage);
        Assert.Equal(4, auditPage.TotalCount);
        Assert.All(auditPage.Items, e =>
        {
            Assert.Equal("DeviceEnrollmentPoPFailed", e.Action);
            Assert.Equal("Denied", e.Result);
            Assert.Equal("site-a", e.SiteId);
        });
    }

    [Fact]
    public async Task TelemetryRetentionPruning_DeletesExpiredSamplesAndSnapshots_AndPreservesRecentData()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup", "Supervisor", "site-a");
        using var examiner = factory.CreateUserClient("S-1-5-21-ex", "Examiner", "site-a");

        var dev1 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-RET-01", "THUMB-RET-01");
        var dev2 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-RET-02", "THUMB-RET-02");
        using var devClient1 = factory.CreateDeviceClient("THUMB-RET-01");
        using var devClient2 = factory.CreateDeviceClient("THUMB-RET-02");

        var now = factory.Clock.GetUtcNow();
        var boot1 = Guid.NewGuid();
        var boot2 = Guid.NewGuid();

        // dev1: 1 expired metric sample (>7d) + 1 recent metric sample + 1 expired process snapshot (>24h)
        var post1 = await devClient1.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(
                new[]
                {
                    new MetricSampleDto(boot1, 1, now.AddDays(-8), 10.0, 20.0, 100, 1000, 1.0, 2.0),
                    new MetricSampleDto(boot1, 2, now.AddHours(-1), 30.0, 40.0, 200, 1000, 3.0, 4.0)
                },
                null,
                new ProcessSnapshotDto(
                    Guid.NewGuid(),
                    now.AddHours(-25),
                    new[] { new ProcessEntryDto("old.exe", 100, now.AddHours(-26), 1.0, 1024) })));
        Assert.Equal(HttpStatusCode.OK, post1.StatusCode);

        // dev2: 1 recent metric sample + 1 recent process snapshot (<24h)
        var recentSnapId = Guid.NewGuid();
        var post2 = await devClient2.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(
                new[]
                {
                    new MetricSampleDto(boot2, 1, now.AddHours(-2), 50.0, 60.0, 300, 1000, 5.0, 6.0)
                },
                null,
                new ProcessSnapshotDto(
                    recentSnapId,
                    now.AddHours(-2),
                    new[] { new ProcessEntryDto("recent.exe", 200, now.AddHours(-3), 2.0, 2048) })));
        Assert.Equal(HttpStatusCode.OK, post2.StatusCode);

        // Non-admin cannot trigger manual prune -> 403
        var forbiddenPrune = await examiner.PostAsync("/api/v1/operations/prune-telemetry", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPrune.StatusCode);

        // Admin triggers prune -> 1 sample and 1 process snapshot deleted
        var pruneResp = await admin.PostAsync("/api/v1/operations/prune-telemetry", null);
        Assert.Equal(HttpStatusCode.OK, pruneResp.StatusCode);
        var pruneResult = await pruneResp.Content.ReadFromJsonAsync<TelemetryRetentionPruneResultDto>();
        Assert.NotNull(pruneResult);
        Assert.Equal(1, pruneResult.DeletedMetricSamples);
        Assert.Equal(1, pruneResult.DeletedProcessSnapshots);
        Assert.Equal(now, pruneResult.PrunedAtUtc);

        var tel1 = await supervisor.GetFromJsonAsync<DeviceTelemetryResponse>($"/api/v1/workstations/{dev1.DeviceId:D}/telemetry?sampleLimit=10");
        Assert.NotNull(tel1);
        var preserved1 = Assert.Single(tel1.RecentSamples);
        Assert.Equal(2, preserved1.SampleSequence);
        Assert.Null(tel1.LatestProcessSnapshot);

        var tel2 = await supervisor.GetFromJsonAsync<DeviceTelemetryResponse>($"/api/v1/workstations/{dev2.DeviceId:D}/telemetry?sampleLimit=10");
        Assert.NotNull(tel2);
        Assert.Single(tel2.RecentSamples);
        Assert.NotNull(tel2.LatestProcessSnapshot);
        Assert.Equal(recentSnapId, tel2.LatestProcessSnapshot.SnapshotId);
    }

    [Fact]
    public async Task EmergencyStopAndRestoreDrill_InvalidatesLeases_BlocksNewLeases_AndResumesAfterUnpause()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "*");
        using var swApprover = factory.CreateUserClient("S-1-5-21-sw-approver", "SoftwareApprover", "*");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup", "Supervisor", "site-a");

        var dev1 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-DRILL-01", "THUMB-DRILL-01");
        var dev2 = await EnrollDeviceAsync(factory, admin, "site-a", "WS-DRILL-02", "THUMB-DRILL-02");
        using var devClient1 = factory.CreateDeviceClient("THUMB-DRILL-01");
        using var devClient2 = factory.CreateDeviceClient("THUMB-DRILL-02");

        await SendHeartbeatAsync(devClient1, "WS-DRILL-01");
        await SendHeartbeatAsync(devClient2, "WS-DRILL-02");

        var package = await UploadPackageAsync(swAdmin, Encoding.UTF8.GetBytes("DRILL-MSI-PAYLOAD"), "drill.msi");
        var productResp = await swAdmin.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest("DrillTool", "Vendor", "NumericSegments", "DrillTool"));
        productResp.EnsureSuccessStatusCode();
        var product = (await productResp.Content.ReadFromJsonAsync<SoftwareProductDto>())!;

        var releaseResp = await swAdmin.PostAsJsonAsync(
            "/api/v1/software/releases",
            new CreateSoftwareReleaseRequest(product.ProductId, "1.0.0", "VAL-DRILL-1", package.Sha256));
        releaseResp.EnsureSuccessStatusCode();
        var release = (await releaseResp.Content.ReadFromJsonAsync<SoftwareReleaseDto>())!;

        var approveResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId:D}/approve",
            new ApproveSoftwareReleaseRequest("Approved for drill"));
        approveResp.EnsureSuccessStatusCode();

        var signResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId:D}/sign",
            new SignReleaseRequest());
        signResp.EnsureSuccessStatusCode();

        var jobResp = await swAdmin.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest(
                "site-a",
                release.ReleaseId,
                new[] { dev1.DeviceId, dev2.DeviceId },
                "Mandatory",
                Confirmed: true));
        Assert.Equal(HttpStatusCode.Created, jobResp.StatusCode);
        var job = (await jobResp.Content.ReadFromJsonAsync<DeploymentJobDto>())!;

        var target1 = job.Targets.Single(t => t.DeviceId == dev1.DeviceId);
        var target2 = job.Targets.Single(t => t.DeviceId == dev2.DeviceId);

        // Device 1 transitions Pending -> Downloading -> Staged -> acquires lease -> Installing
        var downloadingResp = await devClient1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{target1.TargetId:D}/events",
            new DeploymentEventRequest("Downloading", "download_started", "Downloading package"));
        Assert.Equal(HttpStatusCode.OK, downloadingResp.StatusCode);

        var stagedResp = await devClient1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{target1.TargetId:D}/events",
            new DeploymentEventRequest("Staged", "staged_ok", "Staged locally"));
        Assert.Equal(HttpStatusCode.OK, stagedResp.StatusCode);

        var lease1Resp = await devClient1.PostAsync($"/api/v1/agent/deployments/{target1.TargetId:D}/lease", null);
        Assert.Equal(HttpStatusCode.OK, lease1Resp.StatusCode);
        var lease1 = (await lease1Resp.Content.ReadFromJsonAsync<DeploymentLeaseResponse>())!;
        Assert.True(lease1.Granted);
        Assert.NotNull(lease1.LeaseId);

        var installingResp = await devClient1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{target1.TargetId:D}/events",
            new DeploymentEventRequest("Installing", "install_started", "Installer started", lease1.LeaseId));
        Assert.Equal(HttpStatusCode.OK, installingResp.StatusCode);

        // Supervisor cannot trigger emergency stop -> 403
        var supStop = await supervisor.PostAsJsonAsync(
            "/api/v1/operations/emergency-stop",
            new EmergencyStopRequest(true, true, "Unauthorized"));
        Assert.Equal(HttpStatusCode.Forbidden, supStop.StatusCode);

        // Admin activates emergency stop and invalidates active leases
        var stopResp = await admin.PostAsJsonAsync(
            "/api/v1/operations/emergency-stop",
            new EmergencyStopRequest(PauseDeployments: true, InvalidateActiveLeases: true, Reason: "Restore drill"));
        Assert.Equal(HttpStatusCode.OK, stopResp.StatusCode);
        var stopPayload = await stopResp.Content.ReadFromJsonAsync<EmergencyStopResponse>();
        Assert.NotNull(stopPayload);
        Assert.True(stopPayload.DeploymentsPaused);
        Assert.Equal(1, stopPayload.InvalidatedLeaseCount);
        Assert.Equal("Restore drill", stopPayload.Reason);

        // Operational status reflects pause and 0 active leases
        var status = await admin.GetFromJsonAsync<OperationalStatusDto>("/api/v1/operations/status");
        Assert.NotNull(status);
        Assert.True(status.DeploymentsPaused);
        Assert.Equal("Restore drill", status.PauseReason);
        Assert.Equal(0, status.ActiveDeploymentLeaseCount);

        // Target 1 transitioned Installing -> Unknown; prior lease rejected (409)
        var jobAfterStop = await admin.GetFromJsonAsync<DeploymentJobDto>($"/api/v1/deployments/{job.JobId:D}");
        Assert.NotNull(jobAfterStop);
        var t1AfterStop = jobAfterStop.Targets.Single(t => t.DeviceId == dev1.DeviceId);
        Assert.Equal("Unknown", t1AfterStop.State);
        Assert.Equal("emergency_stop_invalidated", t1AfterStop.LastReasonCode);

        var staleEventResp = await devClient1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{target1.TargetId:D}/events",
            new DeploymentEventRequest("Verifying", "verify_after_stop", "Should fail", lease1.LeaseId));
        Assert.Equal(HttpStatusCode.Conflict, staleEventResp.StatusCode);

        // Device 2 lease acquisition is blocked while paused
        var pausedLeaseResp = await devClient2.PostAsync($"/api/v1/agent/deployments/{target2.TargetId:D}/lease", null);
        Assert.Equal(HttpStatusCode.OK, pausedLeaseResp.StatusCode);
        var pausedLease = (await pausedLeaseResp.Content.ReadFromJsonAsync<DeploymentLeaseResponse>())!;
        Assert.False(pausedLease.Granted);
        Assert.Equal("deployments_paused_emergency_stop", pausedLease.ReasonCode);

        // Unpause deployments -> Device 2 can acquire a new lease
        var resumeResp = await admin.PostAsJsonAsync(
            "/api/v1/operations/emergency-stop",
            new EmergencyStopRequest(PauseDeployments: false, InvalidateActiveLeases: false, Reason: "Drill complete"));
        Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);

        var resumedLeaseResp = await devClient2.PostAsync($"/api/v1/agent/deployments/{target2.TargetId:D}/lease", null);
        Assert.Equal(HttpStatusCode.OK, resumedLeaseResp.StatusCode);
        var resumedLease = (await resumedLeaseResp.Content.ReadFromJsonAsync<DeploymentLeaseResponse>())!;
        Assert.True(resumedLease.Granted);
        Assert.NotNull(resumedLease.LeaseId);
    }

    [Fact]
    public async Task AuditFilteringAndExport_SupportsFiltersCsvJsonl_NoStore_SiteIsolation_AndCsvInjectionNeutralization()
    {
        await using var factory = new HiveServerFactory { RequireEnrollmentProofOfPossession = true };
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var auditorSiteA = factory.CreateUserClient("S-1-5-21-aud-a", "Auditor", "site-a");
        using var auditorAll = factory.CreateUserClient("S-1-5-21-aud-all", "Auditor", "*");
        using var anon = factory.CreateClient();

        var tokenA = await CreateEnrollmentTokenAsync(adminAll, "site-a");
        var tokenB = await CreateEnrollmentTokenAsync(adminAll, "site-b");

        // Trigger a Denied audit event with formula-like hostname on site-a
        var badEnroll = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(tokenA.Token, "=CMD('calc')", "THUMB-INJ", null, "1.0.0"));
        Assert.Equal(HttpStatusCode.Unauthorized, badEnroll.StatusCode);

        // Valid enrollment on site-b
        var now = factory.Clock.GetUtcNow();
        using var certB = CreateSelfSignedRsaCert("CN=WS-SITEB", now.AddDays(-1), now.AddDays(30));
        var hashB = EnrollmentTokenHasher.ComputeSha256Hex(tokenB.Token);
        var sigB = EnrollmentProofOfPossession.SignChallenge(certB, hashB, "WS-SITEB");
        var goodEnroll = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(tokenB.Token, "WS-SITEB", certB.Thumbprint, null, "1.0.0", certB.ExportCertificatePem(), sigB));
        Assert.Equal(HttpStatusCode.Created, goodEnroll.StatusCode);

        // Filtered query on GET /api/v1/audit
        var filtered = await auditorAll.GetFromJsonAsync<PagedResponse<AuditEventDto>>(
            "/api/v1/audit?action=DeviceEnrollmentPoPFailed&result=Denied&siteId=site-a");
        Assert.NotNull(filtered);
        var deniedEvent = Assert.Single(filtered.Items);
        Assert.Equal("=CMD('calc')", deniedEvent.ActorId);

        // Cross-site export denied for site-a auditor
        var forbiddenExport = await auditorSiteA.GetAsync("/api/v1/audit/export?siteId=site-b");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenExport.StatusCode);

        // CSV export for site-a auditor: no-store, site isolation, formula neutralization
        var csvResp = await auditorSiteA.GetAsync("/api/v1/audit/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        Assert.True(csvResp.Headers.CacheControl?.NoStore);
        Assert.Equal("attachment", csvResp.Content.Headers.ContentDisposition?.DispositionType);
        var csvText = await csvResp.Content.ReadAsStringAsync();
        Assert.Contains("EventId,OccurredAtUtc,ActorType,ActorId", csvText);
        Assert.Contains("\"'=CMD('calc')\"", csvText);
        Assert.DoesNotContain("WS-SITEB", csvText);

        // JSONL export filtered by action=DeviceEnrolled for all-sites auditor
        var jsonlResp = await auditorAll.GetAsync("/api/v1/audit/export?format=jsonl&action=DeviceEnrolled");
        Assert.Equal(HttpStatusCode.OK, jsonlResp.StatusCode);
        Assert.True(jsonlResp.Headers.CacheControl?.NoStore);
        var jsonlText = await jsonlResp.Content.ReadAsStringAsync();
        var lines = jsonlText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lineDto = JsonSerializer.Deserialize<AuditEventDto>(Assert.Single(lines), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(lineDto);
        Assert.Equal("DeviceEnrolled", lineDto.Action);
        Assert.Equal("site-b", lineDto.SiteId);
    }

    [Fact]
    public async Task ReadinessAndOperationalStatus_EnforceAllowAnonymousAndRoleBoundaries()
    {
        await using var factory = new HiveServerFactory();
        using var anon = factory.CreateClient();
        using var examiner = factory.CreateUserClient("S-1-5-21-ex", "Examiner", "site-a");
        using var auditor = factory.CreateUserClient("S-1-5-21-aud", "Auditor", "*");

        var readyResp = await anon.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readyResp.StatusCode);
        using (var doc = JsonDocument.Parse(await readyResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Ready", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("InMemory", doc.RootElement.GetProperty("storageProvider").GetString());
            Assert.Equal(ProtocolConstants.ProtocolVersion, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        }

        var examinerStatus = await examiner.GetAsync("/api/v1/operations/status");
        Assert.Equal(HttpStatusCode.Forbidden, examinerStatus.StatusCode);

        var auditorStatusResp = await auditor.GetAsync("/api/v1/operations/status");
        Assert.Equal(HttpStatusCode.OK, auditorStatusResp.StatusCode);
        var status = await auditorStatusResp.Content.ReadFromJsonAsync<OperationalStatusDto>();
        Assert.NotNull(status);
        Assert.Equal("Healthy", status.Status);
        Assert.Equal("InMemory", status.StorageProvider);
        Assert.False(status.DeploymentsPaused);
    }

    [Fact]
    public async Task LivePostgresSmoke_RunsMigrationsAndRetentionPruneWhenConfigured()
    {
        var connString = Environment.GetEnvironmentVariable("HIVE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connString);
        var runner = new PostgreSqlMigrationRunner(dataSource);
        await runner.MigrateAsync();

        var telemetryRepo = new PostgresTelemetryRepository(dataSource);
        var now = DateTimeOffset.UtcNow;
        var (deletedMetrics, deletedSnapshots) = await telemetryRepo.PruneExpiredAsync(now.AddDays(-7), now.AddHours(-24));
        Assert.True(deletedMetrics >= 0);
        Assert.True(deletedSnapshots >= 0);

        var outboxRepo = new PostgresOutboxRepository(dataSource);
        var pending = await outboxRepo.CountPendingAsync();
        Assert.True(pending >= 0);

        var deployRepo = new PostgresDeploymentRepository(dataSource, telemetryRepo);
        var activeLeases = await deployRepo.CountActiveLeasesAsync(now);
        Assert.True(activeLeases >= 0);
    }

    private static async Task<CreateEnrollmentTokenResponse> CreateEnrollmentTokenAsync(HttpClient adminClient, string siteId)
    {
        var resp = await adminClient.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest(siteId, 30));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>())!;
    }

    private static async Task<EnrollDeviceResponse> EnrollDeviceAsync(
        HiveServerFactory factory,
        HttpClient adminClient,
        string siteId,
        string hostname,
        string thumbprint)
    {
        var token = await CreateEnrollmentTokenAsync(adminClient, siteId);
        using var anon = factory.CreateClient();
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(token.Token, hostname, thumbprint, null, "1.2.3"));
        enrollResp.EnsureSuccessStatusCode();
        return (await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>())!;
    }

    private static async Task SendHeartbeatAsync(HttpClient deviceClient, string hostname)
    {
        var resp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(
                Guid.NewGuid(),
                "1.2.3",
                new[] { "deployments" },
                Array.Empty<ObservedSessionDto>(),
                hostname));
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<PackageUploadResponse> UploadPackageAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        var resp = await client.PostAsync("/api/v1/packages", form);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PackageUploadResponse>())!;
    }

    private static X509Certificate2 CreateSelfSignedRsaCert(string subjectName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 CreateSelfSignedEcdsaCert(string subjectName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subjectName, ecdsa, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(notBefore, notAfter);
    }
}
