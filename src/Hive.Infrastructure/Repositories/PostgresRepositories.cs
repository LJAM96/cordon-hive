using System.Text;
using Hive.Domain.Audit;
using Hive.Domain.Devices;
using Hive.Domain.Enrollment;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

internal static class PostgresMapping
{
    public static Device ReadDevice(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            Enum.Parse<EnrollmentState>(reader.GetString(6), ignoreCase: true),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));

    public static void AddNullableParameter(NpgsqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

public sealed class PostgresDeviceRepository : IDeviceRepository
{
    private const string DeviceColumns = """
        device_id, site_id, hostname, asset_number, assigned_user_sid,
        certificate_thumbprint, enrollment_state, enrolled_at_utc, last_seen_utc,
        boot_id, agent_version, policy_revision, row_version,
        protected_mode_active, protected_mode_reason
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresDeviceRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<Device?> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE device_id = @device_id;";
        cmd.Parameters.AddWithValue("device_id", deviceId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? PostgresMapping.ReadDevice(reader) : null;
    }

    public async Task<Device?> GetByThumbprintAsync(string certificateThumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            return null;
        }

        var normalized = RepositoryNormalization.NormalizeThumbprint(certificateThumbprint);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE certificate_thumbprint = @thumbprint;";
        cmd.Parameters.AddWithValue("thumbprint", normalized);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? PostgresMapping.ReadDevice(reader) : null;
    }

    public async Task<IReadOnlyList<Device>> ListAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        string? assignedUserSid = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder($"SELECT {DeviceColumns} FROM devices WHERE 1=1");

        if (!string.IsNullOrWhiteSpace(siteId))
        {
            sql.Append(" AND site_id = @site_id");
            cmd.Parameters.AddWithValue("site_id", siteId);
        }

        if (!allSites && allowedSiteIds is not null)
        {
            sql.Append(" AND site_id = ANY(@allowed_sites)");
            cmd.Parameters.AddWithValue("allowed_sites", allowedSiteIds.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(assignedUserSid))
        {
            sql.Append(" AND lower(assigned_user_sid) = lower(@assigned_user_sid)");
            cmd.Parameters.AddWithValue("assigned_user_sid", assignedUserSid);
        }

        sql.Append(" ORDER BY site_id ASC, hostname ASC;");
        cmd.CommandText = sql.ToString();

        var devices = new List<Device>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(PostgresMapping.ReadDevice(reader));
        }

        return devices;
    }

    public async Task<IReadOnlyList<DeviceSession>> GetSessionsAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, boot_id, session_id, user_sid, session_state, observed_at_utc
            FROM device_sessions
            WHERE device_id = @device_id
            ORDER BY session_id ASC;
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId);

        var sessions = new List<DeviceSession>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new DeviceSession(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<DeviceAssignment>> GetAssignmentHistoryAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT assignment_id, device_id, user_sid, assigned_by_sid, valid_from_utc, valid_to_utc
            FROM device_assignments
            WHERE device_id = @device_id
            ORDER BY valid_from_utc DESC;
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId);

        var history = new List<DeviceAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new DeviceAssignment(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return history;
    }

    public async Task<Device?> RecordHeartbeatAsync(
        Guid deviceId,
        Guid bootId,
        string agentVersion,
        string? hostname,
        IReadOnlyList<DeviceSession> sessions,
        DateTimeOffset seenAtUtc,
        CancellationToken cancellationToken = default,
        bool protectedModeActive = false,
        string? protectedModeReason = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var normalizedReason = protectedModeActive
            ? RepositoryNormalization.NormalizeOptional(protectedModeReason)
            : null;

        Device? updated;
        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = $"""
                UPDATE devices
                SET last_seen_utc = @last_seen_utc,
                    boot_id = @boot_id,
                    agent_version = COALESCE(@agent_version, agent_version),
                    hostname = COALESCE(@hostname, hostname),
                    protected_mode_active = @protected_mode_active,
                    protected_mode_reason = @protected_mode_reason,
                    row_version = row_version + 1
                WHERE device_id = @device_id
                  AND enrollment_state = @active_state
                RETURNING {DeviceColumns};
                """;
            updateCmd.Parameters.AddWithValue("last_seen_utc", seenAtUtc);
            updateCmd.Parameters.AddWithValue("boot_id", bootId);
            PostgresMapping.AddNullableParameter(updateCmd, "agent_version", RepositoryNormalization.NormalizeOptional(agentVersion));
            PostgresMapping.AddNullableParameter(updateCmd, "hostname", RepositoryNormalization.NormalizeOptional(hostname));
            updateCmd.Parameters.AddWithValue("protected_mode_active", protectedModeActive);
            PostgresMapping.AddNullableParameter(updateCmd, "protected_mode_reason", normalizedReason);
            updateCmd.Parameters.AddWithValue("device_id", deviceId);
            updateCmd.Parameters.AddWithValue("active_state", EnrollmentState.Active.ToString());

            await using var reader = await updateCmd.ExecuteReaderAsync(cancellationToken);
            updated = await reader.ReadAsync(cancellationToken) ? PostgresMapping.ReadDevice(reader) : null;
        }

        if (updated is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM device_sessions WHERE device_id = @device_id;";
            deleteCmd.Parameters.AddWithValue("device_id", deviceId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var session in sessions)
        {
            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO device_sessions (device_id, boot_id, session_id, user_sid, session_state, observed_at_utc)
                VALUES (@device_id, @boot_id, @session_id, @user_sid, @session_state, @observed_at_utc);
                """;
            insertCmd.Parameters.AddWithValue("device_id", deviceId);
            insertCmd.Parameters.AddWithValue("boot_id", session.BootId);
            insertCmd.Parameters.AddWithValue("session_id", session.SessionId);
            PostgresMapping.AddNullableParameter(insertCmd, "user_sid", session.UserSid);
            insertCmd.Parameters.AddWithValue("session_state", session.SessionState);
            insertCmd.Parameters.AddWithValue("observed_at_utc", session.ObservedAtUtc);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<UpdateAssignmentResult> UpdateAssignmentAsync(
        Guid deviceId,
        string? assetNumber,
        string? assignedUserSid,
        string assignedBySid,
        long expectedRowVersion,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedAsset = RepositoryNormalization.NormalizeOptional(assetNumber);
        var normalizedUser = RepositoryNormalization.NormalizeOptional(assignedUserSid);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        Device? existing = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE device_id = @device_id FOR UPDATE;";
            selectCmd.Parameters.AddWithValue("device_id", deviceId);

            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = PostgresMapping.ReadDevice(reader);
            }
        }

        if (existing is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new UpdateAssignmentResult(AssignmentUpdateStatus.NotFound, null);
        }

        if (existing.RowVersion != expectedRowVersion)
        {
            await tx.RollbackAsync(cancellationToken);
            return new UpdateAssignmentResult(AssignmentUpdateStatus.ConcurrencyConflict, existing);
        }

        if (normalizedAsset is not null)
        {
            await using var dupCmd = conn.CreateCommand();
            dupCmd.Transaction = tx;
            dupCmd.CommandText = """
                SELECT 1 FROM devices
                WHERE device_id <> @device_id AND asset_number = @asset_number
                LIMIT 1;
                """;
            dupCmd.Parameters.AddWithValue("device_id", deviceId);
            dupCmd.Parameters.AddWithValue("asset_number", normalizedAsset);
            var exists = await dupCmd.ExecuteScalarAsync(cancellationToken);
            if (exists is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return new UpdateAssignmentResult(AssignmentUpdateStatus.DuplicateAssetNumber, existing);
            }
        }

        Device updated;
        try
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = $"""
                UPDATE devices
                SET asset_number = @asset_number,
                    assigned_user_sid = @assigned_user_sid,
                    row_version = row_version + 1
                WHERE device_id = @device_id
                  AND row_version = @expected_row_version
                RETURNING {DeviceColumns};
                """;
            PostgresMapping.AddNullableParameter(updateCmd, "asset_number", normalizedAsset);
            PostgresMapping.AddNullableParameter(updateCmd, "assigned_user_sid", normalizedUser);
            updateCmd.Parameters.AddWithValue("device_id", deviceId);
            updateCmd.Parameters.AddWithValue("expected_row_version", expectedRowVersion);

            await using var reader = await updateCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return new UpdateAssignmentResult(AssignmentUpdateStatus.ConcurrencyConflict, existing);
            }

            updated = PostgresMapping.ReadDevice(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(cancellationToken);
            return new UpdateAssignmentResult(AssignmentUpdateStatus.DuplicateAssetNumber, existing);
        }

        DeviceAssignment? newAssignment = null;
        if (!string.Equals(existing.AssignedUserSid, normalizedUser, StringComparison.OrdinalIgnoreCase))
        {
            await using (var closeCmd = conn.CreateCommand())
            {
                closeCmd.Transaction = tx;
                closeCmd.CommandText = """
                    UPDATE device_assignments
                    SET valid_to_utc = @valid_to_utc
                    WHERE device_id = @device_id AND valid_to_utc IS NULL;
                    """;
                closeCmd.Parameters.AddWithValue("valid_to_utc", changedAtUtc);
                closeCmd.Parameters.AddWithValue("device_id", deviceId);
                await closeCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            newAssignment = new DeviceAssignment(
                Guid.NewGuid(),
                deviceId,
                normalizedUser,
                assignedBySid,
                changedAtUtc,
                null);

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO device_assignments (assignment_id, device_id, user_sid, assigned_by_sid, valid_from_utc, valid_to_utc)
                VALUES (@assignment_id, @device_id, @user_sid, @assigned_by_sid, @valid_from_utc, NULL);
                """;
            insertCmd.Parameters.AddWithValue("assignment_id", newAssignment.AssignmentId);
            insertCmd.Parameters.AddWithValue("device_id", newAssignment.DeviceId);
            PostgresMapping.AddNullableParameter(insertCmd, "user_sid", newAssignment.UserSid);
            insertCmd.Parameters.AddWithValue("assigned_by_sid", newAssignment.AssignedBySid);
            insertCmd.Parameters.AddWithValue("valid_from_utc", newAssignment.ValidFromUtc);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new UpdateAssignmentResult(AssignmentUpdateStatus.Success, updated, newAssignment);
    }

    public async Task<RevokeDeviceResult> RevokeAsync(
        Guid deviceId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        Device? existing = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"SELECT {DeviceColumns} FROM devices WHERE device_id = @device_id FOR UPDATE;";
            selectCmd.Parameters.AddWithValue("device_id", deviceId);

            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = PostgresMapping.ReadDevice(reader);
            }
        }

        if (existing is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new RevokeDeviceResult(RevokeDeviceStatus.NotFound, null);
        }

        if (existing.EnrollmentState is EnrollmentState.Revoked or EnrollmentState.Decommissioned)
        {
            await tx.RollbackAsync(cancellationToken);
            return new RevokeDeviceResult(RevokeDeviceStatus.AlreadyRevoked, existing);
        }

        Device updated;
        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = $"""
                UPDATE devices
                SET enrollment_state = @revoked_state,
                    row_version = row_version + 1
                WHERE device_id = @device_id
                RETURNING {DeviceColumns};
                """;
            updateCmd.Parameters.AddWithValue("revoked_state", EnrollmentState.Revoked.ToString());
            updateCmd.Parameters.AddWithValue("device_id", deviceId);

            await using var reader = await updateCmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            updated = PostgresMapping.ReadDevice(reader);
        }

        await tx.CommitAsync(cancellationToken);
        return new RevokeDeviceResult(RevokeDeviceStatus.Success, updated);
    }
}

public sealed class PostgresEnrollmentTokenRepository : IEnrollmentTokenRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresEnrollmentTokenRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task CreateAsync(EnrollmentToken token, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO enrollment_tokens (
                token_id, token_hash_sha256, site_id, created_by_sid,
                created_at_utc, expires_at_utc, redeemed_at_utc, redeemed_by_device_id
            )
            VALUES (
                @token_id, @token_hash_sha256, @site_id, @created_by_sid,
                @created_at_utc, @expires_at_utc, @redeemed_at_utc, @redeemed_by_device_id
            );
            """;
        cmd.Parameters.AddWithValue("token_id", token.TokenId);
        cmd.Parameters.AddWithValue("token_hash_sha256", token.TokenHashSha256);
        cmd.Parameters.AddWithValue("site_id", token.SiteId);
        cmd.Parameters.AddWithValue("created_by_sid", token.CreatedBySid);
        cmd.Parameters.AddWithValue("created_at_utc", token.CreatedAtUtc);
        cmd.Parameters.AddWithValue("expires_at_utc", token.ExpiresAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "redeemed_at_utc", token.RedeemedAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "redeemed_by_device_id", token.RedeemedByDeviceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EnrollmentToken?> GetByHashAsync(string tokenHashSha256, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token_id, token_hash_sha256, site_id, created_by_sid,
                   created_at_utc, expires_at_utc, redeemed_at_utc, redeemed_by_device_id
            FROM enrollment_tokens
            WHERE token_hash_sha256 = @token_hash_sha256;
            """;
        cmd.Parameters.AddWithValue("token_hash_sha256", tokenHashSha256);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EnrollmentToken(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    public async Task<EnrollDeviceResult> RedeemAndEnrollAsync(
        string tokenHashSha256,
        string hostname,
        string certificateThumbprint,
        string? assetNumber,
        string agentVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedThumbprint = RepositoryNormalization.NormalizeThumbprint(certificateThumbprint);
        var normalizedAsset = RepositoryNormalization.NormalizeOptional(assetNumber);
        var normalizedHostname = hostname.Trim();
        var normalizedAgentVersion = RepositoryNormalization.NormalizeOptional(agentVersion);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        EnrollmentToken? token = null;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT token_id, token_hash_sha256, site_id, created_by_sid,
                       created_at_utc, expires_at_utc, redeemed_at_utc, redeemed_by_device_id
                FROM enrollment_tokens
                WHERE token_hash_sha256 = @token_hash_sha256
                FOR UPDATE;
                """;
            selectCmd.Parameters.AddWithValue("token_hash_sha256", tokenHashSha256);

            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                token = new EnrollmentToken(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7));
            }
        }

        if (token is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new EnrollDeviceResult(EnrollmentStatus.InvalidOrUnknownToken, null, "Enrollment token is invalid.");
        }

        if (token.RedeemedAtUtc is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new EnrollDeviceResult(EnrollmentStatus.TokenAlreadyRedeemed, null, "Enrollment token has already been redeemed.");
        }

        if (nowUtc >= token.ExpiresAtUtc)
        {
            await tx.RollbackAsync(cancellationToken);
            return new EnrollDeviceResult(EnrollmentStatus.TokenExpired, null, "Enrollment token has expired.");
        }

        var deviceId = Guid.NewGuid();
        var device = new Device(
            deviceId,
            token.SiteId,
            normalizedHostname,
            normalizedAsset,
            null,
            normalizedThumbprint,
            EnrollmentState.Active,
            nowUtc,
            null,
            null,
            normalizedAgentVersion,
            1,
            1);

        try
        {
            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO devices (
                    device_id, site_id, hostname, asset_number, assigned_user_sid,
                    certificate_thumbprint, enrollment_state, enrolled_at_utc, last_seen_utc,
                    boot_id, agent_version, policy_revision, row_version
                )
                VALUES (
                    @device_id, @site_id, @hostname, @asset_number, NULL,
                    @certificate_thumbprint, @enrollment_state, @enrolled_at_utc, NULL,
                    NULL, @agent_version, 1, 1
                );
                """;
            insertCmd.Parameters.AddWithValue("device_id", device.DeviceId);
            insertCmd.Parameters.AddWithValue("site_id", device.SiteId);
            insertCmd.Parameters.AddWithValue("hostname", device.Hostname);
            PostgresMapping.AddNullableParameter(insertCmd, "asset_number", device.AssetNumber);
            insertCmd.Parameters.AddWithValue("certificate_thumbprint", device.CertificateThumbprint);
            insertCmd.Parameters.AddWithValue("enrollment_state", device.EnrollmentState.ToString());
            insertCmd.Parameters.AddWithValue("enrolled_at_utc", device.EnrolledAtUtc);
            PostgresMapping.AddNullableParameter(insertCmd, "agent_version", device.AgentVersion);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(cancellationToken);
            if (string.Equals(ex.ConstraintName, "ux_devices_asset_number", StringComparison.OrdinalIgnoreCase))
            {
                return new EnrollDeviceResult(EnrollmentStatus.DuplicateAssetNumber, null, "Asset number is already assigned to another device.");
            }

            return new EnrollDeviceResult(EnrollmentStatus.DuplicateCertificateThumbprint, null, "Certificate thumbprint is already enrolled.");
        }

        await using (var redeemCmd = conn.CreateCommand())
        {
            redeemCmd.Transaction = tx;
            redeemCmd.CommandText = """
                UPDATE enrollment_tokens
                SET redeemed_at_utc = @redeemed_at_utc,
                    redeemed_by_device_id = @redeemed_by_device_id
                WHERE token_id = @token_id
                  AND redeemed_at_utc IS NULL;
                """;
            redeemCmd.Parameters.AddWithValue("redeemed_at_utc", nowUtc);
            redeemCmd.Parameters.AddWithValue("redeemed_by_device_id", deviceId);
            redeemCmd.Parameters.AddWithValue("token_id", token.TokenId);
            var updated = await redeemCmd.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return new EnrollDeviceResult(EnrollmentStatus.TokenAlreadyRedeemed, null, "Enrollment token has already been redeemed.");
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new EnrollDeviceResult(EnrollmentStatus.Success, device);
    }
}

public sealed class PostgresAuditRepository : IAuditRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuditRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_events (
                event_id, actor_type, actor_id, action, object_type, object_id,
                result, site_id, correlation_id, occurred_at_utc, summary_json
            )
            VALUES (
                @event_id, @actor_type, @actor_id, @action, @object_type, @object_id,
                @result, @site_id, @correlation_id, @occurred_at_utc, @summary_json
            );
            """;
        cmd.Parameters.AddWithValue("event_id", auditEvent.EventId);
        cmd.Parameters.AddWithValue("actor_type", auditEvent.ActorType);
        cmd.Parameters.AddWithValue("actor_id", auditEvent.ActorId);
        cmd.Parameters.AddWithValue("action", auditEvent.Action);
        cmd.Parameters.AddWithValue("object_type", auditEvent.ObjectType);
        cmd.Parameters.AddWithValue("object_id", auditEvent.ObjectId);
        cmd.Parameters.AddWithValue("result", auditEvent.Result);
        PostgresMapping.AddNullableParameter(cmd, "site_id", auditEvent.SiteId);
        cmd.Parameters.AddWithValue("correlation_id", auditEvent.CorrelationId);
        cmd.Parameters.AddWithValue("occurred_at_utc", auditEvent.OccurredAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "summary_json", auditEvent.SummaryJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AuditEvent> Items, int TotalCount)> QueryAsync(
        int page,
        int pageSize,
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var where = new StringBuilder("WHERE 1=1");
        void BindFilters(NpgsqlCommand command)
        {
            if (!string.IsNullOrWhiteSpace(siteId))
            {
                command.Parameters.AddWithValue("site_id", siteId);
            }

            if (!allSites && allowedSiteIds is not null)
            {
                command.Parameters.AddWithValue("allowed_sites", allowedSiteIds.ToArray());
            }
        }

        if (!string.IsNullOrWhiteSpace(siteId))
        {
            where.Append(" AND site_id = @site_id");
        }

        if (!allSites && allowedSiteIds is not null)
        {
            where.Append(" AND site_id IS NOT NULL AND site_id = ANY(@allowed_sites)");
        }

        int totalCount;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM audit_events {where};";
            BindFilters(countCmd);
            totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<AuditEvent>();
        await using (var queryCmd = conn.CreateCommand())
        {
            queryCmd.CommandText = $"""
                SELECT event_id, actor_type, actor_id, action, object_type, object_id,
                       result, site_id, correlation_id, occurred_at_utc, summary_json
                FROM audit_events
                {where}
                ORDER BY occurred_at_utc DESC
                LIMIT @limit OFFSET @offset;
                """;
            BindFilters(queryCmd);
            queryCmd.Parameters.AddWithValue("limit", safePageSize);
            queryCmd.Parameters.AddWithValue("offset", offset);

            await using var reader = await queryCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new AuditEvent(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        return (items, totalCount);
    }
}

public sealed class PostgresOutboxRepository : IOutboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresOutboxRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task EnqueueAsync(OutboxEventRecord record, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO outbox_events (
                event_id, event_type, resource_id, revision,
                site_id, target_user_sid, occurred_at_utc, dispatched_at_utc
            )
            VALUES (
                @event_id, @event_type, @resource_id, @revision,
                @site_id, @target_user_sid, @occurred_at_utc, @dispatched_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("event_id", record.EventId);
        cmd.Parameters.AddWithValue("event_type", record.EventType);
        cmd.Parameters.AddWithValue("resource_id", record.ResourceId);
        cmd.Parameters.AddWithValue("revision", record.Revision);
        PostgresMapping.AddNullableParameter(cmd, "site_id", record.SiteId);
        PostgresMapping.AddNullableParameter(cmd, "target_user_sid", record.TargetUserSid);
        cmd.Parameters.AddWithValue("occurred_at_utc", record.OccurredAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "dispatched_at_utc", record.DispatchedAtUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxEventRecord>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, event_type, resource_id, revision,
                   site_id, target_user_sid, occurred_at_utc, dispatched_at_utc
            FROM outbox_events
            WHERE dispatched_at_utc IS NULL
            ORDER BY occurred_at_utc ASC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("limit", safeLimit);

        var items = new List<OutboxEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OutboxEventRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return items;
    }

    public async Task MarkDispatchedAsync(Guid eventId, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE outbox_events
            SET dispatched_at_utc = @dispatched_at_utc
            WHERE event_id = @event_id;
            """;
        cmd.Parameters.AddWithValue("dispatched_at_utc", dispatchedAtUtc);
        cmd.Parameters.AddWithValue("event_id", eventId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
