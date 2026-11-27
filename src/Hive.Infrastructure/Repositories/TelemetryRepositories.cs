using System.Text.Json;
using Hive.Domain.Telemetry;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public sealed record TelemetryBatchResult(
    int AcceptedSamples,
    int DuplicateSamples);

public sealed record DeviceTelemetrySnapshot(
    IReadOnlyList<DeviceMetricSample> Samples,
    IReadOnlyList<DeviceVolume> Volumes,
    DeviceProcessSnapshot? LatestProcessSnapshot);

public interface ITelemetryRepository
{
    Task<TelemetryBatchResult> IngestBatchAsync(
        Guid deviceId,
        IReadOnlyList<DeviceMetricSample> samples,
        IReadOnlyList<DeviceVolume>? volumes,
        DeviceProcessSnapshot? processSnapshot,
        CancellationToken cancellationToken = default);

    Task<DeviceTelemetrySnapshot> GetLatestTelemetryAsync(
        Guid deviceId,
        int sampleLimit,
        CancellationToken cancellationToken = default);

    Task<(int DeletedMetricSamples, int DeletedProcessSnapshots)> PruneExpiredAsync(
        DateTimeOffset metricsCutoffUtc,
        DateTimeOffset processSnapshotsCutoffUtc,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryTelemetryRepository : ITelemetryRepository
{
    public const int MaxSamplesPerDevice = 1000;

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, DeviceTelemetryState> _byDevice = new();

    public Task<TelemetryBatchResult> IngestBatchAsync(
        Guid deviceId,
        IReadOnlyList<DeviceMetricSample> samples,
        IReadOnlyList<DeviceVolume>? volumes,
        DeviceProcessSnapshot? processSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);

        lock (_syncRoot)
        {
            if (!_byDevice.TryGetValue(deviceId, out var state))
            {
                state = new DeviceTelemetryState();
                _byDevice[deviceId] = state;
            }

            var accepted = 0;
            var duplicates = 0;

            foreach (var sample in samples)
            {
                var key = (sample.BootId, sample.SampleSequence);
                if (!state.SeenSampleKeys.Add(key))
                {
                    duplicates++;
                    continue;
                }

                state.Samples.Add(sample);
                accepted++;
            }

            if (accepted > 0)
            {
                state.Samples.Sort(static (a, b) =>
                {
                    var tsCompare = b.SampledAtUtc.CompareTo(a.SampledAtUtc);
                    return tsCompare != 0 ? tsCompare : b.SampleSequence.CompareTo(a.SampleSequence);
                });

                if (state.Samples.Count > MaxSamplesPerDevice)
                {
                    for (var i = MaxSamplesPerDevice; i < state.Samples.Count; i++)
                    {
                        var evicted = state.Samples[i];
                        state.SeenSampleKeys.Remove((evicted.BootId, evicted.SampleSequence));
                    }

                    state.Samples.RemoveRange(MaxSamplesPerDevice, state.Samples.Count - MaxSamplesPerDevice);
                }
            }

            if (volumes is not null)
            {
                foreach (var volume in volumes)
                {
                    state.VolumesById[volume.VolumeId] = volume;
                }
            }

            if (processSnapshot is not null &&
                (state.LatestProcessSnapshot is null ||
                 processSnapshot.SampledAtUtc >= state.LatestProcessSnapshot.SampledAtUtc))
            {
                state.LatestProcessSnapshot = processSnapshot;
            }

            return Task.FromResult(new TelemetryBatchResult(accepted, duplicates));
        }
    }

    public Task<DeviceTelemetrySnapshot> GetLatestTelemetryAsync(
        Guid deviceId,
        int sampleLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(sampleLimit, 1, 500);

        lock (_syncRoot)
        {
            if (!_byDevice.TryGetValue(deviceId, out var state))
            {
                return Task.FromResult(new DeviceTelemetrySnapshot(
                    Array.Empty<DeviceMetricSample>(),
                    Array.Empty<DeviceVolume>(),
                    null));
            }

            var samples = state.Samples.Take(safeLimit).ToArray();
            var volumes = state.VolumesById.Values
                .OrderBy(v => v.VolumeId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult(new DeviceTelemetrySnapshot(
                samples,
                volumes,
                state.LatestProcessSnapshot));
        }
    }

    public Task<(int DeletedMetricSamples, int DeletedProcessSnapshots)> PruneExpiredAsync(
        DateTimeOffset metricsCutoffUtc,
        DateTimeOffset processSnapshotsCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            var deletedMetricSamples = 0;
            var deletedProcessSnapshots = 0;

            foreach (var state in _byDevice.Values)
            {
                for (var i = state.Samples.Count - 1; i >= 0; i--)
                {
                    var sample = state.Samples[i];
                    if (sample.SampledAtUtc < metricsCutoffUtc)
                    {
                        state.SeenSampleKeys.Remove((sample.BootId, sample.SampleSequence));
                        state.Samples.RemoveAt(i);
                        deletedMetricSamples++;
                    }
                }

                if (state.LatestProcessSnapshot is not null &&
                    state.LatestProcessSnapshot.SampledAtUtc < processSnapshotsCutoffUtc)
                {
                    state.LatestProcessSnapshot = null;
                    deletedProcessSnapshots++;
                }
            }

            return Task.FromResult((deletedMetricSamples, deletedProcessSnapshots));
        }
    }

    private sealed class DeviceTelemetryState
    {
        public List<DeviceMetricSample> Samples { get; } = new();
        public HashSet<(Guid BootId, long SampleSequence)> SeenSampleKeys { get; } = new();
        public Dictionary<string, DeviceVolume> VolumesById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DeviceProcessSnapshot? LatestProcessSnapshot { get; set; }
    }
}

public sealed class PostgresTelemetryRepository : ITelemetryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTelemetryRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<TelemetryBatchResult> IngestBatchAsync(
        Guid deviceId,
        IReadOnlyList<DeviceMetricSample> samples,
        IReadOnlyList<DeviceVolume>? volumes,
        DeviceProcessSnapshot? processSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var accepted = 0;
        var duplicates = 0;

        foreach (var sample in samples)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO device_metrics (
                    device_id, boot_id, sample_sequence, sampled_at_utc, received_at_utc,
                    cpu_percent, memory_used_percent, memory_used_bytes, memory_total_bytes,
                    gpu_percent, disk_active_percent
                )
                VALUES (
                    @device_id, @boot_id, @sample_sequence, @sampled_at_utc, @received_at_utc,
                    @cpu_percent, @memory_used_percent, @memory_used_bytes, @memory_total_bytes,
                    @gpu_percent, @disk_active_percent
                )
                ON CONFLICT (device_id, boot_id, sample_sequence) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("device_id", deviceId);
            cmd.Parameters.AddWithValue("boot_id", sample.BootId);
            cmd.Parameters.AddWithValue("sample_sequence", sample.SampleSequence);
            cmd.Parameters.AddWithValue("sampled_at_utc", sample.SampledAtUtc);
            cmd.Parameters.AddWithValue("received_at_utc", sample.ReceivedAtUtc);
            PostgresMapping.AddNullableParameter(cmd, "cpu_percent", sample.CpuPercent);
            PostgresMapping.AddNullableParameter(cmd, "memory_used_percent", sample.MemoryUsedPercent);
            PostgresMapping.AddNullableParameter(cmd, "memory_used_bytes", sample.MemoryUsedBytes);
            PostgresMapping.AddNullableParameter(cmd, "memory_total_bytes", sample.MemoryTotalBytes);
            PostgresMapping.AddNullableParameter(cmd, "gpu_percent", sample.GpuPercent);
            PostgresMapping.AddNullableParameter(cmd, "disk_active_percent", sample.DiskActivePercent);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                accepted++;
            }
            else
            {
                duplicates++;
            }
        }

        if (volumes is { Count: > 0 })
        {
            foreach (var volume in volumes)
            {
                await using var volCmd = conn.CreateCommand();
                volCmd.Transaction = tx;
                volCmd.CommandText = """
                    INSERT INTO device_volumes (
                        device_id, volume_id, drive_type, total_bytes, free_bytes, observed_at_utc
                    )
                    VALUES (
                        @device_id, @volume_id, @drive_type, @total_bytes, @free_bytes, @observed_at_utc
                    )
                    ON CONFLICT (device_id, volume_id) DO UPDATE SET
                        drive_type = EXCLUDED.drive_type,
                        total_bytes = EXCLUDED.total_bytes,
                        free_bytes = EXCLUDED.free_bytes,
                        observed_at_utc = EXCLUDED.observed_at_utc;
                    """;
                volCmd.Parameters.AddWithValue("device_id", deviceId);
                volCmd.Parameters.AddWithValue("volume_id", volume.VolumeId);
                volCmd.Parameters.AddWithValue("drive_type", volume.DriveType);
                volCmd.Parameters.AddWithValue("total_bytes", volume.TotalBytes);
                volCmd.Parameters.AddWithValue("free_bytes", volume.FreeBytes);
                volCmd.Parameters.AddWithValue("observed_at_utc", volume.ObservedAtUtc);
                await volCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (processSnapshot is not null)
        {
            var processesJson = JsonSerializer.Serialize(processSnapshot.Processes, JsonOptions);
            await using var procCmd = conn.CreateCommand();
            procCmd.Transaction = tx;
            procCmd.CommandText = """
                INSERT INTO device_process_snapshots (
                    snapshot_id, device_id, sampled_at_utc, processes_json
                )
                VALUES (
                    @snapshot_id, @device_id, @sampled_at_utc, @processes_json::jsonb
                )
                ON CONFLICT (snapshot_id) DO NOTHING;
                """;
            procCmd.Parameters.AddWithValue("snapshot_id", processSnapshot.SnapshotId);
            procCmd.Parameters.AddWithValue("device_id", deviceId);
            procCmd.Parameters.AddWithValue("sampled_at_utc", processSnapshot.SampledAtUtc);
            procCmd.Parameters.AddWithValue("processes_json", processesJson);
            await procCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new TelemetryBatchResult(accepted, duplicates);
    }

    public async Task<DeviceTelemetrySnapshot> GetLatestTelemetryAsync(
        Guid deviceId,
        int sampleLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(sampleLimit, 1, 500);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var samples = new List<DeviceMetricSample>();
        await using (var sampleCmd = conn.CreateCommand())
        {
            sampleCmd.CommandText = """
                SELECT device_id, boot_id, sample_sequence, sampled_at_utc, received_at_utc,
                       cpu_percent, memory_used_percent, memory_used_bytes, memory_total_bytes,
                       gpu_percent, disk_active_percent
                FROM device_metrics
                WHERE device_id = @device_id
                ORDER BY sampled_at_utc DESC, sample_sequence DESC
                LIMIT @limit;
                """;
            sampleCmd.Parameters.AddWithValue("device_id", deviceId);
            sampleCmd.Parameters.AddWithValue("limit", safeLimit);

            await using var reader = await sampleCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                samples.Add(new DeviceMetricSample(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt64(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10)));
            }
        }

        var volumes = new List<DeviceVolume>();
        await using (var volumeCmd = conn.CreateCommand())
        {
            volumeCmd.CommandText = """
                SELECT device_id, volume_id, drive_type, total_bytes, free_bytes, observed_at_utc
                FROM device_volumes
                WHERE device_id = @device_id
                ORDER BY volume_id ASC;
                """;
            volumeCmd.Parameters.AddWithValue("device_id", deviceId);

            await using var reader = await volumeCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                volumes.Add(new DeviceVolume(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        DeviceProcessSnapshot? latestProcessSnapshot = null;
        await using (var procCmd = conn.CreateCommand())
        {
            procCmd.CommandText = """
                SELECT snapshot_id, device_id, sampled_at_utc, processes_json::text
                FROM device_process_snapshots
                WHERE device_id = @device_id
                ORDER BY sampled_at_utc DESC
                LIMIT 1;
                """;
            procCmd.Parameters.AddWithValue("device_id", deviceId);

            await using var reader = await procCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var json = reader.GetString(3);
                var processes = JsonSerializer.Deserialize<DeviceProcessEntry[]>(json, JsonOptions)
                    ?? Array.Empty<DeviceProcessEntry>();

                latestProcessSnapshot = new DeviceProcessSnapshot(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    processes);
            }
        }

        return new DeviceTelemetrySnapshot(samples, volumes, latestProcessSnapshot);
    }

    public async Task<(int DeletedMetricSamples, int DeletedProcessSnapshots)> PruneExpiredAsync(
        DateTimeOffset metricsCutoffUtc,
        DateTimeOffset processSnapshotsCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        int deletedMetricSamples;
        await using (var metricsCmd = conn.CreateCommand())
        {
            metricsCmd.CommandText = """
                DELETE FROM device_metrics
                WHERE sampled_at_utc < @metricsCutoff;
                """;
            metricsCmd.Parameters.AddWithValue("metricsCutoff", metricsCutoffUtc);
            deletedMetricSamples = await metricsCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        int deletedProcessSnapshots;
        await using (var procCmd = conn.CreateCommand())
        {
            procCmd.CommandText = """
                DELETE FROM device_process_snapshots
                WHERE sampled_at_utc < @processCutoff;
                """;
            procCmd.Parameters.AddWithValue("processCutoff", processSnapshotsCutoffUtc);
            deletedProcessSnapshots = await procCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return (deletedMetricSamples, deletedProcessSnapshots);
    }
}
