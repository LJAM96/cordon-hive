using Hive.Domain.Audit;
using Hive.Domain.Deployments;
using Hive.Domain.Devices;
using Hive.Domain.Enrollment;

namespace Hive.Infrastructure.Repositories;

internal static class RepositoryNormalization
{
    public static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Trim().ToUpperInvariant();

    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Shared thread-safe state store for in-memory repository implementations.
/// </summary>
public sealed class InMemoryHiveStore
{
    internal readonly object SyncRoot = new();
    internal readonly Dictionary<Guid, Device> Devices = new();
    internal readonly Dictionary<Guid, List<DeviceAssignment>> Assignments = new();
    internal readonly Dictionary<Guid, List<DeviceSession>> Sessions = new();
    internal readonly Dictionary<string, EnrollmentToken> TokensByHash = new(StringComparer.Ordinal);
    internal readonly List<AuditEvent> AuditEvents = new();
    internal readonly List<OutboxEventRecord> OutboxEvents = new();
    internal readonly Dictionary<Guid, PackageArtifact> Packages = new();
    internal readonly Dictionary<Guid, SignedManifestEnvelope> SignedManifests = new();
    internal readonly Dictionary<Guid, DeploymentJob> DeploymentJobs = new();
    internal readonly Dictionary<Guid, DeploymentTarget> DeploymentTargets = new();
    internal readonly Dictionary<Guid, List<DeploymentAttempt>> DeploymentAttempts = new();
}

public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly InMemoryHiveStore _store;

    public InMemoryDeviceRepository(InMemoryHiveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<Device?> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.Devices.TryGetValue(deviceId, out var device);
            return Task.FromResult(device);
        }
    }

    public Task<Device?> GetByThumbprintAsync(string certificateThumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            return Task.FromResult<Device?>(null);
        }

        var normalized = RepositoryNormalization.NormalizeThumbprint(certificateThumbprint);
        lock (_store.SyncRoot)
        {
            var device = _store.Devices.Values.FirstOrDefault(d =>
                string.Equals(d.CertificateThumbprint, normalized, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(device);
        }
    }

    public Task<IReadOnlyList<Device>> ListAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        string? assignedUserSid = null,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            IEnumerable<Device> query = _store.Devices.Values;

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                query = query.Where(d => string.Equals(d.SiteId, siteId, StringComparison.OrdinalIgnoreCase));
            }

            if (!allSites && allowedSiteIds is not null)
            {
                query = query.Where(d => allowedSiteIds.Contains(d.SiteId));
            }

            if (!string.IsNullOrWhiteSpace(assignedUserSid))
            {
                query = query.Where(d =>
                    !string.IsNullOrWhiteSpace(d.AssignedUserSid) &&
                    string.Equals(d.AssignedUserSid, assignedUserSid, StringComparison.OrdinalIgnoreCase));
            }

            IReadOnlyList<Device> result = query
                .OrderBy(d => d.SiteId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<DeviceSession>> GetSessionsAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (_store.Sessions.TryGetValue(deviceId, out var sessions))
            {
                return Task.FromResult<IReadOnlyList<DeviceSession>>(sessions.ToList());
            }

            return Task.FromResult<IReadOnlyList<DeviceSession>>(Array.Empty<DeviceSession>());
        }
    }

    public Task<IReadOnlyList<DeviceAssignment>> GetAssignmentHistoryAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (_store.Assignments.TryGetValue(deviceId, out var history))
            {
                IReadOnlyList<DeviceAssignment> ordered = history
                    .OrderByDescending(a => a.ValidFromUtc)
                    .ToList();
                return Task.FromResult(ordered);
            }

            return Task.FromResult<IReadOnlyList<DeviceAssignment>>(Array.Empty<DeviceAssignment>());
        }
    }

    public Task<Device?> RecordHeartbeatAsync(
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
        lock (_store.SyncRoot)
        {
            if (!_store.Devices.TryGetValue(deviceId, out var existing))
            {
                return Task.FromResult<Device?>(null);
            }

            if (existing.EnrollmentState != EnrollmentState.Active)
            {
                return Task.FromResult<Device?>(null);
            }

            var normalizedReason = protectedModeActive
                ? RepositoryNormalization.NormalizeOptional(protectedModeReason)
                : null;

            var updated = existing with
            {
                LastSeenUtc = seenAtUtc,
                BootId = bootId,
                AgentVersion = string.IsNullOrWhiteSpace(agentVersion) ? existing.AgentVersion : agentVersion.Trim(),
                Hostname = string.IsNullOrWhiteSpace(hostname) ? existing.Hostname : hostname.Trim(),
                ProtectedModeActive = protectedModeActive,
                ProtectedModeReason = normalizedReason,
                RowVersion = existing.RowVersion + 1
            };

            _store.Devices[deviceId] = updated;
            _store.Sessions[deviceId] = sessions.ToList();
            return Task.FromResult<Device?>(updated);
        }
    }

    public Task<UpdateAssignmentResult> UpdateAssignmentAsync(
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

        lock (_store.SyncRoot)
        {
            if (!_store.Devices.TryGetValue(deviceId, out var existing))
            {
                return Task.FromResult(new UpdateAssignmentResult(AssignmentUpdateStatus.NotFound, null));
            }

            if (existing.RowVersion != expectedRowVersion)
            {
                return Task.FromResult(new UpdateAssignmentResult(AssignmentUpdateStatus.ConcurrencyConflict, existing));
            }

            if (normalizedAsset is not null)
            {
                var duplicate = _store.Devices.Values.Any(d =>
                    d.DeviceId != deviceId &&
                    string.Equals(d.AssetNumber, normalizedAsset, StringComparison.Ordinal));

                if (duplicate)
                {
                    return Task.FromResult(new UpdateAssignmentResult(AssignmentUpdateStatus.DuplicateAssetNumber, existing));
                }
            }

            DeviceAssignment? newAssignment = null;
            if (!string.Equals(existing.AssignedUserSid, normalizedUser, StringComparison.OrdinalIgnoreCase))
            {
                if (!_store.Assignments.TryGetValue(deviceId, out var history))
                {
                    history = new List<DeviceAssignment>();
                    _store.Assignments[deviceId] = history;
                }

                for (var i = 0; i < history.Count; i++)
                {
                    if (history[i].ValidToUtc is null)
                    {
                        history[i] = history[i] with { ValidToUtc = changedAtUtc };
                    }
                }

                newAssignment = new DeviceAssignment(
                    Guid.NewGuid(),
                    deviceId,
                    normalizedUser,
                    assignedBySid,
                    changedAtUtc,
                    null);
                history.Add(newAssignment);
            }

            var updated = existing with
            {
                AssetNumber = normalizedAsset,
                AssignedUserSid = normalizedUser,
                RowVersion = existing.RowVersion + 1
            };

            _store.Devices[deviceId] = updated;
            return Task.FromResult(new UpdateAssignmentResult(AssignmentUpdateStatus.Success, updated, newAssignment));
        }
    }

    public Task<RevokeDeviceResult> RevokeAsync(
        Guid deviceId,
        string revokedBySid,
        string reason,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (!_store.Devices.TryGetValue(deviceId, out var existing))
            {
                return Task.FromResult(new RevokeDeviceResult(RevokeDeviceStatus.NotFound, null));
            }

            if (existing.EnrollmentState is EnrollmentState.Revoked or EnrollmentState.Decommissioned)
            {
                return Task.FromResult(new RevokeDeviceResult(RevokeDeviceStatus.AlreadyRevoked, existing));
            }

            var updated = existing with
            {
                EnrollmentState = EnrollmentState.Revoked,
                RowVersion = existing.RowVersion + 1
            };

            _store.Devices[deviceId] = updated;
            return Task.FromResult(new RevokeDeviceResult(RevokeDeviceStatus.Success, updated));
        }
    }
}

public sealed class InMemoryEnrollmentTokenRepository : IEnrollmentTokenRepository
{
    private readonly InMemoryHiveStore _store;

    public InMemoryEnrollmentTokenRepository(InMemoryHiveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task CreateAsync(EnrollmentToken token, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.TokensByHash[token.TokenHashSha256] = token;
        }

        return Task.CompletedTask;
    }

    public Task<EnrollmentToken?> GetByHashAsync(string tokenHashSha256, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.TokensByHash.TryGetValue(tokenHashSha256, out var token);
            return Task.FromResult(token);
        }
    }

    public Task<EnrollDeviceResult> RedeemAndEnrollAsync(
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

        lock (_store.SyncRoot)
        {
            if (!_store.TokensByHash.TryGetValue(tokenHashSha256, out var token))
            {
                return Task.FromResult(new EnrollDeviceResult(
                    EnrollmentStatus.InvalidOrUnknownToken,
                    null,
                    "Enrollment token is invalid."));
            }

            if (token.RedeemedAtUtc is not null)
            {
                return Task.FromResult(new EnrollDeviceResult(
                    EnrollmentStatus.TokenAlreadyRedeemed,
                    null,
                    "Enrollment token has already been redeemed."));
            }

            if (nowUtc >= token.ExpiresAtUtc)
            {
                return Task.FromResult(new EnrollDeviceResult(
                    EnrollmentStatus.TokenExpired,
                    null,
                    "Enrollment token has expired."));
            }

            if (_store.Devices.Values.Any(d =>
                    string.Equals(d.CertificateThumbprint, normalizedThumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new EnrollDeviceResult(
                    EnrollmentStatus.DuplicateCertificateThumbprint,
                    null,
                    "Certificate thumbprint is already enrolled."));
            }

            if (normalizedAsset is not null &&
                _store.Devices.Values.Any(d => string.Equals(d.AssetNumber, normalizedAsset, StringComparison.Ordinal)))
            {
                return Task.FromResult(new EnrollDeviceResult(
                    EnrollmentStatus.DuplicateAssetNumber,
                    null,
                    "Asset number is already assigned to another device."));
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
                RepositoryNormalization.NormalizeOptional(agentVersion),
                PolicyRevision: 1,
                RowVersion: 1);

            _store.Devices[deviceId] = device;
            _store.Assignments[deviceId] = new List<DeviceAssignment>();
            _store.Sessions[deviceId] = new List<DeviceSession>();
            _store.TokensByHash[tokenHashSha256] = token with
            {
                RedeemedAtUtc = nowUtc,
                RedeemedByDeviceId = deviceId
            };

            return Task.FromResult(new EnrollDeviceResult(EnrollmentStatus.Success, device));
        }
    }
}

public sealed class InMemoryAuditRepository : IAuditRepository
{
    private readonly InMemoryHiveStore _store;

    public InMemoryAuditRepository(InMemoryHiveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.AuditEvents.Add(auditEvent);
        }

        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<AuditEvent> Items, int TotalCount)> QueryAsync(
        int page,
        int pageSize,
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        string? action = null,
        string? actorId = null,
        string? result = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);

        lock (_store.SyncRoot)
        {
            IEnumerable<AuditEvent> query = _store.AuditEvents;

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                var trimmedSite = siteId.Trim();
                query = query.Where(e => string.Equals(e.SiteId, trimmedSite, StringComparison.OrdinalIgnoreCase));
            }

            if (!allSites && allowedSiteIds is not null)
            {
                query = query.Where(e => !string.IsNullOrWhiteSpace(e.SiteId) && allowedSiteIds.Contains(e.SiteId));
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                var trimmedAction = action.Trim();
                query = query.Where(e => string.Equals(e.Action, trimmedAction, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(actorId))
            {
                var trimmedActorId = actorId.Trim();
                query = query.Where(e => string.Equals(e.ActorId, trimmedActorId, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                var trimmedResult = result.Trim();
                query = query.Where(e => string.Equals(e.Result, trimmedResult, StringComparison.OrdinalIgnoreCase));
            }

            if (fromUtc.HasValue)
            {
                query = query.Where(e => e.OccurredAtUtc >= fromUtc.Value);
            }

            if (toUtc.HasValue)
            {
                query = query.Where(e => e.OccurredAtUtc <= toUtc.Value);
            }

            var ordered = query
                .OrderByDescending(e => e.OccurredAtUtc)
                .ToList();

            var totalCount = ordered.Count;
            var items = ordered
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToList();

            return Task.FromResult<(IReadOnlyList<AuditEvent>, int)>((items, totalCount));
        }
    }
}

public sealed class InMemoryOutboxRepository : IOutboxRepository
{
    private readonly InMemoryHiveStore _store;

    public InMemoryOutboxRepository(InMemoryHiveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task EnqueueAsync(OutboxEventRecord record, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.OutboxEvents.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEventRecord>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        lock (_store.SyncRoot)
        {
            IReadOnlyList<OutboxEventRecord> pending = _store.OutboxEvents
                .Where(e => e.DispatchedAtUtc is null)
                .OrderBy(e => e.OccurredAtUtc)
                .Take(safeLimit)
                .ToList();
            return Task.FromResult(pending);
        }
    }

    public Task<long> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            long count = _store.OutboxEvents.LongCount(e => e.DispatchedAtUtc is null);
            return Task.FromResult(count);
        }
    }

    public Task MarkDispatchedAsync(Guid eventId, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            for (var i = 0; i < _store.OutboxEvents.Count; i++)
            {
                if (_store.OutboxEvents[i].EventId == eventId)
                {
                    _store.OutboxEvents[i] = _store.OutboxEvents[i] with { DispatchedAtUtc = dispatchedAtUtc };
                    break;
                }
            }
        }

        return Task.CompletedTask;
    }
}
