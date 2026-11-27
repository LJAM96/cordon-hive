using System.Net;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Infrastructure.Migrations;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class TelemetryIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedTelemetryMigrationAfterInitialFoundation()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0001_InitialFoundation.sql", StringComparison.Ordinal));
        Assert.Contains(sqlResources, r => r.EndsWith("0002_TelemetryVisibility.sql", StringComparison.Ordinal));

        var initialIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0001_InitialFoundation.sql", StringComparison.Ordinal));
        var telemetryIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0002_TelemetryVisibility.sql", StringComparison.Ordinal));
        Assert.True(initialIdx >= 0 && telemetryIdx > initialIdx);
    }

    [Fact]
    public async Task IngestMetricsBatch_DeduplicatesByBootIdAndSampleSequence_AndReturnsLatestTelemetry()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        using var supervisor = factory.CreateUserClient("S-1-5-21-sup-a", "Supervisor", "site-a");

        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-TEL-01", "THUMB-TEL-01");
        using var deviceClient = factory.CreateDeviceClient("THUMB-TEL-01");

        var bootId = Guid.NewGuid();
        var t0 = factory.Clock.GetUtcNow();

        // Send heartbeat so device is Online.
        var hbResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(bootId, "1.0.0", new[] { "telemetry" }, Array.Empty<ObservedSessionDto>(), "WS-TEL-01"));
        Assert.Equal(HttpStatusCode.OK, hbResp.StatusCode);

        var samples = new[]
        {
            new MetricSampleDto(bootId, 1, t0.AddSeconds(-20), 12.5, 40.0, 4_000_000_000, 10_000_000_000, 5.0, 10.0),
            new MetricSampleDto(bootId, 2, t0.AddSeconds(-10), 25.0, 45.0, 4_500_000_000, 10_000_000_000, 15.0, 20.0),
            new MetricSampleDto(bootId, 3, t0, 55.5, 50.0, 5_000_000_000, 10_000_000_000, 35.0, 30.0)
        };

        var volumes = new[]
        {
            new VolumeSnapshotDto("C:\\", "Fixed", 500_000_000_000, 200_000_000_000, t0),
            new VolumeSnapshotDto("D:\\", "Fixed", 1_000_000_000_000, 800_000_000_000, t0)
        };

        var snapshotId = Guid.NewGuid();
        var procSnapshot = new ProcessSnapshotDto(
            snapshotId,
            t0,
            new[]
            {
                new ProcessEntryDto("imager.exe", 4120, t0.AddMinutes(-5), 18.2, 512_000_000)
            });

        var batch = new MetricsBatchRequest(samples, volumes, procSnapshot);

        var firstPost = await deviceClient.PostAsJsonAsync("/api/v1/agent/metrics", batch);
        Assert.Equal(HttpStatusCode.OK, firstPost.StatusCode);
        var firstResult = await firstPost.Content.ReadFromJsonAsync<MetricsBatchResponse>();
        Assert.NotNull(firstResult);
        Assert.Equal(3, firstResult.AcceptedSamples);
        Assert.Equal(0, firstResult.DuplicateSamples);

        // Re-posting identical (BootId, SampleSequence) yields 0 accepted, 3 duplicates.
        var retryPost = await deviceClient.PostAsJsonAsync("/api/v1/agent/metrics", batch);
        Assert.Equal(HttpStatusCode.OK, retryPost.StatusCode);
        var retryResult = await retryPost.Content.ReadFromJsonAsync<MetricsBatchResponse>();
        Assert.NotNull(retryResult);
        Assert.Equal(0, retryResult.AcceptedSamples);
        Assert.Equal(3, retryResult.DuplicateSamples);

        var telemetryResp = await supervisor.GetAsync($"/api/v1/workstations/{dev.DeviceId}/telemetry?sampleLimit=60");
        Assert.Equal(HttpStatusCode.OK, telemetryResp.StatusCode);
        var telemetry = await telemetryResp.Content.ReadFromJsonAsync<DeviceTelemetryResponse>();
        Assert.NotNull(telemetry);
        Assert.Equal(dev.DeviceId, telemetry.DeviceId);
        Assert.Equal("Online", telemetry.ConnectivityState);
        Assert.Equal(3, telemetry.RecentSamples.Count);

        // Samples must be ordered newest-first.
        Assert.Equal(3, telemetry.RecentSamples[0].SampleSequence);
        Assert.Equal(2, telemetry.RecentSamples[1].SampleSequence);
        Assert.Equal(1, telemetry.RecentSamples[2].SampleSequence);
        Assert.NotNull(telemetry.LatestSample);
        Assert.Equal(3, telemetry.LatestSample.SampleSequence);
        Assert.Equal(55.5, telemetry.LatestSample.CpuPercent);

        Assert.Equal(2, telemetry.Volumes.Count);
        Assert.NotNull(telemetry.LatestProcessSnapshot);
        Assert.Equal(snapshotId, telemetry.LatestProcessSnapshot.SnapshotId);
        var proc = Assert.Single(telemetry.LatestProcessSnapshot.Processes);
        Assert.Equal("imager.exe", proc.ImageName);
    }

    [Fact]
    public async Task IngestMetricsBatch_EmptyOrOversizedBatch_Returns400()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        await EnrollDeviceAsync(factory, admin, "site-a", "WS-TEL-02", "THUMB-TEL-02");
        using var deviceClient = factory.CreateDeviceClient("THUMB-TEL-02");

        var emptyResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(Array.Empty<MetricSampleDto>(), null, null));
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);

        var bootId = Guid.NewGuid();
        var now = factory.Clock.GetUtcNow();
        var oversizedSamples = Enumerable.Range(1, 201)
            .Select(i => new MetricSampleDto(bootId, i, now.AddSeconds(i), 10.0, 20.0, 1024, 4096, null, 5.0))
            .ToArray();

        var oversizedResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(oversizedSamples, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedResp.StatusCode);
    }

    [Fact]
    public async Task IngestMetricsBatch_RevokedDevice_FailsClosed()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-TEL-REV", "THUMB-TEL-REV");
        using var deviceClient = factory.CreateDeviceClient("THUMB-TEL-REV");

        var revokeResp = await admin.PostAsJsonAsync(
            $"/api/v1/workstations/{dev.DeviceId}/revoke",
            new RevokeDeviceRequest("Revoked before telemetry"));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        var postResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(
                new[]
                {
                    new MetricSampleDto(Guid.NewGuid(), 1, factory.Clock.GetUtcNow(), 15.0, 30.0, 1000, 4000, null, 2.0)
                },
                null,
                null));

        Assert.True(postResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IngestMetricsBatch_UserCaller_IsDenied()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");

        var resp = await admin.PostAsJsonAsync(
            "/api/v1/agent/metrics",
            new MetricsBatchRequest(
                new[]
                {
                    new MetricSampleDto(Guid.NewGuid(), 1, factory.Clock.GetUtcNow(), 15.0, 30.0, 1000, 4000, null, 2.0)
                },
                null,
                null));

        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetWorkstationTelemetry_EnforcesSiteAndAssignmentScope()
    {
        await using var factory = new HiveServerFactory();
        using var adminAll = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "*");
        using var supSiteA = factory.CreateUserClient("S-1-5-21-sup-a", "Supervisor", "site-a");
        using var supSiteB = factory.CreateUserClient("S-1-5-21-sup-b", "Supervisor", "site-b");
        using var assignedExaminer = factory.CreateUserClient("S-1-5-21-ex-assigned", "Examiner", "site-a");
        using var otherExaminer = factory.CreateUserClient("S-1-5-21-ex-other", "Examiner", "site-a");

        var dev = await EnrollDeviceAsync(factory, adminAll, "site-a", "WS-TEL-SCOPE", "THUMB-TEL-SCOPE");

        // Assign workstation to assignedExaminer
        var assignResp = await supSiteA.PatchAsJsonAsync(
            $"/api/v1/workstations/{dev.DeviceId}/assignment",
            new UpdateAssignmentRequest("ASSET-TEL-SCOPE", "S-1-5-21-ex-assigned", ExpectedRowVersion: 1));
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);

        // Same-site Supervisor -> 200
        var supAResp = await supSiteA.GetAsync($"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.Equal(HttpStatusCode.OK, supAResp.StatusCode);

        // Assigned Examiner -> 200
        var exAssignedResp = await assignedExaminer.GetAsync($"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.Equal(HttpStatusCode.OK, exAssignedResp.StatusCode);

        // Cross-site Supervisor -> 403
        var supBResp = await supSiteB.GetAsync($"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.Equal(HttpStatusCode.Forbidden, supBResp.StatusCode);

        // Unassigned Examiner -> 403
        var exOtherResp = await otherExaminer.GetAsync($"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.Equal(HttpStatusCode.Forbidden, exOtherResp.StatusCode);

        // Missing workstation -> 404
        var missingResp = await supSiteA.GetAsync($"/api/v1/workstations/{Guid.NewGuid()}/telemetry");
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);
    }

    [Fact]
    public async Task IngestMetricsBatch_PreservesNullGpu_AndSanitizesProcessImageNames()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-TEL-SAN", "THUMB-TEL-SAN");
        using var deviceClient = factory.CreateDeviceClient("THUMB-TEL-SAN");

        var now = factory.Clock.GetUtcNow();
        var batch = new MetricsBatchRequest(
            new[]
            {
                new MetricSampleDto(
                    Guid.NewGuid(),
                    1,
                    now,
                    42.0,
                    60.0,
                    6_000_000_000,
                    10_000_000_000,
                    GpuPercent: null,
                    DiskActivePercent: 12.0)
            },
            null,
            new ProcessSnapshotDto(
                Guid.NewGuid(),
                now,
                new[]
                {
                    new ProcessEntryDto(@"C:\Tools\imager.exe --case 123", 9001, now.AddMinutes(-1), 22.5, 256_000_000)
                }));

        var ingestResp = await deviceClient.PostAsJsonAsync("/api/v1/agent/metrics", batch);
        Assert.Equal(HttpStatusCode.OK, ingestResp.StatusCode);

        var telemetry = await admin.GetFromJsonAsync<DeviceTelemetryResponse>(
            $"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.NotNull(telemetry);
        Assert.NotNull(telemetry.LatestSample);
        Assert.Null(telemetry.LatestSample.GpuPercent);

        Assert.NotNull(telemetry.LatestProcessSnapshot);
        var proc = Assert.Single(telemetry.LatestProcessSnapshot.Processes);
        Assert.Equal("imager.exe", proc.ImageName);
    }

    [Fact]
    public async Task GetWorkstationTelemetry_OfflineWorkstation_ReportsOfflineHonestly()
    {
        await using var factory = new HiveServerFactory();
        using var admin = factory.CreateUserClient("S-1-5-21-admin", "Administrator", "site-a");
        var dev = await EnrollDeviceAsync(factory, admin, "site-a", "WS-TEL-OFF", "THUMB-TEL-OFF");
        using var deviceClient = factory.CreateDeviceClient("THUMB-TEL-OFF");

        // Never sent heartbeat -> Offline
        var beforeHb = await admin.GetFromJsonAsync<DeviceTelemetryResponse>(
            $"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.NotNull(beforeHb);
        Assert.Equal("Offline", beforeHb.ConnectivityState);
        Assert.Null(beforeHb.LastSeenUtc);

        // Send heartbeat -> Online
        var bootId = Guid.NewGuid();
        var hbResp = await deviceClient.PostAsJsonAsync(
            "/api/v1/agent/heartbeat",
            new HeartbeatRequest(bootId, "1.0.0", new[] { "telemetry" }, Array.Empty<ObservedSessionDto>(), "WS-TEL-OFF"));
        Assert.Equal(HttpStatusCode.OK, hbResp.StatusCode);

        var online = await admin.GetFromJsonAsync<DeviceTelemetryResponse>(
            $"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.NotNull(online);
        Assert.Equal("Online", online.ConnectivityState);

        // Advance clock past stale threshold (120s) -> Offline
        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        var afterExpiry = await admin.GetFromJsonAsync<DeviceTelemetryResponse>(
            $"/api/v1/workstations/{dev.DeviceId}/telemetry");
        Assert.NotNull(afterExpiry);
        Assert.Equal("Offline", afterExpiry.ConnectivityState);
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
