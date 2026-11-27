using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Infrastructure.Migrations;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class SoftwareComplianceIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedSoftwareComplianceMigrationAfterLocalAuth()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0004_SoftwareCompliance.sql", StringComparison.Ordinal));
        var authIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0003_LocalAuthAccounts.sql", StringComparison.Ordinal));
        var softwareIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0004_SoftwareCompliance.sql", StringComparison.Ordinal));
        Assert.True(authIdx >= 0 && softwareIdx > authIdx);
    }

    [Fact]
    public async Task ReleaseLifecycle_DualControlRejectsSelfApproval_AndSeparateApproverSucceeds()
    {
        await using var factory = new HiveServerFactory();
        using var adminDual = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin,SoftwareApprover", "*");
        using var approver = factory.CreateUserClient("S-1-5-21-sw-approver", "SoftwareApprover", "*");

        var createProductResp = await adminDual.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest(
                "Magnet AXIOM",
                "Magnet Forensics",
                "NumericSegments",
                "Magnet AXIOM",
                new[] { "AXIOMExamine.exe" }));
        Assert.Equal(HttpStatusCode.Created, createProductResp.StatusCode);
        var product = await createProductResp.Content.ReadFromJsonAsync<SoftwareProductDto>();
        Assert.NotNull(product);

        var draftResp = await adminDual.PostAsJsonAsync(
            "/api/v1/software/releases",
            new CreateSoftwareReleaseRequest(
                product.ProductId,
                "8.4.0.100",
                "VAL-2026-042",
                new string('a', 64),
                "sha256:manifest-1"));
        Assert.Equal(HttpStatusCode.Created, draftResp.StatusCode);
        var release = await draftResp.Content.ReadFromJsonAsync<SoftwareReleaseDto>();
        Assert.NotNull(release);
        Assert.Equal("Draft", release.ApprovalState);

        // Same user (even holding SoftwareApprover) gets 403 attempting self-approval.
        var selfApproveResp = await adminDual.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/approve",
            new ApproveSoftwareReleaseRequest("Trying self approval"));
        Assert.Equal(HttpStatusCode.Forbidden, selfApproveResp.StatusCode);

        // Separate SoftwareApprover approves -> 200.
        var approveResp = await approver.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/approve",
            new ApproveSoftwareReleaseRequest("Validated against benchmark dataset"));
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        var approved = await approveResp.Content.ReadFromJsonAsync<SoftwareReleaseDto>();
        Assert.NotNull(approved);
        Assert.Equal("Approved", approved.ApprovalState);
        Assert.Equal("S-1-5-21-sw-approver", approved.ApprovedBySid);

        // Re-approving an already Approved release returns 409 Conflict.
        var secondApproveResp = await approver.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/approve",
            new ApproveSoftwareReleaseRequest("Duplicate approval"));
        Assert.Equal(HttpStatusCode.Conflict, secondApproveResp.StatusCode);
    }

    [Fact]
    public async Task InventoryAndCompliance_EvaluatesCompliantUpdateRequiredAheadUnknown_ExemptAndWithdraw_WithAudit()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var swAdmin = factory.CreateUserClient("S-1-5-21-sw-admin", "SoftwareAdmin", "*");
        using var swApprover = factory.CreateUserClient("S-1-5-21-sw-approver", "SoftwareApprover", "*");

        // Create product + draft + approve release 2.0.0.
        var productResp = await swAdmin.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest("Cellebrite UFED", "Cellebrite", "SemVer", "UFED"));
        Assert.Equal(HttpStatusCode.Created, productResp.StatusCode);
        var product = (await productResp.Content.ReadFromJsonAsync<SoftwareProductDto>())!;

        var releaseResp = await swAdmin.PostAsJsonAsync(
            "/api/v1/software/releases",
            new CreateSoftwareReleaseRequest(product.ProductId, "2.0.0", "VAL-UFED-200"));
        Assert.Equal(HttpStatusCode.Created, releaseResp.StatusCode);
        var release = (await releaseResp.Content.ReadFromJsonAsync<SoftwareReleaseDto>())!;

        var approveResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/approve",
            new ApproveSoftwareReleaseRequest());
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // Enroll four devices for the four evaluation outcomes.
        var devCompliant = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-COMP", "THUMB-COMP");
        var devOutdated = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-OLD", "THUMB-OLD");
        var devAhead = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-AHEAD", "THUMB-AHEAD");
        var devUnknown = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-UNK", "THUMB-UNK");

        using var clientCompliant = factory.CreateDeviceClient("THUMB-COMP");
        using var clientOutdated = factory.CreateDeviceClient("THUMB-OLD");
        using var clientAhead = factory.CreateDeviceClient("THUMB-AHEAD");
        using var clientUnknown = factory.CreateDeviceClient("THUMB-UNK");

        var now = factory.Clock.GetUtcNow();

        await PutInventoryAsync(clientCompliant, now,
            new InstalledSoftwareItemDto("Cellebrite UFED Physical Analyzer", "Cellebrite", "2.0.0", "Registry", "Machine", "Verified", now),
            new InstalledSoftwareItemDto("7-Zip 24.08", "Igor Pavlov", "24.08", "Registry", "Machine", "Verified", now));

        await PutInventoryAsync(clientOutdated, now,
            new InstalledSoftwareItemDto("Cellebrite UFED Physical Analyzer", "Cellebrite", "1.9.5", "Registry", "Machine", "Verified", now));

        await PutInventoryAsync(clientAhead, now,
            new InstalledSoftwareItemDto("Cellebrite UFED Physical Analyzer", "Cellebrite", "2.1.0", "Registry", "Machine", "Verified", now));

        await PutInventoryAsync(clientUnknown, now,
            new InstalledSoftwareItemDto("Cellebrite UFED Physical Analyzer", "Cellebrite", "not-a-semver", "File", "Machine", "Partial", now));

        // Verify individual compliance statuses and unmanaged discovered software.
        var compSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devCompliant.DeviceId}/compliance");
        Assert.NotNull(compSummary);
        Assert.Equal(1, compSummary.CompliantCount);
        Assert.Equal("Compliant", Assert.Single(compSummary.Products).ComplianceStatus);
        var unmanaged = Assert.Single(compSummary.UnmanagedDiscovered);
        Assert.Equal("7-Zip 24.08", unmanaged.DisplayName);

        var oldSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devOutdated.DeviceId}/compliance");
        Assert.NotNull(oldSummary);
        Assert.Equal(1, oldSummary.UpdateRequiredCount);
        Assert.Equal("UpdateRequired", Assert.Single(oldSummary.Products).ComplianceStatus);

        var aheadSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devAhead.DeviceId}/compliance");
        Assert.NotNull(aheadSummary);
        Assert.Equal(1, aheadSummary.AheadUnapprovedCount);
        Assert.Equal("AheadUnapproved", Assert.Single(aheadSummary.Products).ComplianceStatus);

        var unkSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devUnknown.DeviceId}/compliance");
        Assert.NotNull(unkSummary);
        Assert.Equal(1, unkSummary.UnknownCount);
        Assert.Equal(0, unkSummary.CompliantCount);
        Assert.Equal("Unknown", Assert.Single(unkSummary.Products).ComplianceStatus);

        // Create time-bounded exemption on outdated device -> Exempt.
        var exemptionResp = await swApprover.PostAsJsonAsync(
            "/api/v1/software/exemptions",
            new CreateSoftwareExemptionRequest(
                devOutdated.DeviceId,
                product.ProductId,
                "1.9.5",
                "Active case requires legacy parser",
                now,
                now.AddDays(7)));
        Assert.Equal(HttpStatusCode.Created, exemptionResp.StatusCode);
        var exemption = (await exemptionResp.Content.ReadFromJsonAsync<SoftwareExemptionDto>())!;
        Assert.True(exemption.IsActive);

        var exemptSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devOutdated.DeviceId}/compliance");
        Assert.NotNull(exemptSummary);
        Assert.Equal(1, exemptSummary.ExemptCount);
        var exemptProduct = Assert.Single(exemptSummary.Products);
        Assert.Equal("Exempt", exemptProduct.ComplianceStatus);
        Assert.Equal(exemption.ExemptionId, exemptProduct.ActiveExemptionId);

        // Revoke exemption -> reverts to UpdateRequired.
        var revokeResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/exemptions/{exemption.ExemptionId}/revoke",
            new RevokeSoftwareExemptionRequest("Case completed"));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
        var revoked = (await revokeResp.Content.ReadFromJsonAsync<SoftwareExemptionDto>())!;
        Assert.False(revoked.IsActive);

        var revertedSummary = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devOutdated.DeviceId}/compliance");
        Assert.NotNull(revertedSummary);
        Assert.Equal(1, revertedSummary.UpdateRequiredCount);
        Assert.Equal("UpdateRequired", Assert.Single(revertedSummary.Products).ComplianceStatus);

        // Withdraw release -> compliance falls back to Unknown (no approved target).
        var withdrawResp = await swApprover.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/withdraw",
            new WithdrawSoftwareReleaseRequest("Regression discovered"));
        Assert.Equal(HttpStatusCode.OK, withdrawResp.StatusCode);

        var afterWithdraw = await adminAll.GetFromJsonAsync<DeviceComplianceSummaryDto>(
            $"/api/v1/workstations/{devCompliant.DeviceId}/compliance");
        Assert.NotNull(afterWithdraw);
        Assert.Equal(1, afterWithdraw.UnknownCount);
        Assert.Equal("Unknown", Assert.Single(afterWithdraw.Products).ComplianceStatus);

        // Audit trail contains exemption creation/revocation and release lifecycle events.
        var auditPage = await adminAll.GetFromJsonAsync<PagedResponse<AuditEventDto>>("/api/v1/audit?page=1&pageSize=100");
        Assert.NotNull(auditPage);
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareProductCreated");
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareReleaseDrafted");
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareReleaseApproved");
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareReleaseWithdrawn");
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareExemptionCreated");
        Assert.Contains(auditPage.Items, a => a.Action == "SoftwareExemptionRevoked");
    }

    [Fact]
    public async Task InventoryEndpoint_RejectsOversizedPayload_AndEnforcesDeviceAuth()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        await EnrollDeviceAsync(factory, admin, "site-a", "WS-LIM", "THUMB-LIM");
        using var deviceClient = factory.CreateDeviceClient("THUMB-LIM");

        var now = factory.Clock.GetUtcNow();
        var oversized = Enumerable.Range(1, 2001)
            .Select(i => new InstalledSoftwareItemDto($"App {i}", "Pub", "1.0.0", "Registry", "Machine", "Verified", now))
            .ToArray();

        var oversizedResp = await deviceClient.PutAsJsonAsync(
            "/api/v1/agent/inventory",
            new InventorySnapshotRequest(1, true, oversized, now));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedResp.StatusCode);

        var userCallResp = await admin.PutAsJsonAsync(
            "/api/v1/agent/inventory",
            new InventorySnapshotRequest(1, true, Array.Empty<InstalledSoftwareItemDto>(), now));
        Assert.Equal(HttpStatusCode.Unauthorized, userCallResp.StatusCode);
    }

    [Fact]
    public async Task Examiner_CanReadOwnWorkstationCompliance_AndReceives403OnFleetAndMutations()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup", "Supervisor", "site-a");
        using var examiner = factory.CreateUserClient("S-1-5-21-examiner", "Examiner", "site-a");
        using var otherExaminer = factory.CreateUserClient("S-1-5-21-other", "Examiner", "site-a");

        var productResp = await admin.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest("EnCase", "OpenText", "NumericSegments", "EnCase"));
        Assert.Equal(HttpStatusCode.Created, productResp.StatusCode);
        var product = (await productResp.Content.ReadFromJsonAsync<SoftwareProductDto>())!;

        var releaseResp = await admin.PostAsJsonAsync(
            "/api/v1/software/releases",
            new CreateSoftwareReleaseRequest(product.ProductId, "24.2", "VAL-ENC"));
        Assert.Equal(HttpStatusCode.Created, releaseResp.StatusCode);
        var release = (await releaseResp.Content.ReadFromJsonAsync<SoftwareReleaseDto>())!;

        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-EX-COMP", "THUMB-EX-COMP");
        var assignResp = await supervisor.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-EX-1", "S-1-5-21-examiner", ExpectedRowVersion: 1));
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);

        // Assigned Examiner can read own workstation compliance -> 200.
        var ownCompliance = await examiner.GetAsync($"/api/v1/workstations/{dev.DeviceId}/compliance");
        Assert.Equal(HttpStatusCode.OK, ownCompliance.StatusCode);

        // Unassigned Examiner cannot read -> 403.
        var otherCompliance = await otherExaminer.GetAsync($"/api/v1/workstations/{dev.DeviceId}/compliance");
        Assert.Equal(HttpStatusCode.Forbidden, otherCompliance.StatusCode);

        // Examiner gets 403 on fleet compliance and software catalogue/approval/exemption mutations.
        var fleetResp = await examiner.GetAsync("/api/v1/software/compliance");
        Assert.Equal(HttpStatusCode.Forbidden, fleetResp.StatusCode);

        var createProdResp = await examiner.PostAsJsonAsync(
            "/api/v1/software/products",
            new CreateSoftwareProductRequest("Test", "Pub", "Exact", "Test"));
        Assert.Equal(HttpStatusCode.Forbidden, createProdResp.StatusCode);

        var approveResp = await examiner.PostAsJsonAsync(
            $"/api/v1/software/releases/{release.ReleaseId}/approve",
            new ApproveSoftwareReleaseRequest());
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);

        var exemptionResp = await examiner.PostAsJsonAsync(
            "/api/v1/software/exemptions",
            new CreateSoftwareExemptionRequest(
                dev.DeviceId,
                product.ProductId,
                null,
                "Not allowed",
                factory.Clock.GetUtcNow(),
                factory.Clock.GetUtcNow().AddDays(1)));
        Assert.Equal(HttpStatusCode.Forbidden, exemptionResp.StatusCode);
    }

    private static async Task PutInventoryAsync(
        HttpClient deviceClient,
        DateTimeOffset now,
        params InstalledSoftwareItemDto[] items)
    {
        var resp = await deviceClient.PutAsJsonAsync(
            "/api/v1/agent/inventory",
            new InventorySnapshotRequest(1, true, items, now));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task<EnrollDeviceResponse> EnrollDeviceAsync(
        HiveServerFactory factory,
        HttpClient adminClient,
        string siteId,
        string hostname,
        string thumbprint)
    {
        var tokenResp = await adminClient.PostAsJsonAsync(
            "/api/v1/enrollment-tokens",
            new CreateEnrollmentTokenRequest(siteId, 15));
        tokenResp.EnsureSuccessStatusCode();
        var token = (await tokenResp.Content.ReadFromJsonAsync<CreateEnrollmentTokenResponse>())!;

        using var anon = factory.CreateClient();
        var enrollResp = await anon.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(token.Token, hostname, thumbprint, null, "1.0.0"));
        enrollResp.EnsureSuccessStatusCode();
        return (await enrollResp.Content.ReadFromJsonAsync<EnrollDeviceResponse>())!;
    }
}
