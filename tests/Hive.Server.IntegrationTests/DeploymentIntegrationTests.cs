using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Hive.Infrastructure.Migrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class DeploymentIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedSafeDeploymentsMigrationAfterRemoteAccessEscrow()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0007_SafeDeployments.sql", StringComparison.Ordinal));
        var escrowIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0006_RemoteAccessEscrow.sql", StringComparison.Ordinal));
        var deployIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0007_SafeDeployments.sql", StringComparison.Ordinal));
        Assert.True(escrowIdx >= 0 && deployIdx > escrowIdx);
    }

    [Fact]
    public async Task PackageUploadAndDownload_SupportsRange206_ETag304_InvalidRange416_AndRejectsEmpty()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "*");
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner", "Examiner", "site-a");

        var device = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-PKG-01", "THUMB-PKG-01");
        using var deviceClient = factory.CreateDeviceClient("THUMB-PKG-01");

        // Empty multipart upload -> 400
        using (var emptyForm = new MultipartFormDataContent())
        using (var emptyContent = new ByteArrayContent(Array.Empty<byte>()))
        {
            emptyContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            emptyForm.Add(emptyContent, "file", "empty.msi");
            var emptyResp = await swAdmin.PostAsync("/api/v1/packages", emptyForm);
            Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);
        }

        // Examiner cannot upload packages -> 403
        var payload = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-MSI-PAYLOAD");
        var expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using (var forbiddenForm = BuildMultipartPackage(payload, "tool.msi"))
        {
            var forbiddenResp = await examiner.PostAsync("/api/v1/packages", forbiddenForm);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
        }

        // SoftwareAdmin uploads package -> 201 Created
        PackageUploadResponse uploaded;
        using (var form = BuildMultipartPackage(payload, "forensic-tool-1.0.msi"))
        {
            var uploadResp = await swAdmin.PostAsync("/api/v1/packages", form);
            Assert.Equal(HttpStatusCode.Created, uploadResp.StatusCode);
            uploaded = (await uploadResp.Content.ReadFromJsonAsync<PackageUploadResponse>())!;
        }

        Assert.Equal("forensic-tool-1.0.msi", uploaded.FileName);
        Assert.Equal(expectedSha, uploaded.Sha256);
        Assert.Equal(payload.Length, uploaded.SizeBytes);

        var contentUrl = $"/api/v1/packages/{uploaded.Id:D}/content";

        // Device downloads full package -> 200 OK with ETag & Accept-Ranges
        var fullResp = await deviceClient.GetAsync(contentUrl);
        Assert.Equal(HttpStatusCode.OK, fullResp.StatusCode);
        Assert.Equal($"\"{expectedSha}\"", fullResp.Headers.ETag?.ToString());
        Assert.Contains("bytes", fullResp.Headers.AcceptRanges.ToString(), StringComparison.OrdinalIgnoreCase);
        var downloadedBytes = await fullResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, downloadedBytes);

        // Conditional GET with If-None-Match -> 304 Not Modified
        using (var condReq = new HttpRequestMessage(HttpMethod.Get, contentUrl))
        {
            condReq.Headers.TryAddWithoutValidation("If-None-Match", $"\"{expectedSha}\"");
            var condResp = await deviceClient.SendAsync(condReq);
            Assert.Equal(HttpStatusCode.NotModified, condResp.StatusCode);
            Assert.Equal($"\"{expectedSha}\"", condResp.Headers.ETag?.ToString());
        }

        // Partial byte range -> 206 Partial Content
        using (var rangeReq = new HttpRequestMessage(HttpMethod.Get, contentUrl))
        {
            rangeReq.Headers.Range = new RangeHeaderValue(5, 19);
            var rangeResp = await deviceClient.SendAsync(rangeReq);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResp.StatusCode);
            Assert.Contains("bytes", rangeResp.Headers.AcceptRanges.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal($"\"{expectedSha}\"", rangeResp.Headers.ETag?.ToString());
            Assert.Equal($"bytes 5-19/{payload.Length}", rangeResp.Content.Headers.ContentRange?.ToString());
            var slice = await rangeResp.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload[5..20], slice);
        }

        // Out-of-bounds range -> 416 Range Not Satisfiable
        using (var badRangeReq = new HttpRequestMessage(HttpMethod.Get, contentUrl))
        {
            badRangeReq.Headers.Range = new RangeHeaderValue(payload.Length + 10, payload.Length + 20);
            var badRangeResp = await deviceClient.SendAsync(badRangeReq);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, badRangeResp.StatusCode);
        }

        Assert.NotEqual(Guid.Empty, device.DeviceId);
    }

    [Fact]
    public async Task ReleaseSigning_ProducesValidCanonicalManifest_AndDetectsTampering()
    {
        await using var factory = new HiveServerFactory();
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "*");
        using var swApprover = factory.CreateUserClient("S-1-5-21-sw-approver", "SoftwareApprover", "*");
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner", "Examiner", "site-a");

        var msiBytes = Encoding.UTF8.GetBytes("FAKE-MSI-BINARY-CONTENT-FOR-SIGNING");
        var package = await UploadPackageAsync(swAdmin, msiBytes, "axiom-8.5.msi");

        var product = await CreateProductAsync(swAdmin, "Magnet AXIOM", "Magnet Forensics", "NumericSegments", "Magnet AXIOM", new[] { "AXIOMExamine.exe" });
        var release = await CreateReleaseAsync(swAdmin, product.ProductId, "8.5.0.1", "VAL-2026-100", package.Sha256);

        // Examiner cannot sign -> 403
        var forbiddenSign = await examiner.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId:D}/sign",
            new SignReleaseRequest());
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSign.StatusCode);

        // SoftwareApprover signs -> 200 OK
        var signResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId:D}/sign",
            new SignReleaseRequest(ProtectedProcesses: new[] { "FTKImager.exe" }, RebootAllowed: false, ValidDays: 60));
        Assert.Equal(HttpStatusCode.OK, signResp.StatusCode);

        var envDto = await signResp.Content.ReadFromJsonAsync<SignedManifestEnvelopeDto>();
        Assert.NotNull(envDto);
        Assert.Equal("MSI", envDto.Manifest.InstallerType);
        Assert.Equal("validated-msi-no-restart", envDto.Manifest.InstallProfile);
        Assert.Equal(package.Sha256, envDto.Manifest.PackageSha256);
        Assert.Equal(package.SizeBytes, envDto.Manifest.PackageSizeBytes);
        Assert.Equal($"/api/v1/packages/{package.Id:D}/content", envDto.Manifest.DownloadRelativeUrl);
        Assert.Contains("AXIOMExamine.exe", envDto.Manifest.ProtectedProcesses);
        Assert.Contains("FTKImager.exe", envDto.Manifest.ProtectedProcesses);

        // Verify valid signature via domain crypto
        var domainEnvelope = ToDomainEnvelope(envDto);
        Assert.True(ReleaseManifestCrypto.Verify(domainEnvelope, out var okReason, utcNow: factory.Clock.GetUtcNow()));
        Assert.Equal("manifest_valid", okReason);

        // Tamper with manifest -> Verify fails
        var tamperedEnvelope = domainEnvelope with
        {
            Manifest = domainEnvelope.Manifest with { Version = "9.9.9.9" }
        };
        Assert.False(ReleaseManifestCrypto.Verify(tamperedEnvelope, out var tamperReason, utcNow: factory.Clock.GetUtcNow()));
        Assert.Equal("manifest_canonical_mismatch", tamperReason);

        var tamperedCanonicalAndManifest = domainEnvelope with
        {
            Manifest = domainEnvelope.Manifest with { Version = "9.9.9.9" },
            ManifestCanonicalJson = ReleaseManifestCrypto.Canonicalize(domainEnvelope.Manifest with { Version = "9.9.9.9" })
        };
        Assert.False(ReleaseManifestCrypto.Verify(tamperedCanonicalAndManifest, out var sigFailReason, utcNow: factory.Clock.GetUtcNow()));
        Assert.Equal("manifest_invalid_signature", sigFailReason);
    }

    [Fact]
    public async Task DeploymentCreation_RejectsUnconfirmed_Unapproved_Unsigned_AndCrossSite()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var swAdminSiteA = factory.CreateUserClient("S-1-5-21-sw-a", "SoftwareAdmin", "site-a");
        using var swApproverAll = factory.CreateUserClient("S-1-5-21-approver", "SoftwareApprover", "*");

        var devA = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-DEP-A1", "THUMB-DEP-A1");
        var devB = await EnrollDeviceAsync(factory, adminAll, "site-b", "WS-DEP-B1", "THUMB-DEP-B1");

        var pkg = await UploadPackageAsync(swAdminSiteA, Encoding.UTF8.GetBytes("MSI-DATA"), "app.msi");
        var product = await CreateProductAsync(swAdminSiteA, "EnCase", "OpenText", "NumericSegments", "EnCase");
        var release = await CreateReleaseAsync(swAdminSiteA, product.ProductId, "24.1.0", "VAL-ENC-24", pkg.Sha256);

        // 1. Confirmed = false -> 400 Bad Request
        var unconfirmedResp = await swAdminSiteA.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", release.ReleaseId, new[] { devA.DeviceId }, "Mandatory", Confirmed: false));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmedResp.StatusCode);

        // 2. Unapproved (Draft) release -> 409 Conflict
        var draftResp = await swAdminSiteA.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", release.ReleaseId, new[] { devA.DeviceId }, "Mandatory", Confirmed: true));
        Assert.Equal(HttpStatusCode.Conflict, draftResp.StatusCode);

        // Approve release, but do NOT sign yet -> 409 Conflict
        await ApproveReleaseAsync(swApproverAll, release.ReleaseId);
        var unsignedResp = await swAdminSiteA.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", release.ReleaseId, new[] { devA.DeviceId }, "Mandatory", Confirmed: true));
        Assert.Equal(HttpStatusCode.Conflict, unsignedResp.StatusCode);

        // Sign release
        var signResp = await swApproverAll.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId:D}/sign",
            new SignReleaseRequest());
        signResp.EnsureSuccessStatusCode();

        // 3. Cross-site isolation: site-a SoftwareAdmin cannot deploy to site-b or target site-b device -> 403
        var crossSiteResp = await swAdminSiteA.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-b", release.ReleaseId, new[] { devB.DeviceId }, "Mandatory", Confirmed: true));
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResp.StatusCode);

        var crossDeviceResp = await swAdminSiteA.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", release.ReleaseId, new[] { devB.DeviceId }, "Mandatory", Confirmed: true));
        Assert.Equal(HttpStatusCode.Forbidden, crossDeviceResp.StatusCode);

        var crossListResp = await swAdminSiteA.GetAsync("/api/v1/deployments?siteId=site-b");
        Assert.Equal(HttpStatusCode.Forbidden, crossListResp.StatusCode);
    }

    [Fact]
    public async Task DeploymentLifecycle_Sync_ExclusiveLease_ProtectionGate_Transitions_AndCancellation()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "site-a");
        using var swApprover = factory.CreateUserClient("S-1-5-21-sw-approver", "SoftwareApprover", "*");
        using var auditor = factory.CreateUserClient("S-1-5-21-auditor", "Auditor", "*");

        var ws1 = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-SAFE-01", "THUMB-SAFE-01");
        var ws2 = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-SAFE-02", "THUMB-SAFE-02");
        using var dev1 = factory.CreateDeviceClient("THUMB-SAFE-01");
        using var dev2 = factory.CreateDeviceClient("THUMB-SAFE-02");

        var pkg1 = await UploadPackageAsync(swAdmin, Encoding.UTF8.GetBytes("MSI-V1-BYTES"), "tool-v1.msi");
        var pkg2 = await UploadPackageAsync(swAdmin, Encoding.UTF8.GetBytes("MSI-V2-BYTES"), "tool-v2.msi");
        var product = await CreateProductAsync(swAdmin, "X-Ways Forensics", "X-Ways", "NumericSegments", "X-Ways", new[] { "xwforensics64.exe" });

        var rel1 = await CreateReleaseAsync(swAdmin, product.ProductId, "21.1.0", "VAL-XW-1", pkg1.Sha256);
        await ApproveReleaseAsync(swApprover, rel1.ReleaseId);
        (await swApprover.PostAsJsonAsync($"/api/v1/software/releases/{rel1.ReleaseId:D}/sign", new SignReleaseRequest())).EnsureSuccessStatusCode();

        var rel2 = await CreateReleaseAsync(swAdmin, product.ProductId, "21.2.0", "VAL-XW-2", pkg2.Sha256);
        await ApproveReleaseAsync(swApprover, rel2.ReleaseId);
        (await swApprover.PostAsJsonAsync($"/api/v1/software/releases/{rel2.ReleaseId:D}/sign", new SignReleaseRequest())).EnsureSuccessStatusCode();

        // Create Job 1 (targeting ws1 & ws2) and Job 2 (targeting ws1)
        var job1Resp = await swAdmin.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", rel1.ReleaseId, new[] { ws1.DeviceId, ws2.DeviceId }, "Mandatory", Confirmed: true));
        Assert.Equal(HttpStatusCode.Created, job1Resp.StatusCode);
        var job1 = (await job1Resp.Content.ReadFromJsonAsync<DeploymentJobDto>())!;
        Assert.Equal(2, job1.Targets.Count);

        var job2Resp = await swAdmin.PostAsJsonAsync(
            "/api/v1/deployments",
            new CreateDeploymentJobRequest("site-a", rel2.ReleaseId, new[] { ws1.DeviceId }, "RingPilot", Confirmed: true));
        Assert.Equal(HttpStatusCode.Created, job2Resp.StatusCode);
        var job2 = (await job2Resp.Content.ReadFromJsonAsync<DeploymentJobDto>())!;

        var targetWs1Job1 = job1.Targets.Single(t => t.DeviceId == ws1.DeviceId);
        var targetWs2Job1 = job1.Targets.Single(t => t.DeviceId == ws2.DeviceId);
        var targetWs1Job2 = job2.Targets.Single(t => t.DeviceId == ws1.DeviceId);

        // Agent sync for ws1 returns both pending deployments with signed envelopes
        var sync1 = await dev1.GetFromJsonAsync<AgentSyncResponse>("/api/v1/agent/sync");
        Assert.NotNull(sync1);
        Assert.Equal(2, sync1.PendingDeployments.Count);

        // 1. Fail-closed lease acquisition before heartbeat -> Granted = false
        var noHbLease = await AcquireLeaseAsync(dev1, targetWs1Job1.TargetId);
        Assert.False(noHbLease.Granted);
        Assert.Equal("protection_state_unknown", noHbLease.ReasonCode);

        // 2. Heartbeat with ProtectedModeActive = true -> Granted = false
        await SendHeartbeatAsync(dev1, "WS-SAFE-01", protectedModeActive: true, "Imaging case 2026-09");
        var protectedLease = await AcquireLeaseAsync(dev1, targetWs1Job1.TargetId);
        Assert.False(protectedLease.Granted);
        Assert.Equal("manual_protection_lock_active", protectedLease.ReasonCode);

        // 3. Heartbeat with ProtectedModeActive = false -> Lease granted!
        factory.Clock.Advance(TimeSpan.FromSeconds(5));
        await SendHeartbeatAsync(dev1, "WS-SAFE-01", protectedModeActive: false, null);
        var lease1 = await AcquireLeaseAsync(dev1, targetWs1Job1.TargetId);
        Assert.True(lease1.Granted);
        Assert.NotNull(lease1.LeaseId);

        // 4. Conflicting second lease on ws1 for Job 2 rejected!
        var conflictLease = await AcquireLeaseAsync(dev1, targetWs1Job2.TargetId);
        Assert.False(conflictLease.Granted);
        Assert.Equal("device_lease_conflict", conflictLease.ReasonCode);

        // 5. Cross-device isolation: ws2 cannot acquire lease or post events for ws1's target -> 403
        var crossLeaseResp = await dev2.PostAsync($"/api/v1/agent/deployments/{targetWs1Job1.TargetId:D}/lease", null);
        Assert.Equal(HttpStatusCode.Forbidden, crossLeaseResp.StatusCode);

        var crossEventResp = await dev2.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{targetWs1Job1.TargetId:D}/events",
            new DeploymentEventRequest("Downloading", "cross_device_attempt"));
        Assert.Equal(HttpStatusCode.Forbidden, crossEventResp.StatusCode);

        // 6. Illegal state transition Pending -> Verified -> 409 Conflict
        var illegalResp = await dev1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{targetWs1Job1.TargetId:D}/events",
            new DeploymentEventRequest("Verified", "skip_steps", LeaseId: lease1.LeaseId));
        Assert.Equal(HttpStatusCode.Conflict, illegalResp.StatusCode);

        // 7. Full state progression Pending -> Downloading -> Staged -> Installing -> Verifying -> Verified
        await PostEventExpectSuccessAsync(dev1, targetWs1Job1.TargetId, "Downloading", "download_started");
        await PostEventExpectSuccessAsync(dev1, targetWs1Job1.TargetId, "Staged", "sha256_verified");

        // Execution transition without leaseId -> 409 Conflict
        var missingLeaseResp = await dev1.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{targetWs1Job1.TargetId:D}/events",
            new DeploymentEventRequest("Installing", "no_lease"));
        Assert.Equal(HttpStatusCode.Conflict, missingLeaseResp.StatusCode);

        await PostEventExpectSuccessAsync(dev1, targetWs1Job1.TargetId, "Installing", "msi_started", lease1.LeaseId);
        await PostEventExpectSuccessAsync(dev1, targetWs1Job1.TargetId, "Verifying", "msi_exit_0", lease1.LeaseId, exitCode: 0);
        var verifiedDto = await PostEventExpectSuccessAsync(dev1, targetWs1Job1.TargetId, "Verified", "inventory_confirmed", lease1.LeaseId);
        Assert.Equal("Verified", verifiedDto.State);
        Assert.Equal(5, verifiedDto.AttemptCount);

        // 8. Cancel Job 1: ws2 (Pending) -> Cancelled, ws1 (Verified) stays Verified!
        var cancelResp = await swAdmin.PostAsync($"/api/v1/deployments/{job1.JobId:D}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var cancelledJob1 = (await cancelResp.Content.ReadFromJsonAsync<DeploymentJobDto>())!;
        Assert.Equal("Verified", cancelledJob1.Targets.Single(t => t.TargetId == targetWs1Job1.TargetId).State);
        Assert.Equal("Cancelled", cancelledJob1.Targets.Single(t => t.TargetId == targetWs2Job1.TargetId).State);

        // 9. Stale heartbeat fail-closed on Job 2, then fresh heartbeat succeeds after Job 1 cleared its lease
        factory.Clock.Advance(TimeSpan.FromSeconds(50));
        var staleLease = await AcquireLeaseAsync(dev1, targetWs1Job2.TargetId);
        Assert.False(staleLease.Granted);
        Assert.Equal("protection_state_unknown", staleLease.ReasonCode);

        await SendHeartbeatAsync(dev1, "WS-SAFE-01", protectedModeActive: false, null);
        var job2Lease = await AcquireLeaseAsync(dev1, targetWs1Job2.TargetId);
        Assert.True(job2Lease.Granted);

        // Audit verification
        var auditPage = await auditor.GetFromJsonAsync<PagedResponse<AuditEventDto>>("/api/v1/audit?page=1&pageSize=100");
        Assert.NotNull(auditPage);
        Assert.Contains(auditPage.Items, e => e.Action == "PackageUploaded");
        Assert.Contains(auditPage.Items, e => e.Action == "SoftwareReleaseSigned");
        Assert.Contains(auditPage.Items, e => e.Action == "DeploymentJobCreated");
        Assert.Contains(auditPage.Items, e => e.Action == "DeploymentJobCancelled");
        Assert.Contains(auditPage.Items, e => e.Action == "DeploymentTargetEventRecorded");
    }

    private static MultipartFormDataContent BuildMultipartPackage(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        return form;
    }

    private static async Task<PackageUploadResponse> UploadPackageAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = BuildMultipartPackage(bytes, fileName);
        var resp = await client.PostAsync("/api/v1/packages", form);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PackageUploadResponse>())!;
    }

    private static async Task<SoftwareProductDto> CreateProductAsync(
        HttpClient client,
        string name,
        string publisher,
        string versionScheme,
        string matchPattern,
        IReadOnlyList<string>? protectedProcesses = null)
    {
        var resp = await client.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest(name, publisher, versionScheme, matchPattern, protectedProcesses));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SoftwareProductDto>())!;
    }

    private static async Task<SoftwareReleaseDto> CreateReleaseAsync(
        HttpClient client,
        Guid productId,
        string version,
        string validationRef,
        string? packageSha256)
    {
        var resp = await client.PostAsJsonAsync(
            "/api/v1/software/releases",
            new CreateSoftwareReleaseRequest(productId, version, validationRef, packageSha256));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SoftwareReleaseDto>())!;
    }

    private static async Task ApproveReleaseAsync(HttpClient approverClient, Guid releaseId)
    {
        var resp = await approverClient.PostAsJsonAsync(
            $"/api/v1/software/releases/{releaseId:D}/approve",
            new ApproveSoftwareReleaseRequest("Approved for deployment"));
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<EnrollDeviceResponse> EnrollDeviceAsync(
        WebApplicationFactory<Program> factory,
        HttpClient adminClient,
        string siteId,
        string hostname,
        string thumbprint)
    {
        var tokenResp = await adminClient.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest(siteId, 30));
        tokenResp.EnsureSuccessStatusCode();
        var token = (await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>())!;

        using var anon = factory.CreateClient();
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(token.Token, hostname, thumbprint, null, "1.2.3"));
        enrollResp.EnsureSuccessStatusCode();
        return (await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>())!;
    }

    private static async Task SendHeartbeatAsync(
        HttpClient deviceClient,
        string hostname,
        bool protectedModeActive,
        string? protectedModeReason)
    {
        var resp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(
                Guid.NewGuid(),
                "1.2.3",
                new[] { "deployments" },
                Array.Empty<ObservedSessionDto>(),
                hostname,
                protectedModeActive,
                protectedModeReason));
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<DeploymentLeaseResponse> AcquireLeaseAsync(HttpClient deviceClient, Guid targetId)
    {
        var resp = await deviceClient.PostAsync($"/api/v1/agent/deployments/{targetId:D}/lease", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<DeploymentLeaseResponse>())!;
    }

    private static async Task<DeploymentTargetDto> PostEventExpectSuccessAsync(
        HttpClient deviceClient,
        Guid targetId,
        string state,
        string reasonCode,
        Guid? leaseId = null,
        int? exitCode = null)
    {
        var resp = await deviceClient.PostAsJsonAsync(
            $"/api/v1/agent/deployments/{targetId:D}/events",
            new DeploymentEventRequest(state, reasonCode, $"Transition to {state}", leaseId, exitCode));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<DeploymentTargetDto>())!;
    }

    private static SignedManifestEnvelope ToDomainEnvelope(SignedManifestEnvelopeDto dto) =>
        new(
            new SignedReleaseManifest(
                dto.Manifest.ReleaseId,
                dto.Manifest.ProductId,
                dto.Manifest.ProductName,
                dto.Manifest.Version,
                dto.Manifest.InstallerType,
                dto.Manifest.PackageSha256,
                dto.Manifest.PackageSizeBytes,
                dto.Manifest.DownloadRelativeUrl,
                dto.Manifest.SilentArgs,
                dto.Manifest.VerificationType,
                dto.Manifest.VerificationTarget,
                dto.Manifest.ExpectedVersion,
                dto.Manifest.ProtectedProcesses,
                dto.Manifest.RebootAllowed,
                dto.Manifest.InstallProfile,
                dto.Manifest.IssuedUtc,
                dto.Manifest.ExpiresUtc,
                dto.Manifest.KeyId),
            dto.ManifestCanonicalJson,
            dto.SignatureBase64,
            dto.Algorithm,
            dto.PublicKeyPem);
}
