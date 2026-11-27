using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hive.Infrastructure.Sqlite;

public sealed record AgentDeviceState(
    Guid? DeviceId,
    string? SiteId,
    string? CertificateThumbprint,
    string EnrollmentState,
    long PolicyRevision,
    DateTimeOffset? LastHeartbeatUtc,
    bool ProtectedModeActive,
    DateTimeOffset UpdatedAtUtc);

public sealed record OutboundQueueItem(
    long Id,
    string ItemType,
    string? CoalesceKey,
    bool IsCritical,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int RetryCount);

public interface IAgentLocalStore : IAsyncDisposable
{
    int MaxQueueItems { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AgentDeviceState?> GetDeviceStateAsync(CancellationToken cancellationToken = default);

    Task SaveDeviceStateAsync(AgentDeviceState state, CancellationToken cancellationToken = default);

    Task UpdateEnrollmentStateAsync(
        string enrollmentState,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    Task<long?> EnqueueOutboundAsync(
        string itemType,
        string payloadJson,
        bool isCritical = false,
        string? coalesceKey = null,
        DateTimeOffset? createdAtUtc = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboundQueueItem>> GetPendingOutboundAsync(int limit = 50, CancellationToken cancellationToken = default);

    Task RemoveOutboundAsync(long id, CancellationToken cancellationToken = default);

    Task RemoveOutboundBatchAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default);

    Task<int> GetOutboundCountAsync(CancellationToken cancellationToken = default);

    Task<string?> GetSyncCursorAsync(string streamName, CancellationToken cancellationToken = default);

    Task SaveSyncCursorAsync(
        string streamName,
        string cursorValue,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable SQLite store for Windows Agent local machine state, bounded outbound queue, and sync cursors.
/// </summary>
public sealed class AgentLocalStore : IAgentLocalStore
{
    private readonly string _connectionString;
    private readonly int _maxQueueItems;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AgentLocalStore(string connectionString, int maxQueueItems = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (maxQueueItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueueItems), "MaxQueueItems must be positive.");
        }

        _connectionString = connectionString;
        _maxQueueItems = maxQueueItems;
    }

    public static AgentLocalStore FromFilePath(string databasePath, int maxQueueItems = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        return new AgentLocalStore(builder.ToString(), maxQueueItems);
    }

    public int MaxQueueItems => _maxQueueItems;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);

            if (!_connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS device_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    device_id TEXT NULL,
                    site_id TEXT NULL,
                    certificate_thumbprint TEXT NULL,
                    enrollment_state TEXT NOT NULL,
                    policy_revision INTEGER NOT NULL,
                    last_heartbeat_utc TEXT NULL,
                    protected_mode_active INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS outbound_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    item_type TEXT NOT NULL,
                    coalesce_key TEXT NULL,
                    is_critical INTEGER NOT NULL DEFAULT 0,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    retry_count INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_outbound_queue_coalesce_key
                    ON outbound_queue (coalesce_key)
                    WHERE coalesce_key IS NOT NULL;

                CREATE TABLE IF NOT EXISTS sync_cursor (
                    stream_name TEXT PRIMARY KEY,
                    cursor_value TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentDeviceState?> GetDeviceStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT device_id, site_id, certificate_thumbprint, enrollment_state,
                       policy_revision, last_heartbeat_utc, protected_mode_active, updated_at_utc
                FROM device_state
                WHERE id = 1;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new AgentDeviceState(
                reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
                reader.GetInt64(6) != 0,
                ParseIso(reader.GetString(7)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveDeviceStateAsync(AgentDeviceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await SaveDeviceStateInternalAsync(conn, state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateEnrollmentStateAsync(
        string enrollmentState,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentState);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            AgentDeviceState? existing = null;

            await using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.CommandText = """
                    SELECT device_id, site_id, certificate_thumbprint, enrollment_state,
                           policy_revision, last_heartbeat_utc, protected_mode_active, updated_at_utc
                    FROM device_state
                    WHERE id = 1;
                    """;

                await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    existing = new AgentDeviceState(
                        reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
                        reader.GetInt64(6) != 0,
                        ParseIso(reader.GetString(7)));
                }
            }

            var updated = existing is not null
                ? existing with { EnrollmentState = enrollmentState.Trim(), UpdatedAtUtc = updatedAtUtc }
                : new AgentDeviceState(
                    DeviceId: null,
                    SiteId: null,
                    CertificateThumbprint: null,
                    EnrollmentState: enrollmentState.Trim(),
                    PolicyRevision: 0,
                    LastHeartbeatUtc: null,
                    ProtectedModeActive: false,
                    UpdatedAtUtc: updatedAtUtc);

            await SaveDeviceStateInternalAsync(conn, updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SaveDeviceStateInternalAsync(
        SqliteConnection conn,
        AgentDeviceState state,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device_state (
                id, device_id, site_id, certificate_thumbprint, enrollment_state,
                policy_revision, last_heartbeat_utc, protected_mode_active, updated_at_utc
            )
            VALUES (
                1, @device_id, @site_id, @certificate_thumbprint, @enrollment_state,
                @policy_revision, @last_heartbeat_utc, @protected_mode_active, @updated_at_utc
            )
            ON CONFLICT (id) DO UPDATE SET
                device_id = excluded.device_id,
                site_id = excluded.site_id,
                certificate_thumbprint = excluded.certificate_thumbprint,
                enrollment_state = excluded.enrollment_state,
                policy_revision = excluded.policy_revision,
                last_heartbeat_utc = excluded.last_heartbeat_utc,
                protected_mode_active = excluded.protected_mode_active,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("@device_id", (object?)state.DeviceId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@site_id", (object?)state.SiteId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@certificate_thumbprint", (object?)state.CertificateThumbprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enrollment_state", state.EnrollmentState);
        cmd.Parameters.AddWithValue("@policy_revision", state.PolicyRevision);
        cmd.Parameters.AddWithValue("@last_heartbeat_utc", state.LastHeartbeatUtc is { } hb ? FormatIso(hb) : DBNull.Value);
        cmd.Parameters.AddWithValue("@protected_mode_active", state.ProtectedModeActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@updated_at_utc", FormatIso(state.UpdatedAtUtc));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> EnqueueOutboundAsync(
        string itemType,
        string payloadJson,
        bool isCritical = false,
        string? coalesceKey = null,
        DateTimeOffset? createdAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var timestamp = createdAtUtc ?? DateTimeOffset.UtcNow;
        var normalizedKey = string.IsNullOrWhiteSpace(coalesceKey) ? null : coalesceKey.Trim();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

            if (normalizedKey is not null)
            {
                await using var coalesceCmd = conn.CreateCommand();
                coalesceCmd.Transaction = tx;
                coalesceCmd.CommandText = "DELETE FROM outbound_queue WHERE coalesce_key = @coalesce_key;";
                coalesceCmd.Parameters.AddWithValue("@coalesce_key", normalizedKey);
                await coalesceCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var currentCount = await GetCountInternalAsync(conn, tx, cancellationToken);
            while (currentCount >= _maxQueueItems)
            {
                var deleted = await DeleteOldestAsync(conn, tx, nonCriticalOnly: true, cancellationToken);
                if (!deleted)
                {
                    if (!isCritical)
                    {
                        // Queue is full of critical items; drop incoming non-critical item to preserve critical entries.
                        await tx.CommitAsync(cancellationToken);
                        return null;
                    }

                    deleted = await DeleteOldestAsync(conn, tx, nonCriticalOnly: false, cancellationToken);
                    if (!deleted)
                    {
                        break;
                    }
                }

                currentCount--;
            }

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO outbound_queue (item_type, coalesce_key, is_critical, payload_json, created_at_utc, retry_count)
                VALUES (@item_type, @coalesce_key, @is_critical, @payload_json, @created_at_utc, 0);
                SELECT last_insert_rowid();
                """;
            insertCmd.Parameters.AddWithValue("@item_type", itemType.Trim());
            insertCmd.Parameters.AddWithValue("@coalesce_key", (object?)normalizedKey ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@is_critical", isCritical ? 1 : 0);
            insertCmd.Parameters.AddWithValue("@payload_json", payloadJson);
            insertCmd.Parameters.AddWithValue("@created_at_utc", FormatIso(timestamp));

            var insertedId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync(cancellationToken));
            await tx.CommitAsync(cancellationToken);
            return insertedId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboundQueueItem>> GetPendingOutboundAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, item_type, coalesce_key, is_critical, payload_json, created_at_utc, retry_count
                FROM outbound_queue
                ORDER BY is_critical DESC, id ASC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", safeLimit);

            var items = new List<OutboundQueueItem>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new OutboundQueueItem(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.GetString(4),
                    ParseIso(reader.GetString(5)),
                    reader.GetInt32(6)));
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveOutboundAsync(long id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM outbound_queue WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveOutboundBatchAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinctIds = ids.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM outbound_queue WHERE id = @id;";
            var idParam = cmd.Parameters.Add("@id", SqliteType.Integer);

            foreach (var id in distinctIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetOutboundCountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            return await GetCountInternalAsync(conn, null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSyncCursorAsync(string streamName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT cursor_value FROM sync_cursor WHERE stream_name = @stream_name;";
            cmd.Parameters.AddWithValue("@stream_name", streamName.Trim());
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSyncCursorAsync(
        string streamName,
        string cursorValue,
        DateTimeOffset? updatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(cursorValue);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_cursor (stream_name, cursor_value, updated_at_utc)
                VALUES (@stream_name, @cursor_value, @updated_at_utc)
                ON CONFLICT (stream_name) DO UPDATE SET
                    cursor_value = excluded.cursor_value,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("@stream_name", streamName.Trim());
            cmd.Parameters.AddWithValue("@cursor_value", cursorValue);
            cmd.Parameters.AddWithValue("@updated_at_utc", FormatIso(updatedAtUtc ?? DateTimeOffset.UtcNow));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    private static async Task<int> GetCountInternalAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM outbound_queue;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> DeleteOldestAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        bool nonCriticalOnly,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = nonCriticalOnly
            ? """
              DELETE FROM outbound_queue
              WHERE id = (
                  SELECT id FROM outbound_queue
                  WHERE is_critical = 0
                  ORDER BY created_at_utc ASC, id ASC
                  LIMIT 1
              );
              """
            : """
              DELETE FROM outbound_queue
              WHERE id = (
                  SELECT id FROM outbound_queue
                  ORDER BY created_at_utc ASC, id ASC
                  LIMIT 1
              );
              """;
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    private static string FormatIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
