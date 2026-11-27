using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Software;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public sealed record DeviceInventoryRecord(
    Guid DeviceId,
    long DetectionRevision,
    bool IsComplete,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<DeviceSoftwareInstallation> Items);

public interface ISoftwareRepository
{
    Task<IReadOnlyList<SoftwareProduct>> ListProductsAsync(CancellationToken cancellationToken = default);

    Task<SoftwareProduct?> GetProductByIdAsync(Guid productId, CancellationToken cancellationToken = default);

    Task CreateProductAsync(SoftwareProduct product, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SoftwareRelease>> ListReleasesAsync(Guid? productId = null, CancellationToken cancellationToken = default);

    Task<SoftwareRelease?> GetReleaseByIdAsync(Guid releaseId, CancellationToken cancellationToken = default);

    Task CreateReleaseAsync(SoftwareRelease release, CancellationToken cancellationToken = default);

    Task<SoftwareRelease?> ApproveReleaseAsync(
        Guid releaseId,
        string approvedBySid,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default);

    Task<SoftwareRelease?> WithdrawReleaseAsync(
        Guid releaseId,
        string reason,
        DateTimeOffset withdrawnAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SoftwareExemption>> ListExemptionsAsync(
        Guid? productId = null,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default);

    Task<SoftwareExemption?> GetExemptionByIdAsync(Guid exemptionId, CancellationToken cancellationToken = default);

    Task CreateExemptionAsync(SoftwareExemption exemption, CancellationToken cancellationToken = default);

    Task<SoftwareExemption?> RevokeExemptionAsync(
        Guid exemptionId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default);

    Task UpsertDeviceInventoryAsync(DeviceInventoryRecord record, CancellationToken cancellationToken = default);

    Task<DeviceInventoryRecord?> GetDeviceInventoryAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceInventoryRecord>> ListDeviceInventoriesAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default);
}

internal static class SoftwareSerialization
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeProtectedProcessNames(IReadOnlyList<string> names) =>
        JsonSerializer.Serialize(names, JsonOptions);

    public static IReadOnlyList<string> DeserializeProtectedProcessNames(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();

    public static string SerializeInventoryItems(IReadOnlyList<DeviceSoftwareInstallation> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    public static IReadOnlyList<DeviceSoftwareInstallation> DeserializeInventoryItems(string json) =>
        JsonSerializer.Deserialize<DeviceSoftwareInstallation[]>(json, JsonOptions) ?? Array.Empty<DeviceSoftwareInstallation>();
}

public sealed class InMemorySoftwareRepository : ISoftwareRepository
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, SoftwareProduct> _products = new();
    private readonly Dictionary<Guid, SoftwareRelease> _releases = new();
    private readonly Dictionary<Guid, SoftwareExemption> _exemptions = new();
    private readonly Dictionary<Guid, DeviceInventoryRecord> _inventories = new();

    public Task<IReadOnlyList<SoftwareProduct>> ListProductsAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            IReadOnlyList<SoftwareProduct> result = _products.Values
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.ProductId)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<SoftwareProduct?> GetProductByIdAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            _products.TryGetValue(productId, out var product);
            return Task.FromResult(product);
        }
    }

    public Task CreateProductAsync(SoftwareProduct product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var cloned = product with
        {
            ProtectedProcessNames = product.ProtectedProcessNames.ToArray()
        };

        lock (_syncRoot)
        {
            _products[cloned.ProductId] = cloned;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SoftwareRelease>> ListReleasesAsync(Guid? productId = null, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            IEnumerable<SoftwareRelease> query = _releases.Values;
            if (productId.HasValue)
            {
                query = query.Where(r => r.ProductId == productId.Value);
            }

            IReadOnlyList<SoftwareRelease> result = query
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenBy(r => r.ReleaseId)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<SoftwareRelease?> GetReleaseByIdAsync(Guid releaseId, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            _releases.TryGetValue(releaseId, out var release);
            return Task.FromResult(release);
        }
    }

    public Task CreateReleaseAsync(SoftwareRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        lock (_syncRoot)
        {
            _releases[release.ReleaseId] = release;
        }

        return Task.CompletedTask;
    }

    public Task<SoftwareRelease?> ApproveReleaseAsync(
        Guid releaseId,
        string approvedBySid,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_releases.TryGetValue(releaseId, out var existing) ||
                existing.ApprovalState != ReleaseApprovalState.Draft)
            {
                return Task.FromResult<SoftwareRelease?>(null);
            }

            var updated = existing with
            {
                ApprovalState = ReleaseApprovalState.Approved,
                ApprovedBySid = approvedBySid,
                ApprovedAtUtc = approvedAtUtc
            };

            _releases[releaseId] = updated;
            return Task.FromResult<SoftwareRelease?>(updated);
        }
    }

    public Task<SoftwareRelease?> WithdrawReleaseAsync(
        Guid releaseId,
        string reason,
        DateTimeOffset withdrawnAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_releases.TryGetValue(releaseId, out var existing) ||
                existing.ApprovalState == ReleaseApprovalState.Withdrawn)
            {
                return Task.FromResult<SoftwareRelease?>(null);
            }

            var updated = existing with
            {
                ApprovalState = ReleaseApprovalState.Withdrawn,
                WithdrawnAtUtc = withdrawnAtUtc,
                WithdrawnReason = reason
            };

            _releases[releaseId] = updated;
            return Task.FromResult<SoftwareRelease?>(updated);
        }
    }

    public Task<IReadOnlyList<SoftwareExemption>> ListExemptionsAsync(
        Guid? productId = null,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            IEnumerable<SoftwareExemption> query = _exemptions.Values;
            if (productId.HasValue)
            {
                query = query.Where(e => e.ProductId == productId.Value);
            }

            if (deviceId.HasValue)
            {
                query = query.Where(e => e.DeviceId == deviceId.Value);
            }

            IReadOnlyList<SoftwareExemption> result = query
                .OrderByDescending(e => e.StartsAtUtc)
                .ThenBy(e => e.ExemptionId)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<SoftwareExemption?> GetExemptionByIdAsync(Guid exemptionId, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            _exemptions.TryGetValue(exemptionId, out var exemption);
            return Task.FromResult(exemption);
        }
    }

    public Task CreateExemptionAsync(SoftwareExemption exemption, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exemption);

        lock (_syncRoot)
        {
            _exemptions[exemption.ExemptionId] = exemption;
        }

        return Task.CompletedTask;
    }

    public Task<SoftwareExemption?> RevokeExemptionAsync(
        Guid exemptionId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (!_exemptions.TryGetValue(exemptionId, out var existing) ||
                existing.RevokedAtUtc is not null)
            {
                return Task.FromResult<SoftwareExemption?>(null);
            }

            var updated = existing with
            {
                RevokedAtUtc = revokedAtUtc,
                RevokedBySid = revokedBySid,
                RevokedReason = reason
            };

            _exemptions[exemptionId] = updated;
            return Task.FromResult<SoftwareExemption?>(updated);
        }
    }

    public Task UpsertDeviceInventoryAsync(DeviceInventoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var cloned = record with
        {
            Items = record.Items.ToArray()
        };

        lock (_syncRoot)
        {
            _inventories[cloned.DeviceId] = cloned;
        }

        return Task.CompletedTask;
    }

    public Task<DeviceInventoryRecord?> GetDeviceInventoryAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            _inventories.TryGetValue(deviceId, out var record);
            return Task.FromResult(record);
        }
    }

    public Task<IReadOnlyList<DeviceInventoryRecord>> ListDeviceInventoriesAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DeviceInventoryRecord>>(Array.Empty<DeviceInventoryRecord>());
        }

        lock (_syncRoot)
        {
            var result = new List<DeviceInventoryRecord>(deviceIds.Count);
            foreach (var id in deviceIds.Distinct())
            {
                if (_inventories.TryGetValue(id, out var record))
                {
                    result.Add(record);
                }
            }

            return Task.FromResult<IReadOnlyList<DeviceInventoryRecord>>(result);
        }
    }
}

public sealed class PostgresSoftwareRepository : ISoftwareRepository
{
    private const string ProductColumns = """
        product_id, name, publisher, version_scheme, match_pattern,
        detection_revision, protected_process_names_json::text, created_at_utc
        """;

    private const string ReleaseColumns = """
        release_id, product_id, version_raw, version_scheme, approval_state,
        validation_reference, package_sha256, manifest_digest, created_by_sid,
        approved_by_sid, created_at_utc, approved_at_utc, withdrawn_at_utc, withdrawn_reason
        """;

    private const string ExemptionColumns = """
        exemption_id, device_id, product_id, allowed_version_raw, reason,
        approved_by_sid, starts_at_utc, expires_at_utc, revoked_at_utc,
        revoked_by_sid, revoked_reason
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSoftwareRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IReadOnlyList<SoftwareProduct>> ListProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ProductColumns} FROM software_products ORDER BY name ASC, product_id ASC;";

        var products = new List<SoftwareProduct>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    public async Task<SoftwareProduct?> GetProductByIdAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ProductColumns} FROM software_products WHERE product_id = @product_id;";
        cmd.Parameters.AddWithValue("product_id", productId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    public async Task CreateProductAsync(SoftwareProduct product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO software_products (
                product_id, name, publisher, version_scheme, match_pattern,
                detection_revision, protected_process_names_json, created_at_utc
            )
            VALUES (
                @product_id, @name, @publisher, @version_scheme, @match_pattern,
                @detection_revision, @protected_process_names_json::jsonb, @created_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("product_id", product.ProductId);
        cmd.Parameters.AddWithValue("name", product.Name);
        cmd.Parameters.AddWithValue("publisher", product.Publisher);
        cmd.Parameters.AddWithValue("version_scheme", product.VersionScheme.ToString());
        cmd.Parameters.AddWithValue("match_pattern", product.MatchPattern);
        cmd.Parameters.AddWithValue("detection_revision", product.DetectionRevision);
        cmd.Parameters.AddWithValue("protected_process_names_json", SoftwareSerialization.SerializeProtectedProcessNames(product.ProtectedProcessNames));
        cmd.Parameters.AddWithValue("created_at_utc", product.CreatedAtUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SoftwareRelease>> ListReleasesAsync(Guid? productId = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder($"SELECT {ReleaseColumns} FROM software_releases WHERE 1=1");
        if (productId.HasValue)
        {
            sql.Append(" AND product_id = @product_id");
            cmd.Parameters.AddWithValue("product_id", productId.Value);
        }

        sql.Append(" ORDER BY created_at_utc DESC, release_id ASC;");
        cmd.CommandText = sql.ToString();

        var releases = new List<SoftwareRelease>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            releases.Add(ReadRelease(reader));
        }

        return releases;
    }

    public async Task<SoftwareRelease?> GetReleaseByIdAsync(Guid releaseId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ReleaseColumns} FROM software_releases WHERE release_id = @release_id;";
        cmd.Parameters.AddWithValue("release_id", releaseId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRelease(reader) : null;
    }

    public async Task CreateReleaseAsync(SoftwareRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO software_releases (
                release_id, product_id, version_raw, version_scheme, approval_state,
                validation_reference, package_sha256, manifest_digest, created_by_sid,
                approved_by_sid, created_at_utc, approved_at_utc, withdrawn_at_utc, withdrawn_reason
            )
            VALUES (
                @release_id, @product_id, @version_raw, @version_scheme, @approval_state,
                @validation_reference, @package_sha256, @manifest_digest, @created_by_sid,
                @approved_by_sid, @created_at_utc, @approved_at_utc, @withdrawn_at_utc, @withdrawn_reason
            );
            """;
        cmd.Parameters.AddWithValue("release_id", release.ReleaseId);
        cmd.Parameters.AddWithValue("product_id", release.ProductId);
        cmd.Parameters.AddWithValue("version_raw", release.VersionRaw);
        cmd.Parameters.AddWithValue("version_scheme", release.VersionScheme.ToString());
        cmd.Parameters.AddWithValue("approval_state", release.ApprovalState.ToString());
        cmd.Parameters.AddWithValue("validation_reference", release.ValidationReference);
        PostgresMapping.AddNullableParameter(cmd, "package_sha256", release.PackageSha256);
        PostgresMapping.AddNullableParameter(cmd, "manifest_digest", release.ManifestDigest);
        cmd.Parameters.AddWithValue("created_by_sid", release.CreatedBySid);
        PostgresMapping.AddNullableParameter(cmd, "approved_by_sid", release.ApprovedBySid);
        cmd.Parameters.AddWithValue("created_at_utc", release.CreatedAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "approved_at_utc", release.ApprovedAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "withdrawn_at_utc", release.WithdrawnAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "withdrawn_reason", release.WithdrawnReason);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SoftwareRelease?> ApproveReleaseAsync(
        Guid releaseId,
        string approvedBySid,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE software_releases
            SET approval_state = @approved_state,
                approved_by_sid = @approved_by_sid,
                approved_at_utc = @approved_at_utc
            WHERE release_id = @release_id
              AND approval_state = @draft_state
            RETURNING {ReleaseColumns};
            """;
        cmd.Parameters.AddWithValue("release_id", releaseId);
        cmd.Parameters.AddWithValue("approved_state", ReleaseApprovalState.Approved.ToString());
        cmd.Parameters.AddWithValue("draft_state", ReleaseApprovalState.Draft.ToString());
        cmd.Parameters.AddWithValue("approved_by_sid", approvedBySid);
        cmd.Parameters.AddWithValue("approved_at_utc", approvedAtUtc);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRelease(reader) : null;
    }

    public async Task<SoftwareRelease?> WithdrawReleaseAsync(
        Guid releaseId,
        string reason,
        DateTimeOffset withdrawnAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE software_releases
            SET approval_state = @withdrawn_state,
                withdrawn_at_utc = @withdrawn_at_utc,
                withdrawn_reason = @withdrawn_reason
            WHERE release_id = @release_id
              AND approval_state <> @withdrawn_state
            RETURNING {ReleaseColumns};
            """;
        cmd.Parameters.AddWithValue("release_id", releaseId);
        cmd.Parameters.AddWithValue("withdrawn_state", ReleaseApprovalState.Withdrawn.ToString());
        cmd.Parameters.AddWithValue("withdrawn_at_utc", withdrawnAtUtc);
        cmd.Parameters.AddWithValue("withdrawn_reason", reason);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRelease(reader) : null;
    }

    public async Task<IReadOnlyList<SoftwareExemption>> ListExemptionsAsync(
        Guid? productId = null,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder($"SELECT {ExemptionColumns} FROM software_exemptions WHERE 1=1");
        if (productId.HasValue)
        {
            sql.Append(" AND product_id = @product_id");
            cmd.Parameters.AddWithValue("product_id", productId.Value);
        }

        if (deviceId.HasValue)
        {
            sql.Append(" AND device_id = @device_id");
            cmd.Parameters.AddWithValue("device_id", deviceId.Value);
        }

        sql.Append(" ORDER BY starts_at_utc DESC, exemption_id ASC;");
        cmd.CommandText = sql.ToString();

        var exemptions = new List<SoftwareExemption>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            exemptions.Add(ReadExemption(reader));
        }

        return exemptions;
    }

    public async Task<SoftwareExemption?> GetExemptionByIdAsync(Guid exemptionId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ExemptionColumns} FROM software_exemptions WHERE exemption_id = @exemption_id;";
        cmd.Parameters.AddWithValue("exemption_id", exemptionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExemption(reader) : null;
    }

    public async Task CreateExemptionAsync(SoftwareExemption exemption, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exemption);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO software_exemptions (
                exemption_id, device_id, product_id, allowed_version_raw, reason,
                approved_by_sid, starts_at_utc, expires_at_utc, revoked_at_utc,
                revoked_by_sid, revoked_reason
            )
            VALUES (
                @exemption_id, @device_id, @product_id, @allowed_version_raw, @reason,
                @approved_by_sid, @starts_at_utc, @expires_at_utc, @revoked_at_utc,
                @revoked_by_sid, @revoked_reason
            );
            """;
        cmd.Parameters.AddWithValue("exemption_id", exemption.ExemptionId);
        PostgresMapping.AddNullableParameter(cmd, "device_id", exemption.DeviceId);
        cmd.Parameters.AddWithValue("product_id", exemption.ProductId);
        PostgresMapping.AddNullableParameter(cmd, "allowed_version_raw", exemption.AllowedVersionRaw);
        cmd.Parameters.AddWithValue("reason", exemption.Reason);
        cmd.Parameters.AddWithValue("approved_by_sid", exemption.ApprovedBySid);
        cmd.Parameters.AddWithValue("starts_at_utc", exemption.StartsAtUtc);
        cmd.Parameters.AddWithValue("expires_at_utc", exemption.ExpiresAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "revoked_at_utc", exemption.RevokedAtUtc);
        PostgresMapping.AddNullableParameter(cmd, "revoked_by_sid", exemption.RevokedBySid);
        PostgresMapping.AddNullableParameter(cmd, "revoked_reason", exemption.RevokedReason);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SoftwareExemption?> RevokeExemptionAsync(
        Guid exemptionId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE software_exemptions
            SET revoked_at_utc = @revoked_at_utc,
                revoked_by_sid = @revoked_by_sid,
                revoked_reason = @revoked_reason
            WHERE exemption_id = @exemption_id
              AND revoked_at_utc IS NULL
            RETURNING {ExemptionColumns};
            """;
        cmd.Parameters.AddWithValue("exemption_id", exemptionId);
        cmd.Parameters.AddWithValue("revoked_at_utc", revokedAtUtc);
        cmd.Parameters.AddWithValue("revoked_by_sid", revokedBySid);
        cmd.Parameters.AddWithValue("revoked_reason", reason);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExemption(reader) : null;
    }

    public async Task UpsertDeviceInventoryAsync(DeviceInventoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device_software_snapshots (
                device_id, detection_revision, is_complete, captured_at_utc, received_at_utc, items_json
            )
            VALUES (
                @device_id, @detection_revision, @is_complete, @captured_at_utc, @received_at_utc, @items_json::jsonb
            )
            ON CONFLICT (device_id) DO UPDATE SET
                detection_revision = EXCLUDED.detection_revision,
                is_complete = EXCLUDED.is_complete,
                captured_at_utc = EXCLUDED.captured_at_utc,
                received_at_utc = EXCLUDED.received_at_utc,
                items_json = EXCLUDED.items_json;
            """;
        cmd.Parameters.AddWithValue("device_id", record.DeviceId);
        cmd.Parameters.AddWithValue("detection_revision", record.DetectionRevision);
        cmd.Parameters.AddWithValue("is_complete", record.IsComplete);
        cmd.Parameters.AddWithValue("captured_at_utc", record.CapturedAtUtc);
        cmd.Parameters.AddWithValue("received_at_utc", record.ReceivedAtUtc);
        cmd.Parameters.AddWithValue("items_json", SoftwareSerialization.SerializeInventoryItems(record.Items));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceInventoryRecord?> GetDeviceInventoryAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, detection_revision, is_complete, captured_at_utc, received_at_utc, items_json::text
            FROM device_software_snapshots
            WHERE device_id = @device_id;
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInventory(reader) : null;
    }

    public async Task<IReadOnlyList<DeviceInventoryRecord>> ListDeviceInventoriesAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return Array.Empty<DeviceInventoryRecord>();
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, detection_revision, is_complete, captured_at_utc, received_at_utc, items_json::text
            FROM device_software_snapshots
            WHERE device_id = ANY(@device_ids);
            """;
        cmd.Parameters.AddWithValue("device_ids", deviceIds.Distinct().ToArray());

        var result = new List<DeviceInventoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadInventory(reader));
        }

        return result;
    }

    private static SoftwareProduct ReadProduct(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<VersionScheme>(reader.GetString(3), ignoreCase: true),
            reader.GetString(4),
            reader.GetInt64(5),
            SoftwareSerialization.DeserializeProtectedProcessNames(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7));

    private static SoftwareRelease ReadRelease(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            Enum.Parse<VersionScheme>(reader.GetString(3), ignoreCase: true),
            Enum.Parse<ReleaseApprovalState>(reader.GetString(4), ignoreCase: true),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private static SoftwareExemption ReadExemption(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    private static DeviceInventoryRecord ReadInventory(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            SoftwareSerialization.DeserializeInventoryItems(reader.GetString(5)));
}
