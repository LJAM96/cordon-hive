using Hive.Domain.Devices;
using Hive.Infrastructure.Sqlite;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class SqliteAgentLocalStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteAgentLocalStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CordonHiveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "agent-test.db");
    }

    [Fact]
    public async Task DeviceStateAndSyncCursor_PersistAcrossReopen()
    {
        var deviceId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 11, 27, 13, 0, 0, TimeSpan.Zero);

        await using (var store1 = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 10))
        {
            await store1.InitializeAsync();
            await store1.SaveDeviceStateAsync(new AgentDeviceState(
                DeviceId: deviceId,
                SiteId: "site-lab-01",
                CertificateThumbprint: "AABBCCDD11223344",
                EnrollmentState: EnrollmentState.Active.ToString(),
                PolicyRevision: 42,
                LastHeartbeatUtc: updatedAt,
                ProtectedModeActive: true,
                UpdatedAtUtc: updatedAt));
            await store1.SaveSyncCursorAsync("devices", "cursor-123", updatedAt);
        }

        // Re-open with a fresh store instance pointing to the same SQLite file
        await using var store2 = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 10);
        await store2.InitializeAsync();
        var loaded = await store2.GetDeviceStateAsync();
        var cursor = await store2.GetSyncCursorAsync("devices");

        Assert.NotNull(loaded);
        Assert.Equal(deviceId, loaded!.DeviceId);
        Assert.Equal("site-lab-01", loaded.SiteId);
        Assert.Equal("AABBCCDD11223344", loaded.CertificateThumbprint);
        Assert.Equal(EnrollmentState.Active.ToString(), loaded.EnrollmentState);
        Assert.Equal(42, loaded.PolicyRevision);
        Assert.Equal(updatedAt, loaded.LastHeartbeatUtc);
        Assert.True(loaded.ProtectedModeActive);
        Assert.Equal(updatedAt, loaded.UpdatedAtUtc);
        Assert.Equal("cursor-123", cursor);
    }

    [Fact]
    public async Task OutboundQueue_EnforcesMaxQueueItemsCapacity_AndPreservesCriticalItems()
    {
        await using var store = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 3);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(await store.EnqueueOutboundAsync("audit", "{\"seq\":1}", isCritical: true, createdAtUtc: now));
        Assert.NotNull(await store.EnqueueOutboundAsync("telemetry_batch", "{\"seq\":2}", isCritical: false, createdAtUtc: now.AddSeconds(1)));
        Assert.NotNull(await store.EnqueueOutboundAsync("audit", "{\"seq\":3}", isCritical: true, createdAtUtc: now.AddSeconds(2)));
        Assert.Equal(3, await store.GetOutboundCountAsync());

        // Adding another critical item when queue has 1 non-critical item must evict the non-critical item, not critical ones.
        Assert.NotNull(await store.EnqueueOutboundAsync("audit", "{\"seq\":4}", isCritical: true, createdAtUtc: now.AddSeconds(3)));
        Assert.Equal(3, await store.GetOutboundCountAsync());

        var itemsAfterEviction = await store.GetPendingOutboundAsync(10);
        Assert.All(itemsAfterEviction, item => Assert.True(item.IsCritical));

        // When queue is full of critical items, an incoming non-critical item is dropped (returns null).
        var droppedId = await store.EnqueueOutboundAsync("telemetry_batch", "{\"seq\":5}", isCritical: false, createdAtUtc: now.AddSeconds(4));
        Assert.Null(droppedId);
        Assert.Equal(3, await store.GetOutboundCountAsync());

        // Deleting an item frees space for new non-critical items.
        await store.RemoveOutboundBatchAsync([itemsAfterEviction[0].Id]);
        Assert.Equal(2, await store.GetOutboundCountAsync());
        Assert.NotNull(await store.EnqueueOutboundAsync("telemetry_batch", "{\"seq\":6}", isCritical: false, createdAtUtc: now.AddSeconds(5)));
        Assert.Equal(3, await store.GetOutboundCountAsync());
    }

    [Fact]
    public async Task OutboundQueue_CoalesceKey_ReplacesPriorEntry()
    {
        await using var store = AgentLocalStore.FromFilePath(_dbPath, maxQueueItems: 10);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        await store.EnqueueOutboundAsync("telemetry_snapshot", "{\"v\":1}", isCritical: false, coalesceKey: "latest-telemetry", createdAtUtc: now);
        await store.EnqueueOutboundAsync("telemetry_snapshot", "{\"v\":2}", isCritical: false, coalesceKey: "latest-telemetry", createdAtUtc: now.AddSeconds(1));

        var pending = await store.GetPendingOutboundAsync(10);
        Assert.Single(pending);
        Assert.Equal("latest-telemetry", pending[0].CoalesceKey);
        Assert.Equal("{\"v\":2}", pending[0].PayloadJson);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup locks in temp folder
        }
    }
}
