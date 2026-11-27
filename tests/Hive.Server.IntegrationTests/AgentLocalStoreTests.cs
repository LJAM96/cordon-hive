using Hive.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class AgentLocalStoreTests
{
    [Fact]
    public async Task DeviceState_AndSyncCursor_RoundTripInSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"hive-agent-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new AgentLocalStore($"Data Source={dbPath};Pooling=False", maxQueueItems: 5);
            await store.InitializeAsync();

            Assert.Null(await store.GetDeviceStateAsync());

            var deviceId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 11, 27, 13, 0, 0, TimeSpan.Zero);
            var state = new AgentDeviceState(
                deviceId,
                "site-a",
                "THUMB-01",
                "Active",
                3,
                now,
                true,
                now);

            await store.SaveDeviceStateAsync(state);

            var loaded = await store.GetDeviceStateAsync();
            Assert.NotNull(loaded);
            Assert.Equal(deviceId, loaded.DeviceId);
            Assert.Equal("site-a", loaded.SiteId);
            Assert.Equal("THUMB-01", loaded.CertificateThumbprint);
            Assert.Equal("Active", loaded.EnrollmentState);
            Assert.Equal(3, loaded.PolicyRevision);
            Assert.True(loaded.ProtectedModeActive);

            await store.SaveSyncCursorAsync("notifications", "rev-42", now);
            Assert.Equal("rev-42", await store.GetSyncCursorAsync("notifications"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task OutboundQueue_EnforcesCoalesceAndDropOldestNonCriticalPolicy()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"hive-queue-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new AgentLocalStore($"Data Source={dbPath};Pooling=False", maxQueueItems: 3);
            await store.InitializeAsync();

            var t0 = new DateTimeOffset(2026, 11, 27, 13, 0, 0, TimeSpan.Zero);

            // Coalesce key replaces previous item with the same key
            await store.EnqueueOutboundAsync("heartbeat", "{\"seq\":1}", isCritical: false, coalesceKey: "hb", createdAtUtc: t0);
            await store.EnqueueOutboundAsync("heartbeat", "{\"seq\":2}", isCritical: false, coalesceKey: "hb", createdAtUtc: t0.AddSeconds(1));
            Assert.Equal(1, await store.GetOutboundCountAsync());

            // Add 1 critical + 1 non-critical -> queue at capacity 3
            await store.EnqueueOutboundAsync("audit", "{\"crit\":1}", isCritical: true, coalesceKey: null, createdAtUtc: t0.AddSeconds(2));
            await store.EnqueueOutboundAsync("telemetry", "{\"tele\":1}", isCritical: false, coalesceKey: null, createdAtUtc: t0.AddSeconds(3));
            Assert.Equal(3, await store.GetOutboundCountAsync());

            // Adding another item drops oldest non-critical ("hb" seq 2 at t0+1s), keeping critical item
            await store.EnqueueOutboundAsync("telemetry", "{\"tele\":2}", isCritical: false, coalesceKey: null, createdAtUtc: t0.AddSeconds(4));
            Assert.Equal(3, await store.GetOutboundCountAsync());

            var pending = await store.GetPendingOutboundAsync(10);
            Assert.Contains(pending, p => p.IsCritical && p.PayloadJson == "{\"crit\":1}");
            Assert.DoesNotContain(pending, p => p.PayloadJson == "{\"seq\":2}");
            Assert.Contains(pending, p => p.PayloadJson == "{\"tele\":2}");

            await store.RemoveOutboundAsync(pending[0].Id);
            Assert.Equal(2, await store.GetOutboundCountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(dbPath);
        }
    }

    private static void DeleteIfExists(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
