using Hive.Domain.RemoteAccess;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public interface IEscrowCredentialRepository
{
    Task<EscrowedCredentialRecord?> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default);

    Task<EscrowedCredentialRecord> UpsertAsync(EscrowedCredentialRecord record, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid deviceId, CancellationToken ct = default);
}

public sealed class InMemoryEscrowCredentialRepository : IEscrowCredentialRepository
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, EscrowedCredentialRecord> _recordsByDeviceId = new();

    public Task<EscrowedCredentialRecord?> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_recordsByDeviceId.TryGetValue(deviceId, out var record) ? Clone(record) : null);
        }
    }

    public Task<EscrowedCredentialRecord> UpsertAsync(EscrowedCredentialRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var copy = Clone(record);
        lock (_syncRoot)
        {
            _recordsByDeviceId[copy.DeviceId] = copy;
        }

        return Task.FromResult(Clone(copy));
    }

    public Task<bool> DeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_recordsByDeviceId.Remove(deviceId));
        }
    }

    private static EscrowedCredentialRecord Clone(EscrowedCredentialRecord record) =>
        record with
        {
            Ciphertext = record.Ciphertext.ToArray(),
            Nonce = record.Nonce.ToArray(),
            Tag = record.Tag.ToArray()
        };
}

public sealed class PostgresEscrowCredentialRepository : IEscrowCredentialRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresEscrowCredentialRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<EscrowedCredentialRecord?> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT credential_id, device_id, account_username,
                   ciphertext_aes_gcm, nonce, tag, key_version,
                   updated_by_sid, updated_at_utc
            FROM device_credential_escrow
            WHERE device_id = @device_id;
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<EscrowedCredentialRecord> UpsertAsync(EscrowedCredentialRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device_credential_escrow (
                credential_id, device_id, account_username,
                ciphertext_aes_gcm, nonce, tag, key_version,
                updated_by_sid, updated_at_utc
            )
            VALUES (
                @credential_id, @device_id, @account_username,
                @ciphertext_aes_gcm, @nonce, @tag, @key_version,
                @updated_by_sid, @updated_at_utc
            )
            ON CONFLICT (device_id) DO UPDATE SET
                credential_id = EXCLUDED.credential_id,
                account_username = EXCLUDED.account_username,
                ciphertext_aes_gcm = EXCLUDED.ciphertext_aes_gcm,
                nonce = EXCLUDED.nonce,
                tag = EXCLUDED.tag,
                key_version = EXCLUDED.key_version,
                updated_by_sid = EXCLUDED.updated_by_sid,
                updated_at_utc = EXCLUDED.updated_at_utc
            RETURNING credential_id, device_id, account_username,
                      ciphertext_aes_gcm, nonce, tag, key_version,
                      updated_by_sid, updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("credential_id", record.CredentialId);
        cmd.Parameters.AddWithValue("device_id", record.DeviceId);
        cmd.Parameters.AddWithValue("account_username", record.AccountUsername);
        cmd.Parameters.AddWithValue("ciphertext_aes_gcm", record.Ciphertext);
        cmd.Parameters.AddWithValue("nonce", record.Nonce);
        cmd.Parameters.AddWithValue("tag", record.Tag);
        cmd.Parameters.AddWithValue("key_version", record.KeyVersion);
        cmd.Parameters.AddWithValue("updated_by_sid", record.UpdatedBySid);
        cmd.Parameters.AddWithValue("updated_at_utc", record.UpdatedAtUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadRecord(reader);
    }

    public async Task<bool> DeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM device_credential_escrow WHERE device_id = @device_id;";
        cmd.Parameters.AddWithValue("device_id", deviceId);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private static EscrowedCredentialRecord ReadRecord(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8));
}
