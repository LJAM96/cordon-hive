using System.Text.Json;
using Hive.Domain.Identity;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public interface IUserAccountRepository
{
    Task<int> CountAsync(CancellationToken ct = default);

    Task<UserAccount?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default);

    Task<bool> CreateAsync(UserAccount user, CancellationToken ct = default);

    Task<UserAccount?> UpdateAsync(
        Guid userId,
        string? displayName,
        IReadOnlySet<HiveRole>? roles,
        IReadOnlySet<string>? allowedSiteIds,
        bool? allSitesAccess,
        bool? isActive,
        string? newPasswordHash,
        bool? mustChangePassword,
        DateTimeOffset updatedAtUtc,
        CancellationToken ct = default);

    Task RecordLoginSuccessAsync(Guid userId, DateTimeOffset atUtc, CancellationToken ct = default);

    Task<UserAccount?> RecordLoginFailureAsync(
        Guid userId,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        DateTimeOffset atUtc,
        CancellationToken ct = default);

    Task CreateRefreshTokenAsync(UserRefreshToken token, CancellationToken ct = default);

    Task<UserRefreshToken?> GetRefreshTokenByHashAsync(string tokenHashSha256, CancellationToken ct = default);

    Task<bool> RevokeRefreshTokenAsync(string tokenHashSha256, DateTimeOffset revokedAtUtc, CancellationToken ct = default);

    Task RevokeAllUserRefreshTokensAsync(Guid userId, DateTimeOffset revokedAtUtc, CancellationToken ct = default);
}

internal static class UserAccountSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeRoles(IReadOnlySet<HiveRole> roles) =>
        JsonSerializer.Serialize(roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(), JsonOptions);

    public static string SerializeSites(IReadOnlySet<string> siteIds) =>
        JsonSerializer.Serialize(siteIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);

    public static IReadOnlySet<HiveRole> DeserializeRoles(string json)
    {
        var raw = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
        var result = new HashSet<HiveRole>();
        foreach (var item in raw)
        {
            if (Enum.TryParse<HiveRole>(item, ignoreCase: true, out var role))
            {
                result.Add(role);
            }
        }

        return result;
    }

    public static IReadOnlySet<string> DeserializeSites(string json)
    {
        var raw = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
        return raw
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<HiveRole> CloneRoles(IReadOnlySet<HiveRole> roles) =>
        new HashSet<HiveRole>(roles);

    public static IReadOnlySet<string> CloneSites(IReadOnlySet<string> siteIds) =>
        new HashSet<string>(siteIds, StringComparer.OrdinalIgnoreCase);
}

public sealed class InMemoryUserAccountRepository : IUserAccountRepository
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, UserAccount> _usersById = new();
    private readonly Dictionary<string, Guid> _userIdsByNormalizedUsername = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UserRefreshToken> _refreshTokensByHash = new(StringComparer.Ordinal);

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_usersById.Count);
        }
    }

    public Task<UserAccount?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _usersById.TryGetValue(userId, out var user);
            return Task.FromResult(user);
        }
    }

    public Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult<UserAccount?>(null);
        }

        var normalized = UserAccount.NormalizeUsername(username);
        lock (_syncRoot)
        {
            if (_userIdsByNormalizedUsername.TryGetValue(normalized, out var userId) &&
                _usersById.TryGetValue(userId, out var user))
            {
                return Task.FromResult<UserAccount?>(user);
            }

            return Task.FromResult<UserAccount?>(null);
        }
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            IReadOnlyList<UserAccount> users = _usersById.Values
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(users);
        }
    }

    public Task<bool> CreateAsync(UserAccount user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var normalized = UserAccount.NormalizeUsername(user.Username);

        lock (_syncRoot)
        {
            if (_userIdsByNormalizedUsername.ContainsKey(normalized) || _usersById.ContainsKey(user.UserId))
            {
                return Task.FromResult(false);
            }

            var stored = user with
            {
                Username = user.Username.Trim(),
                UsernameNormalized = normalized,
                DisplayName = user.DisplayName.Trim(),
                Roles = UserAccountSerialization.CloneRoles(user.Roles),
                AllowedSiteIds = UserAccountSerialization.CloneSites(user.AllowedSiteIds)
            };

            _usersById[stored.UserId] = stored;
            _userIdsByNormalizedUsername[normalized] = stored.UserId;
            return Task.FromResult(true);
        }
    }

    public Task<UserAccount?> UpdateAsync(
        Guid userId,
        string? displayName,
        IReadOnlySet<HiveRole>? roles,
        IReadOnlySet<string>? allowedSiteIds,
        bool? allSitesAccess,
        bool? isActive,
        string? newPasswordHash,
        bool? mustChangePassword,
        DateTimeOffset updatedAtUtc,
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            if (!_usersById.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserAccount?>(null);
            }

            var updated = existing with
            {
                DisplayName = displayName is not null ? displayName.Trim() : existing.DisplayName,
                Roles = roles is not null ? UserAccountSerialization.CloneRoles(roles) : existing.Roles,
                AllowedSiteIds = allowedSiteIds is not null ? UserAccountSerialization.CloneSites(allowedSiteIds) : existing.AllowedSiteIds,
                AllSitesAccess = allSitesAccess ?? existing.AllSitesAccess,
                IsActive = isActive ?? existing.IsActive,
                PasswordHash = newPasswordHash ?? existing.PasswordHash,
                MustChangePassword = mustChangePassword ?? existing.MustChangePassword,
                UpdatedAtUtc = updatedAtUtc
            };

            _usersById[userId] = updated;
            return Task.FromResult<UserAccount?>(updated);
        }
    }

    public Task RecordLoginSuccessAsync(Guid userId, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            if (_usersById.TryGetValue(userId, out var existing))
            {
                _usersById[userId] = existing with
                {
                    FailedLoginCount = 0,
                    LockedUntilUtc = null,
                    UpdatedAtUtc = atUtc
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task<UserAccount?> RecordLoginFailureAsync(
        Guid userId,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            if (!_usersById.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<UserAccount?>(null);
            }

            var baseCount = existing.LockedUntilUtc is not null && atUtc >= existing.LockedUntilUtc.Value
                ? 0
                : existing.FailedLoginCount;
            var newCount = baseCount + 1;
            DateTimeOffset? lockedUntil = newCount >= maxFailedAttempts
                ? atUtc.Add(lockoutDuration)
                : null;

            var updated = existing with
            {
                FailedLoginCount = newCount,
                LockedUntilUtc = lockedUntil,
                UpdatedAtUtc = atUtc
            };

            _usersById[userId] = updated;
            return Task.FromResult<UserAccount?>(updated);
        }
    }

    public Task CreateRefreshTokenAsync(UserRefreshToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_syncRoot)
        {
            _refreshTokensByHash[token.TokenHashSha256] = token;
        }

        return Task.CompletedTask;
    }

    public Task<UserRefreshToken?> GetRefreshTokenByHashAsync(string tokenHashSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHashSha256))
        {
            return Task.FromResult<UserRefreshToken?>(null);
        }

        lock (_syncRoot)
        {
            _refreshTokensByHash.TryGetValue(tokenHashSha256, out var token);
            return Task.FromResult(token);
        }
    }

    public Task<bool> RevokeRefreshTokenAsync(string tokenHashSha256, DateTimeOffset revokedAtUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHashSha256))
        {
            return Task.FromResult(false);
        }

        lock (_syncRoot)
        {
            if (!_refreshTokensByHash.TryGetValue(tokenHashSha256, out var token) || token.RevokedAtUtc is not null)
            {
                return Task.FromResult(false);
            }

            _refreshTokensByHash[tokenHashSha256] = token with { RevokedAtUtc = revokedAtUtc };
            return Task.FromResult(true);
        }
    }

    public Task RevokeAllUserRefreshTokensAsync(Guid userId, DateTimeOffset revokedAtUtc, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            foreach (var key in _refreshTokensByHash.Keys.ToArray())
            {
                var token = _refreshTokensByHash[key];
                if (token.UserId == userId && token.RevokedAtUtc is null)
                {
                    _refreshTokensByHash[key] = token with { RevokedAtUtc = revokedAtUtc };
                }
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class PostgresUserAccountRepository : IUserAccountRepository
{
    private const string UserColumns = """
        user_id, username, username_normalized, display_name, password_hash,
        roles_json, allowed_sites_json, all_sites_access, is_active, must_change_password,
        failed_login_count, locked_until_utc, created_at_utc, updated_at_utc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserAccountRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<UserAccount?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM users WHERE user_id = @user_id;";
        cmd.Parameters.AddWithValue("user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var normalized = UserAccount.NormalizeUsername(username);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM users WHERE username_normalized = @username_normalized;";
        cmd.Parameters.AddWithValue("username_normalized", normalized);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM users ORDER BY username_normalized ASC;";

        var users = new List<UserAccount>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public async Task<bool> CreateAsync(UserAccount user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var normalized = UserAccount.NormalizeUsername(user.Username);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (
                user_id, username, username_normalized, display_name, password_hash,
                roles_json, allowed_sites_json, all_sites_access, is_active, must_change_password,
                failed_login_count, locked_until_utc, created_at_utc, updated_at_utc
            )
            VALUES (
                @user_id, @username, @username_normalized, @display_name, @password_hash,
                @roles_json::jsonb, @allowed_sites_json::jsonb, @all_sites_access, @is_active, @must_change_password,
                @failed_login_count, @locked_until_utc, @created_at_utc, @updated_at_utc
            )
            ON CONFLICT (username_normalized) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("user_id", user.UserId);
        cmd.Parameters.AddWithValue("username", user.Username.Trim());
        cmd.Parameters.AddWithValue("username_normalized", normalized);
        cmd.Parameters.AddWithValue("display_name", user.DisplayName.Trim());
        cmd.Parameters.AddWithValue("password_hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("roles_json", UserAccountSerialization.SerializeRoles(user.Roles));
        cmd.Parameters.AddWithValue("allowed_sites_json", UserAccountSerialization.SerializeSites(user.AllowedSiteIds));
        cmd.Parameters.AddWithValue("all_sites_access", user.AllSitesAccess);
        cmd.Parameters.AddWithValue("is_active", user.IsActive);
        cmd.Parameters.AddWithValue("must_change_password", user.MustChangePassword);
        cmd.Parameters.AddWithValue("failed_login_count", user.FailedLoginCount);
        PostgresMapping.AddNullableParameter(cmd, "locked_until_utc", user.LockedUntilUtc);
        cmd.Parameters.AddWithValue("created_at_utc", user.CreatedAtUtc);
        cmd.Parameters.AddWithValue("updated_at_utc", user.UpdatedAtUtc);

        try
        {
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<UserAccount?> UpdateAsync(
        Guid userId,
        string? displayName,
        IReadOnlySet<HiveRole>? roles,
        IReadOnlySet<string>? allowedSiteIds,
        bool? allSitesAccess,
        bool? isActive,
        string? newPasswordHash,
        bool? mustChangePassword,
        DateTimeOffset updatedAtUtc,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE users
            SET display_name = COALESCE(@display_name, display_name),
                roles_json = COALESCE(@roles_json::jsonb, roles_json),
                allowed_sites_json = COALESCE(@allowed_sites_json::jsonb, allowed_sites_json),
                all_sites_access = COALESCE(@all_sites_access, all_sites_access),
                is_active = COALESCE(@is_active, is_active),
                password_hash = COALESCE(@password_hash, password_hash),
                must_change_password = COALESCE(@must_change_password, must_change_password),
                updated_at_utc = @updated_at_utc
            WHERE user_id = @user_id
            RETURNING {UserColumns};
            """;
        cmd.Parameters.AddWithValue("user_id", userId);
        PostgresMapping.AddNullableParameter(cmd, "display_name", displayName?.Trim());
        PostgresMapping.AddNullableParameter(cmd, "roles_json", roles is null ? null : UserAccountSerialization.SerializeRoles(roles));
        PostgresMapping.AddNullableParameter(cmd, "allowed_sites_json", allowedSiteIds is null ? null : UserAccountSerialization.SerializeSites(allowedSiteIds));
        PostgresMapping.AddNullableParameter(cmd, "all_sites_access", allSitesAccess);
        PostgresMapping.AddNullableParameter(cmd, "is_active", isActive);
        PostgresMapping.AddNullableParameter(cmd, "password_hash", newPasswordHash);
        PostgresMapping.AddNullableParameter(cmd, "must_change_password", mustChangePassword);
        cmd.Parameters.AddWithValue("updated_at_utc", updatedAtUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task RecordLoginSuccessAsync(Guid userId, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET failed_login_count = 0,
                locked_until_utc = NULL,
                updated_at_utc = @at_utc
            WHERE user_id = @user_id;
            """;
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("at_utc", atUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UserAccount?> RecordLoginFailureAsync(
        Guid userId,
        int maxFailedAttempts,
        TimeSpan lockoutDuration,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE users
            SET failed_login_count = CASE
                    WHEN locked_until_utc IS NOT NULL AND @at_utc >= locked_until_utc THEN 1
                    ELSE failed_login_count + 1
                END,
                locked_until_utc = CASE
                    WHEN (CASE WHEN locked_until_utc IS NOT NULL AND @at_utc >= locked_until_utc THEN 1 ELSE failed_login_count + 1 END) >= @max_failed_attempts
                        THEN @locked_until_utc
                    ELSE NULL
                END,
                updated_at_utc = @at_utc
            WHERE user_id = @user_id
            RETURNING {UserColumns};
            """;
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("at_utc", atUtc);
        cmd.Parameters.AddWithValue("max_failed_attempts", maxFailedAttempts);
        cmd.Parameters.AddWithValue("locked_until_utc", atUtc.Add(lockoutDuration));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task CreateRefreshTokenAsync(UserRefreshToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_refresh_tokens (
                token_id, user_id, token_hash_sha256, client_label,
                issued_at_utc, expires_at_utc, revoked_at_utc
            )
            VALUES (
                @token_id, @user_id, @token_hash_sha256, @client_label,
                @issued_at_utc, @expires_at_utc, @revoked_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("token_id", token.TokenId);
        cmd.Parameters.AddWithValue("user_id", token.UserId);
        cmd.Parameters.AddWithValue("token_hash_sha256", token.TokenHashSha256);
        PostgresMapping.AddNullableParameter(cmd, "client_label", token.ClientLabel);
        cmd.Parameters.AddWithValue("issued_at_utc", token.IssuedAtUtc);
        cmd.Parameters.AddWithValue("expires_at_utc", token.ExpiresAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "revoked_at_utc", token.RevokedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UserRefreshToken?> GetRefreshTokenByHashAsync(string tokenHashSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHashSha256))
        {
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token_id, user_id, token_hash_sha256, client_label,
                   issued_at_utc, expires_at_utc, revoked_at_utc
            FROM user_refresh_tokens
            WHERE token_hash_sha256 = @token_hash_sha256;
            """;
        cmd.Parameters.AddWithValue("token_hash_sha256", tokenHashSha256);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new UserRefreshToken(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<bool> RevokeRefreshTokenAsync(string tokenHashSha256, DateTimeOffset revokedAtUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHashSha256))
        {
            return false;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE user_refresh_tokens
            SET revoked_at_utc = @revoked_at_utc
            WHERE token_hash_sha256 = @token_hash_sha256
              AND revoked_at_utc IS NULL;
            """;
        cmd.Parameters.AddWithValue("token_hash_sha256", tokenHashSha256);
        cmd.Parameters.AddWithValue("revoked_at_utc", revokedAtUtc);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task RevokeAllUserRefreshTokensAsync(Guid userId, DateTimeOffset revokedAtUtc, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE user_refresh_tokens
            SET revoked_at_utc = @revoked_at_utc
            WHERE user_id = @user_id
              AND revoked_at_utc IS NULL;
            """;
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("revoked_at_utc", revokedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static UserAccount ReadUser(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            UserAccountSerialization.DeserializeRoles(reader.GetString(5)),
            UserAccountSerialization.DeserializeSites(reader.GetString(6)),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetFieldValue<DateTimeOffset>(13));
}
