using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Deployments;
using Hive.Domain.Devices;
using Hive.Domain.Telemetry;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hive.Infrastructure.Repositories;

public sealed class PackageStorageOptions
{
    public const string SectionName = "PackageStorage";

    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "cordon-hive-packages");
}

public sealed record StoredPackageContent(
    string StoragePath,
    string Sha256,
    long SizeBytes);

public interface IPackageArtifactStore
{
    Task<StoredPackageContent> StoreAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

    bool Exists(string storagePath);

    Stream OpenRead(string storagePath);

    Stream OpenReadRange(string storagePath, long startOffset, long length);

    void DeleteIfExists(string storagePath);
}

public sealed class FileSystemPackageArtifactStore : IPackageArtifactStore
{
    private readonly string _rootFullPath;

    public FileSystemPackageArtifactStore(IOptions<PackageStorageOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public FileSystemPackageArtifactStore(PackageStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            throw new ArgumentException("PackageStorage RootPath must not be empty.", nameof(options));
        }

        _rootFullPath = Path.GetFullPath(options.RootPath);
        Directory.CreateDirectory(_rootFullPath);
    }

    public async Task<StoredPackageContent> StoreAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(_rootFullPath);

        var tempFileName = $"{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(_rootFullPath, tempFileName);
        var finalFileName = $"{Guid.NewGuid():N}.pkg";
        var finalPath = Path.Combine(_rootFullPath, finalFileName);

        try
        {
            long totalBytes = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];

            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                int bytesRead;
                while ((bytesRead = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (totalBytes <= 0)
            {
                DeleteFileQuietly(tempPath);
                return new StoredPackageContent(string.Empty, string.Empty, 0);
            }

            var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            File.Move(tempPath, finalPath);
            return new StoredPackageContent(finalPath, sha256, totalBytes);
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            DeleteFileQuietly(finalPath);
            throw;
        }
    }

    public bool Exists(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return false;
        }

        var fullPath = ResolveSafePath(storagePath);
        return File.Exists(fullPath);
    }

    public Stream OpenRead(string storagePath)
    {
        var fullPath = ResolveSafePath(storagePath);
        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
    }

    public Stream OpenReadRange(string storagePath, long startOffset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var fullPath = ResolveSafePath(storagePath);
        var inner = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        try
        {
            inner.Seek(startOffset, SeekOrigin.Begin);
            return new BoundedReadOnlyStream(inner, length);
        }
        catch
        {
            inner.Dispose();
            throw;
        }
    }

    public void DeleteIfExists(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return;
        }

        var fullPath = ResolveSafePath(storagePath);
        DeleteFileQuietly(fullPath);
    }

    private string ResolveSafePath(string storagePath)
    {
        var fullPath = Path.GetFullPath(storagePath);
        var rootWithSep = _rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootFullPath
            : _rootFullPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Package storage path escapes configured RootPath.");
        }

        return fullPath;
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of temp/artifact files.
        }
    }

    private sealed class BoundedReadOnlyStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadOnlyStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0 || count <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, _remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

public sealed record DeploymentJobWithTargets(
    DeploymentJob Job,
    IReadOnlyList<DeploymentTarget> Targets);

public sealed record ActiveDeviceDeployment(
    DeploymentTarget Target,
    DeploymentJob Job,
    SignedManifestEnvelope Envelope);

public enum LeaseAcquisitionStatus
{
    Granted,
    Denied,
    NotFound,
    WrongDevice
}

public sealed record LeaseAcquisitionResult(
    LeaseAcquisitionStatus Status,
    DeploymentLeaseResponse? Response);

public enum RecordDeploymentEventStatus
{
    Success,
    NotFound,
    WrongDevice,
    IllegalTransition,
    LeaseRequiredOrInvalid
}

public sealed record RecordDeploymentEventResult(
    RecordDeploymentEventStatus Status,
    DeploymentTarget? Target,
    string? ErrorDetail = null);

public interface IDeploymentRepository
{
    Task CreatePackageAsync(PackageArtifact package, CancellationToken cancellationToken = default);

    Task<PackageArtifact?> GetPackageByIdAsync(Guid packageId, CancellationToken cancellationToken = default);

    Task<PackageArtifact?> GetPackageBySha256Async(string sha256, CancellationToken cancellationToken = default);

    Task SaveSignedManifestAsync(
        Guid releaseId,
        Guid productId,
        SignedManifestEnvelope envelope,
        string signedBySid,
        DateTimeOffset signedAtUtc,
        CancellationToken cancellationToken = default);

    Task<SignedManifestEnvelope?> GetSignedManifestAsync(Guid releaseId, CancellationToken cancellationToken = default);

    Task CreateJobAsync(
        DeploymentJob job,
        IReadOnlyList<DeploymentTarget> targets,
        CancellationToken cancellationToken = default);

    Task<DeploymentJobWithTargets?> GetJobWithTargetsAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeploymentJobWithTargets>> ListJobsAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        CancellationToken cancellationToken = default);

    Task<DeploymentJobWithTargets?> CancelJobTargetsAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveDeviceDeployment>> ListActiveDeploymentsForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task<DeploymentTarget?> GetTargetByIdAsync(Guid targetId, CancellationToken cancellationToken = default);

    Task<LeaseAcquisitionResult> TryAcquireLeaseAsync(
        Guid targetId,
        Guid deviceId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string>? protectedProcesses = null,
        CancellationToken cancellationToken = default);

    Task<RecordDeploymentEventResult> RecordTargetEventAsync(
        Guid targetId,
        Guid deviceId,
        DeploymentState nextState,
        string reasonCode,
        string? message,
        Guid? leaseId,
        int? exitCode,
        DateTimeOffset? deferredUntilUtc,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeploymentAttempt>> ListAttemptsAsync(
        Guid targetId,
        CancellationToken cancellationToken = default);
}

internal static class DeploymentRepositoryHelpers
{
    internal static readonly JsonSerializerOptions CanonicalManifestJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool IsTerminalState(DeploymentState state) =>
        state is DeploymentState.Verified or DeploymentState.Failed or DeploymentState.Cancelled;

    public static bool IsLeaseEligibleState(DeploymentState state) =>
        state is DeploymentState.Pending
            or DeploymentState.Downloading
            or DeploymentState.Staged
            or DeploymentState.Deferred;

    public static bool RequiresActiveLease(DeploymentState state) =>
        state is DeploymentState.Installing
            or DeploymentState.Verifying
            or DeploymentState.Verified
            or DeploymentState.PendingReboot;

    public static bool ShouldClearLeaseOnTransition(DeploymentState nextState) =>
        nextState is DeploymentState.Verified
            or DeploymentState.Failed
            or DeploymentState.Cancelled
            or DeploymentState.Deferred;

    public static (bool Allowed, string ReasonCode, string Message) EvaluateDeviceProtectionState(
        Device? device,
        DeviceProcessSnapshot? recentProcessSnapshot,
        IReadOnlyCollection<string>? protectedProcesses,
        DateTimeOffset utcNow)
    {
        if (device is null || device.EnrollmentState != EnrollmentState.Active)
        {
            return (false, "device_not_active", "Workstation is not enrolled and active.");
        }

        var connectivity = DeviceConnectivityEvaluator.Evaluate(device.LastSeenUtc, utcNow);
        if (device.LastSeenUtc is null || connectivity != ConnectivityState.Online)
        {
            return (
                false,
                "protection_state_unknown",
                $"Workstation protection state is unknown or stale ({connectivity}); deployment lease denied fail-closed.");
        }

        if (device.ProtectedModeActive)
        {
            var reason = string.IsNullOrWhiteSpace(device.ProtectedModeReason)
                ? "Protected Forensic Work Mode lock is active; deployment lease denied."
                : $"Protected Forensic Work Mode is active ({device.ProtectedModeReason}); deployment lease denied.";
            return (false, "manual_protection_lock_active", reason);
        }

        if (recentProcessSnapshot is not null &&
            protectedProcesses is { Count: > 0 } &&
            utcNow - recentProcessSnapshot.SampledAtUtc <= DeviceConnectivityEvaluator.DefaultStaleThreshold)
        {
            var normalizedProtected = protectedProcesses
                .Select(NormalizeProcessName)
                .Where(static n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in recentProcessSnapshot.Processes)
            {
                var normalized = NormalizeProcessName(proc.ImageName);
                if (!string.IsNullOrEmpty(normalized) && normalizedProtected.Contains(normalized))
                {
                    return (
                        false,
                        "protected_workload_active",
                        $"Protected forensic process '{proc.ImageName}' is currently running; deployment lease denied.");
                }
            }
        }

        return (true, "protection_checks_passed", "Device protection state is clear.");
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(name.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }
}

public sealed class InMemoryDeploymentRepository : IDeploymentRepository
{
    private readonly InMemoryHiveStore _store;
    private readonly ITelemetryRepository? _telemetryRepository;

    public InMemoryDeploymentRepository(
        InMemoryHiveStore store,
        ITelemetryRepository? telemetryRepository = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _telemetryRepository = telemetryRepository;
    }

    public Task CreatePackageAsync(PackageArtifact package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        lock (_store.SyncRoot)
        {
            _store.Packages[package.Id] = package;
        }

        return Task.CompletedTask;
    }

    public Task<PackageArtifact?> GetPackageByIdAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.Packages.TryGetValue(packageId, out var package);
            return Task.FromResult(package);
        }
    }

    public Task<PackageArtifact?> GetPackageBySha256Async(string sha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return Task.FromResult<PackageArtifact?>(null);
        }

        var normalized = sha256.Trim().ToLowerInvariant();
        lock (_store.SyncRoot)
        {
            var match = _store.Packages.Values
                .Where(p => string.Equals(p.Sha256, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.UploadedUtc)
                .FirstOrDefault();
            return Task.FromResult(match);
        }
    }

    public Task SaveSignedManifestAsync(
        Guid releaseId,
        Guid productId,
        SignedManifestEnvelope envelope,
        string signedBySid,
        DateTimeOffset signedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_store.SyncRoot)
        {
            _store.SignedManifests[releaseId] = envelope;
        }

        return Task.CompletedTask;
    }

    public Task<SignedManifestEnvelope?> GetSignedManifestAsync(Guid releaseId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.SignedManifests.TryGetValue(releaseId, out var envelope);
            return Task.FromResult(envelope);
        }
    }

    public Task CreateJobAsync(
        DeploymentJob job,
        IReadOnlyList<DeploymentTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(targets);

        lock (_store.SyncRoot)
        {
            _store.DeploymentJobs[job.Id] = job;
            foreach (var target in targets)
            {
                _store.DeploymentTargets[target.Id] = target;
                _store.DeploymentAttempts[target.Id] = new List<DeploymentAttempt>();
            }
        }

        return Task.CompletedTask;
    }

    public Task<DeploymentJobWithTargets?> GetJobWithTargetsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (!_store.DeploymentJobs.TryGetValue(jobId, out var job))
            {
                return Task.FromResult<DeploymentJobWithTargets?>(null);
            }

            var targets = _store.DeploymentTargets.Values
                .Where(t => t.JobId == jobId)
                .OrderBy(t => t.DeviceId)
                .ThenBy(t => t.Id)
                .ToList();

            return Task.FromResult<DeploymentJobWithTargets?>(new DeploymentJobWithTargets(job, targets));
        }
    }

    public Task<IReadOnlyList<DeploymentJobWithTargets>> ListJobsAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            IEnumerable<DeploymentJob> query = _store.DeploymentJobs.Values;

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                query = query.Where(j => string.Equals(j.SiteId, siteId, StringComparison.OrdinalIgnoreCase));
            }

            if (!allSites && allowedSiteIds is not null)
            {
                query = query.Where(j => allowedSiteIds.Contains(j.SiteId));
            }

            var jobs = query
                .OrderByDescending(j => j.CreatedUtc)
                .ThenBy(j => j.Id)
                .ToList();

            var targetsByJob = _store.DeploymentTargets.Values
                .GroupBy(t => t.JobId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<DeploymentTarget>)g.OrderBy(t => t.DeviceId).ThenBy(t => t.Id).ToList());

            IReadOnlyList<DeploymentJobWithTargets> result = jobs
                .Select(j => new DeploymentJobWithTargets(
                    j,
                    targetsByJob.GetValueOrDefault(j.Id, Array.Empty<DeploymentTarget>())))
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task<DeploymentJobWithTargets?> CancelJobTargetsAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (!_store.DeploymentJobs.TryGetValue(jobId, out var job))
            {
                return Task.FromResult<DeploymentJobWithTargets?>(null);
            }

            var targetIds = _store.DeploymentTargets.Values
                .Where(t => t.JobId == jobId)
                .Select(t => t.Id)
                .ToList();

            foreach (var targetId in targetIds)
            {
                var target = _store.DeploymentTargets[targetId];
                if (!DeploymentStateMachine.CanTransition(target.State, DeploymentState.Cancelled))
                {
                    continue;
                }

                var nextAttemptNumber = target.AttemptCount + 1;
                var updated = target with
                {
                    State = DeploymentState.Cancelled,
                    AttemptCount = nextAttemptNumber,
                    ActiveLeaseId = null,
                    LeaseExpiresUtc = null,
                    LastReasonCode = "cancelled_by_admin",
                    LastMessage = "Deployment target cancelled by administrator.",
                    DeferredUntilUtc = null,
                    UpdatedUtc = cancelledAtUtc
                };

                _store.DeploymentTargets[targetId] = updated;

                if (!_store.DeploymentAttempts.TryGetValue(targetId, out var attempts))
                {
                    attempts = new List<DeploymentAttempt>();
                    _store.DeploymentAttempts[targetId] = attempts;
                }

                attempts.Add(new DeploymentAttempt(
                    Guid.NewGuid(),
                    targetId,
                    nextAttemptNumber,
                    DeploymentState.Cancelled,
                    "cancelled_by_admin",
                    "Deployment target cancelled by administrator.",
                    null,
                    cancelledAtUtc));
            }

            var updatedTargets = _store.DeploymentTargets.Values
                .Where(t => t.JobId == jobId)
                .OrderBy(t => t.DeviceId)
                .ThenBy(t => t.Id)
                .ToList();

            return Task.FromResult<DeploymentJobWithTargets?>(new DeploymentJobWithTargets(job, updatedTargets));
        }
    }

    public Task<IReadOnlyList<ActiveDeviceDeployment>> ListActiveDeploymentsForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            var results = new List<ActiveDeviceDeployment>();
            var activeTargets = _store.DeploymentTargets.Values
                .Where(t => t.DeviceId == deviceId && !DeploymentRepositoryHelpers.IsTerminalState(t.State))
                .OrderBy(t => t.UpdatedUtc)
                .ThenBy(t => t.Id);

            foreach (var target in activeTargets)
            {
                if (_store.DeploymentJobs.TryGetValue(target.JobId, out var job) &&
                    _store.SignedManifests.TryGetValue(job.ReleaseId, out var envelope))
                {
                    results.Add(new ActiveDeviceDeployment(target, job, envelope));
                }
            }

            return Task.FromResult<IReadOnlyList<ActiveDeviceDeployment>>(results);
        }
    }

    public Task<DeploymentTarget?> GetTargetByIdAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            _store.DeploymentTargets.TryGetValue(targetId, out var target);
            return Task.FromResult(target);
        }
    }

    public async Task<LeaseAcquisitionResult> TryAcquireLeaseAsync(
        Guid targetId,
        Guid deviceId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string>? protectedProcesses = null,
        CancellationToken cancellationToken = default)
    {
        DeviceProcessSnapshot? processSnapshot = null;
        if (_telemetryRepository is not null && protectedProcesses is { Count: > 0 })
        {
            var telemetry = await _telemetryRepository.GetLatestTelemetryAsync(deviceId, 1, cancellationToken).ConfigureAwait(false);
            processSnapshot = telemetry.LatestProcessSnapshot;
        }

        lock (_store.SyncRoot)
        {
            if (!_store.DeploymentTargets.TryGetValue(targetId, out var target))
            {
                return new LeaseAcquisitionResult(LeaseAcquisitionStatus.NotFound, null);
            }

            if (target.DeviceId != deviceId)
            {
                return new LeaseAcquisitionResult(LeaseAcquisitionStatus.WrongDevice, null);
            }

            _store.Devices.TryGetValue(deviceId, out var device);
            var protection = DeploymentRepositoryHelpers.EvaluateDeviceProtectionState(
                device,
                processSnapshot,
                protectedProcesses,
                utcNow);

            if (!protection.Allowed)
            {
                return new LeaseAcquisitionResult(
                    LeaseAcquisitionStatus.Denied,
                    new DeploymentLeaseResponse(
                        false,
                        targetId,
                        null,
                        null,
                        protection.ReasonCode,
                        protection.Message));
            }

            if (!DeploymentRepositoryHelpers.IsLeaseEligibleState(target.State))
            {
                return new LeaseAcquisitionResult(
                    LeaseAcquisitionStatus.Denied,
                    new DeploymentLeaseResponse(
                        false,
                        targetId,
                        null,
                        null,
                        "target_not_eligible",
                        $"Deployment target is in state '{target.State}' and cannot acquire an execution lease."));
            }

            var hasOtherActiveLease = _store.DeploymentTargets.Values.Any(other =>
                other.DeviceId == deviceId &&
                other.Id != targetId &&
                other.ActiveLeaseId is not null &&
                other.LeaseExpiresUtc is DateTimeOffset otherExp &&
                otherExp > utcNow);

            if (hasOtherActiveLease)
            {
                return new LeaseAcquisitionResult(
                    LeaseAcquisitionStatus.Denied,
                    new DeploymentLeaseResponse(
                        false,
                        targetId,
                        null,
                        null,
                        "device_lease_conflict",
                        "Another deployment target already holds an active lease on this device."));
            }

            var leaseId = Guid.NewGuid();
            var expiresUtc = utcNow.Add(leaseDuration);
            var updated = target with
            {
                ActiveLeaseId = leaseId,
                LeaseExpiresUtc = expiresUtc,
                UpdatedUtc = utcNow
            };

            _store.DeploymentTargets[targetId] = updated;

            return new LeaseAcquisitionResult(
                LeaseAcquisitionStatus.Granted,
                new DeploymentLeaseResponse(
                    true,
                    targetId,
                    leaseId,
                    expiresUtc,
                    "lease_granted",
                    "Exclusive deployment lease granted."));
        }
    }

    public Task<RecordDeploymentEventResult> RecordTargetEventAsync(
        Guid targetId,
        Guid deviceId,
        DeploymentState nextState,
        string reasonCode,
        string? message,
        Guid? leaseId,
        int? exitCode,
        DateTimeOffset? deferredUntilUtc,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (!_store.DeploymentTargets.TryGetValue(targetId, out var target))
            {
                return Task.FromResult(new RecordDeploymentEventResult(RecordDeploymentEventStatus.NotFound, null));
            }

            if (target.DeviceId != deviceId)
            {
                return Task.FromResult(new RecordDeploymentEventResult(RecordDeploymentEventStatus.WrongDevice, null));
            }

            if (!DeploymentStateMachine.CanTransition(target.State, nextState))
            {
                return Task.FromResult(new RecordDeploymentEventResult(
                    RecordDeploymentEventStatus.IllegalTransition,
                    target,
                    $"Illegal deployment state transition from '{target.State}' to '{nextState}'."));
            }

            if (DeploymentRepositoryHelpers.RequiresActiveLease(nextState))
            {
                if (leaseId is not Guid providedLease ||
                    providedLease == Guid.Empty ||
                    target.ActiveLeaseId != providedLease ||
                    target.LeaseExpiresUtc is not DateTimeOffset exp ||
                    exp <= utcNow)
                {
                    return Task.FromResult(new RecordDeploymentEventResult(
                        RecordDeploymentEventStatus.LeaseRequiredOrInvalid,
                        target,
                        $"A valid non-expired deployment lease is required to transition to '{nextState}'."));
                }
            }

            var clearLease = DeploymentRepositoryHelpers.ShouldClearLeaseOnTransition(nextState);
            var attemptNumber = target.AttemptCount + 1;
            var updated = target with
            {
                State = nextState,
                AttemptCount = attemptNumber,
                ActiveLeaseId = clearLease ? null : target.ActiveLeaseId,
                LeaseExpiresUtc = clearLease ? null : target.LeaseExpiresUtc,
                LastReasonCode = reasonCode,
                LastMessage = message,
                DeferredUntilUtc = nextState == DeploymentState.Deferred ? deferredUntilUtc : null,
                UpdatedUtc = utcNow
            };

            _store.DeploymentTargets[targetId] = updated;

            if (!_store.DeploymentAttempts.TryGetValue(targetId, out var attempts))
            {
                attempts = new List<DeploymentAttempt>();
                _store.DeploymentAttempts[targetId] = attempts;
            }

            attempts.Add(new DeploymentAttempt(
                Guid.NewGuid(),
                targetId,
                attemptNumber,
                nextState,
                reasonCode,
                message,
                exitCode,
                utcNow));

            return Task.FromResult(new RecordDeploymentEventResult(RecordDeploymentEventStatus.Success, updated));
        }
    }

    public Task<IReadOnlyList<DeploymentAttempt>> ListAttemptsAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        lock (_store.SyncRoot)
        {
            if (_store.DeploymentAttempts.TryGetValue(targetId, out var attempts))
            {
                return Task.FromResult<IReadOnlyList<DeploymentAttempt>>(attempts.ToList());
            }

            return Task.FromResult<IReadOnlyList<DeploymentAttempt>>(Array.Empty<DeploymentAttempt>());
        }
    }
}

public sealed class PostgresDeploymentRepository : IDeploymentRepository
{
    private const string PackageColumns = """
        package_id, file_name, sha256, size_bytes, storage_path, uploaded_by_sid, uploaded_at_utc
        """;

    private const string JobColumns = """
        job_id, site_id, release_id, product_name, version, policy, created_by_sid, created_at_utc
        """;

    private const string TargetColumns = """
        target_id, job_id, device_id, state, attempt_count, active_lease_id,
        lease_expires_utc, last_reason_code, last_message, deferred_until_utc, updated_at_utc
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ITelemetryRepository? _telemetryRepository;

    public PostgresDeploymentRepository(
        NpgsqlDataSource dataSource,
        ITelemetryRepository? telemetryRepository = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _telemetryRepository = telemetryRepository;
    }

    public async Task CreatePackageAsync(PackageArtifact package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO packages (
                package_id, file_name, sha256, size_bytes, storage_path, uploaded_by_sid, uploaded_at_utc
            )
            VALUES (
                @package_id, @file_name, @sha256, @size_bytes, @storage_path, @uploaded_by_sid, @uploaded_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("package_id", package.Id);
        cmd.Parameters.AddWithValue("file_name", package.FileName);
        cmd.Parameters.AddWithValue("sha256", package.Sha256);
        cmd.Parameters.AddWithValue("size_bytes", package.SizeBytes);
        cmd.Parameters.AddWithValue("storage_path", package.StoragePath);
        cmd.Parameters.AddWithValue("uploaded_by_sid", package.UploadedBySid);
        cmd.Parameters.AddWithValue("uploaded_at_utc", package.UploadedUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PackageArtifact?> GetPackageByIdAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {PackageColumns} FROM packages WHERE package_id = @package_id;";
        cmd.Parameters.AddWithValue("package_id", packageId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPackage(reader) : null;
    }

    public async Task<PackageArtifact?> GetPackageBySha256Async(string sha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PackageColumns}
            FROM packages
            WHERE lower(sha256) = lower(@sha256)
            ORDER BY uploaded_at_utc DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("sha256", sha256.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPackage(reader) : null;
    }

    public async Task SaveSignedManifestAsync(
        Guid releaseId,
        Guid productId,
        SignedManifestEnvelope envelope,
        string signedBySid,
        DateTimeOffset signedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_manifests (
                release_id, product_id, package_sha256, key_id, algorithm,
                public_key_pem, manifest_canonical_json, manifest_json, signature_base64,
                issued_at_utc, expires_at_utc, signed_by_sid, signed_at_utc
            )
            VALUES (
                @release_id, @product_id, @package_sha256, @key_id, @algorithm,
                @public_key_pem, @manifest_canonical_json, @manifest_json::jsonb, @signature_base64,
                @issued_at_utc, @expires_at_utc, @signed_by_sid, @signed_at_utc
            )
            ON CONFLICT (release_id) DO UPDATE SET
                product_id = EXCLUDED.product_id,
                package_sha256 = EXCLUDED.package_sha256,
                key_id = EXCLUDED.key_id,
                algorithm = EXCLUDED.algorithm,
                public_key_pem = EXCLUDED.public_key_pem,
                manifest_canonical_json = EXCLUDED.manifest_canonical_json,
                manifest_json = EXCLUDED.manifest_json,
                signature_base64 = EXCLUDED.signature_base64,
                issued_at_utc = EXCLUDED.issued_at_utc,
                expires_at_utc = EXCLUDED.expires_at_utc,
                signed_by_sid = EXCLUDED.signed_by_sid,
                signed_at_utc = EXCLUDED.signed_at_utc;
            """;
        cmd.Parameters.AddWithValue("release_id", releaseId);
        cmd.Parameters.AddWithValue("product_id", productId);
        cmd.Parameters.AddWithValue("package_sha256", envelope.Manifest.PackageSha256);
        cmd.Parameters.AddWithValue("key_id", envelope.Manifest.KeyId);
        cmd.Parameters.AddWithValue("algorithm", envelope.Algorithm);
        cmd.Parameters.AddWithValue("public_key_pem", envelope.PublicKeyPem);
        cmd.Parameters.AddWithValue("manifest_canonical_json", envelope.ManifestCanonicalJson);
        cmd.Parameters.AddWithValue("manifest_json", envelope.ManifestCanonicalJson);
        cmd.Parameters.AddWithValue("signature_base64", envelope.SignatureBase64);
        cmd.Parameters.AddWithValue("issued_at_utc", envelope.Manifest.IssuedUtc);
        cmd.Parameters.AddWithValue("expires_at_utc", envelope.Manifest.ExpiresUtc);
        cmd.Parameters.AddWithValue("signed_by_sid", signedBySid);
        cmd.Parameters.AddWithValue("signed_at_utc", signedAtUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SignedManifestEnvelope?> GetSignedManifestAsync(Guid releaseId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT manifest_canonical_json, signature_base64, algorithm, public_key_pem
            FROM release_manifests
            WHERE release_id = @release_id;
            """;
        cmd.Parameters.AddWithValue("release_id", releaseId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvelope(reader) : null;
    }

    public async Task CreateJobAsync(
        DeploymentJob job,
        IReadOnlyList<DeploymentTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(targets);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var jobCmd = conn.CreateCommand())
        {
            jobCmd.Transaction = tx;
            jobCmd.CommandText = """
                INSERT INTO deployment_jobs (
                    job_id, site_id, release_id, product_name, version, policy, created_by_sid, created_at_utc
                )
                VALUES (
                    @job_id, @site_id, @release_id, @product_name, @version, @policy, @created_by_sid, @created_at_utc
                );
                """;
            jobCmd.Parameters.AddWithValue("job_id", job.Id);
            jobCmd.Parameters.AddWithValue("site_id", job.SiteId);
            jobCmd.Parameters.AddWithValue("release_id", job.ReleaseId);
            jobCmd.Parameters.AddWithValue("product_name", job.ProductName);
            jobCmd.Parameters.AddWithValue("version", job.Version);
            jobCmd.Parameters.AddWithValue("policy", job.Policy.ToString());
            jobCmd.Parameters.AddWithValue("created_by_sid", job.CreatedBySid);
            jobCmd.Parameters.AddWithValue("created_at_utc", job.CreatedUtc);
            await jobCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var target in targets)
        {
            await using var targetCmd = conn.CreateCommand();
            targetCmd.Transaction = tx;
            targetCmd.CommandText = """
                INSERT INTO deployment_targets (
                    target_id, job_id, device_id, state, attempt_count, active_lease_id,
                    lease_expires_utc, last_reason_code, last_message, deferred_until_utc, updated_at_utc
                )
                VALUES (
                    @target_id, @job_id, @device_id, @state, @attempt_count, @active_lease_id,
                    @lease_expires_utc, @last_reason_code, @last_message, @deferred_until_utc, @updated_at_utc
                );
                """;
            targetCmd.Parameters.AddWithValue("target_id", target.Id);
            targetCmd.Parameters.AddWithValue("job_id", target.JobId);
            targetCmd.Parameters.AddWithValue("device_id", target.DeviceId);
            targetCmd.Parameters.AddWithValue("state", target.State.ToString());
            targetCmd.Parameters.AddWithValue("attempt_count", target.AttemptCount);
            PostgresMapping.AddNullableParameter(targetCmd, "active_lease_id", target.ActiveLeaseId);
            PostgresMapping.AddNullableParameter(targetCmd, "lease_expires_utc", target.LeaseExpiresUtc);
            PostgresMapping.AddNullableParameter(targetCmd, "last_reason_code", target.LastReasonCode);
            PostgresMapping.AddNullableParameter(targetCmd, "last_message", target.LastMessage);
            PostgresMapping.AddNullableParameter(targetCmd, "deferred_until_utc", target.DeferredUntilUtc);
            targetCmd.Parameters.AddWithValue("updated_at_utc", target.UpdatedUtc);
            await targetCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<DeploymentJobWithTargets?> GetJobWithTargetsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        DeploymentJob? job;
        await using (var jobCmd = conn.CreateCommand())
        {
            jobCmd.CommandText = $"SELECT {JobColumns} FROM deployment_jobs WHERE job_id = @job_id;";
            jobCmd.Parameters.AddWithValue("job_id", jobId);
            await using var reader = await jobCmd.ExecuteReaderAsync(cancellationToken);
            job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }

        if (job is null)
        {
            return null;
        }

        var targets = await ReadTargetsForJobsAsync(conn, [jobId], cancellationToken);
        return new DeploymentJobWithTargets(job, targets.GetValueOrDefault(jobId, Array.Empty<DeploymentTarget>()));
    }

    public async Task<IReadOnlyList<DeploymentJobWithTargets>> ListJobsAsync(
        string? siteId = null,
        IReadOnlySet<string>? allowedSiteIds = null,
        bool allSites = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder($"SELECT {JobColumns} FROM deployment_jobs WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            sql.Append(" AND site_id = @site_id");
            cmd.Parameters.AddWithValue("site_id", siteId.Trim());
        }

        if (!allSites && allowedSiteIds is not null)
        {
            sql.Append(" AND site_id = ANY(@allowed_sites)");
            cmd.Parameters.AddWithValue("allowed_sites", allowedSiteIds.ToArray());
        }

        sql.Append(" ORDER BY created_at_utc DESC, job_id ASC;");
        cmd.CommandText = sql.ToString();

        var jobs = new List<DeploymentJob>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                jobs.Add(ReadJob(reader));
            }
        }

        if (jobs.Count == 0)
        {
            return Array.Empty<DeploymentJobWithTargets>();
        }

        var targetsByJob = await ReadTargetsForJobsAsync(conn, jobs.Select(j => j.Id).ToArray(), cancellationToken);
        return jobs
            .Select(j => new DeploymentJobWithTargets(j, targetsByJob.GetValueOrDefault(j.Id, Array.Empty<DeploymentTarget>())))
            .ToArray();
    }

    public async Task<DeploymentJobWithTargets?> CancelJobTargetsAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        DeploymentJob? job;
        await using (var jobCmd = conn.CreateCommand())
        {
            jobCmd.Transaction = tx;
            jobCmd.CommandText = $"SELECT {JobColumns} FROM deployment_jobs WHERE job_id = @job_id;";
            jobCmd.Parameters.AddWithValue("job_id", jobId);
            await using var reader = await jobCmd.ExecuteReaderAsync(cancellationToken);
            job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }

        if (job is null)
        {
            return null;
        }

        var existingTargets = new List<DeploymentTarget>();
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"""
                SELECT {TargetColumns}
                FROM deployment_targets
                WHERE job_id = @job_id
                ORDER BY device_id ASC, target_id ASC
                FOR UPDATE;
                """;
            selectCmd.Parameters.AddWithValue("job_id", jobId);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingTargets.Add(ReadTarget(reader));
            }
        }

        var finalTargets = new List<DeploymentTarget>(existingTargets.Count);
        foreach (var target in existingTargets)
        {
            if (!DeploymentStateMachine.CanTransition(target.State, DeploymentState.Cancelled))
            {
                finalTargets.Add(target);
                continue;
            }

            var nextAttemptNumber = target.AttemptCount + 1;
            var updated = target with
            {
                State = DeploymentState.Cancelled,
                AttemptCount = nextAttemptNumber,
                ActiveLeaseId = null,
                LeaseExpiresUtc = null,
                LastReasonCode = "cancelled_by_admin",
                LastMessage = "Deployment target cancelled by administrator.",
                DeferredUntilUtc = null,
                UpdatedUtc = cancelledAtUtc
            };

            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE deployment_targets
                    SET state = @state,
                        attempt_count = @attempt_count,
                        active_lease_id = NULL,
                        lease_expires_utc = NULL,
                        last_reason_code = @last_reason_code,
                        last_message = @last_message,
                        deferred_until_utc = NULL,
                        updated_at_utc = @updated_at_utc
                    WHERE target_id = @target_id;
                    """;
                updateCmd.Parameters.AddWithValue("target_id", updated.Id);
                updateCmd.Parameters.AddWithValue("state", updated.State.ToString());
                updateCmd.Parameters.AddWithValue("attempt_count", updated.AttemptCount);
                updateCmd.Parameters.AddWithValue("last_reason_code", updated.LastReasonCode!);
                updateCmd.Parameters.AddWithValue("last_message", updated.LastMessage!);
                updateCmd.Parameters.AddWithValue("updated_at_utc", updated.UpdatedUtc);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAttemptAsync(
                conn,
                tx,
                new DeploymentAttempt(
                    Guid.NewGuid(),
                    updated.Id,
                    nextAttemptNumber,
                    DeploymentState.Cancelled,
                    "cancelled_by_admin",
                    "Deployment target cancelled by administrator.",
                    null,
                    cancelledAtUtc),
                cancellationToken);

            finalTargets.Add(updated);
        }

        await tx.CommitAsync(cancellationToken);
        return new DeploymentJobWithTargets(job, finalTargets);
    }

    public async Task<IReadOnlyList<ActiveDeviceDeployment>> ListActiveDeploymentsForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.target_id, t.job_id, t.device_id, t.state, t.attempt_count, t.active_lease_id,
                   t.lease_expires_utc, t.last_reason_code, t.last_message, t.deferred_until_utc, t.updated_at_utc,
                   j.job_id, j.site_id, j.release_id, j.product_name, j.version, j.policy, j.created_by_sid, j.created_at_utc,
                   m.manifest_canonical_json, m.signature_base64, m.algorithm, m.public_key_pem
            FROM deployment_targets t
            INNER JOIN deployment_jobs j ON j.job_id = t.job_id
            INNER JOIN release_manifests m ON m.release_id = j.release_id
            WHERE t.device_id = @device_id
              AND t.state NOT IN ('Verified', 'Failed', 'Cancelled')
            ORDER BY t.updated_at_utc ASC, t.target_id ASC;
            """;
        cmd.Parameters.AddWithValue("device_id", deviceId);

        var items = new List<ActiveDeviceDeployment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var target = ReadTarget(reader, offset: 0);
            var job = ReadJob(reader, offset: 11);
            var envelope = ReadEnvelope(reader, offset: 19);
            items.Add(new ActiveDeviceDeployment(target, job, envelope));
        }

        return items;
    }

    public async Task<DeploymentTarget?> GetTargetByIdAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TargetColumns} FROM deployment_targets WHERE target_id = @target_id;";
        cmd.Parameters.AddWithValue("target_id", targetId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTarget(reader) : null;
    }

    public async Task<LeaseAcquisitionResult> TryAcquireLeaseAsync(
        Guid targetId,
        Guid deviceId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string>? protectedProcesses = null,
        CancellationToken cancellationToken = default)
    {
        DeviceProcessSnapshot? processSnapshot = null;
        if (_telemetryRepository is not null && protectedProcesses is { Count: > 0 })
        {
            var telemetry = await _telemetryRepository.GetLatestTelemetryAsync(deviceId, 1, cancellationToken).ConfigureAwait(false);
            processSnapshot = telemetry.LatestProcessSnapshot;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        Device? device = null;
        await using (var devCmd = conn.CreateCommand())
        {
            devCmd.Transaction = tx;
            devCmd.CommandText = """
                SELECT device_id, site_id, hostname, asset_number, assigned_user_sid,
                       certificate_thumbprint, enrollment_state, enrolled_at_utc, last_seen_utc,
                       boot_id, agent_version, policy_revision, row_version,
                       protected_mode_active, protected_mode_reason
                FROM devices
                WHERE device_id = @device_id
                FOR UPDATE;
                """;
            devCmd.Parameters.AddWithValue("device_id", deviceId);
            await using var devReader = await devCmd.ExecuteReaderAsync(cancellationToken);
            if (await devReader.ReadAsync(cancellationToken))
            {
                device = PostgresMapping.ReadDevice(devReader);
            }
        }

        DeploymentTarget? target;
        await using (var targetCmd = conn.CreateCommand())
        {
            targetCmd.Transaction = tx;
            targetCmd.CommandText = $"""
                SELECT {TargetColumns}
                FROM deployment_targets
                WHERE target_id = @target_id
                FOR UPDATE;
                """;
            targetCmd.Parameters.AddWithValue("target_id", targetId);
            await using var reader = await targetCmd.ExecuteReaderAsync(cancellationToken);
            target = await reader.ReadAsync(cancellationToken) ? ReadTarget(reader) : null;
        }

        if (target is null)
        {
            return new LeaseAcquisitionResult(LeaseAcquisitionStatus.NotFound, null);
        }

        if (target.DeviceId != deviceId)
        {
            return new LeaseAcquisitionResult(LeaseAcquisitionStatus.WrongDevice, null);
        }

        var protection = DeploymentRepositoryHelpers.EvaluateDeviceProtectionState(
            device,
            processSnapshot,
            protectedProcesses,
            utcNow);

        if (!protection.Allowed)
        {
            return new LeaseAcquisitionResult(
                LeaseAcquisitionStatus.Denied,
                new DeploymentLeaseResponse(
                    false,
                    targetId,
                    null,
                    null,
                    protection.ReasonCode,
                    protection.Message));
        }

        if (!DeploymentRepositoryHelpers.IsLeaseEligibleState(target.State))
        {
            return new LeaseAcquisitionResult(
                LeaseAcquisitionStatus.Denied,
                new DeploymentLeaseResponse(
                    false,
                    targetId,
                    null,
                    null,
                    "target_not_eligible",
                    $"Deployment target is in state '{target.State}' and cannot acquire an execution lease."));
        }

        bool hasOtherActiveLease;
        await using (var conflictCmd = conn.CreateCommand())
        {
            conflictCmd.Transaction = tx;
            conflictCmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM deployment_targets
                    WHERE device_id = @device_id
                      AND target_id <> @target_id
                      AND active_lease_id IS NOT NULL
                      AND lease_expires_utc > @now_utc
                );
                """;
            conflictCmd.Parameters.AddWithValue("device_id", deviceId);
            conflictCmd.Parameters.AddWithValue("target_id", targetId);
            conflictCmd.Parameters.AddWithValue("now_utc", utcNow);
            hasOtherActiveLease = (bool)(await conflictCmd.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        if (hasOtherActiveLease)
        {
            return new LeaseAcquisitionResult(
                LeaseAcquisitionStatus.Denied,
                new DeploymentLeaseResponse(
                    false,
                    targetId,
                    null,
                    null,
                    "device_lease_conflict",
                    "Another deployment target already holds an active lease on this device."));
        }

        var leaseId = Guid.NewGuid();
        var expiresUtc = utcNow.Add(leaseDuration);

        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE deployment_targets
                SET active_lease_id = @active_lease_id,
                    lease_expires_utc = @lease_expires_utc,
                    updated_at_utc = @updated_at_utc
                WHERE target_id = @target_id;
                """;
            updateCmd.Parameters.AddWithValue("target_id", targetId);
            updateCmd.Parameters.AddWithValue("active_lease_id", leaseId);
            updateCmd.Parameters.AddWithValue("lease_expires_utc", expiresUtc);
            updateCmd.Parameters.AddWithValue("updated_at_utc", utcNow);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new LeaseAcquisitionResult(
            LeaseAcquisitionStatus.Granted,
            new DeploymentLeaseResponse(
                true,
                targetId,
                leaseId,
                expiresUtc,
                "lease_granted",
                "Exclusive deployment lease granted."));
    }

    public async Task<RecordDeploymentEventResult> RecordTargetEventAsync(
        Guid targetId,
        Guid deviceId,
        DeploymentState nextState,
        string reasonCode,
        string? message,
        Guid? leaseId,
        int? exitCode,
        DateTimeOffset? deferredUntilUtc,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        DeploymentTarget? target;
        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = $"""
                SELECT {TargetColumns}
                FROM deployment_targets
                WHERE target_id = @target_id
                FOR UPDATE;
                """;
            selectCmd.Parameters.AddWithValue("target_id", targetId);
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            target = await reader.ReadAsync(cancellationToken) ? ReadTarget(reader) : null;
        }

        if (target is null)
        {
            return new RecordDeploymentEventResult(RecordDeploymentEventStatus.NotFound, null);
        }

        if (target.DeviceId != deviceId)
        {
            return new RecordDeploymentEventResult(RecordDeploymentEventStatus.WrongDevice, null);
        }

        if (!DeploymentStateMachine.CanTransition(target.State, nextState))
        {
            return new RecordDeploymentEventResult(
                RecordDeploymentEventStatus.IllegalTransition,
                target,
                $"Illegal deployment state transition from '{target.State}' to '{nextState}'.");
        }

        if (DeploymentRepositoryHelpers.RequiresActiveLease(nextState))
        {
            if (leaseId is not Guid providedLease ||
                providedLease == Guid.Empty ||
                target.ActiveLeaseId != providedLease ||
                target.LeaseExpiresUtc is not DateTimeOffset exp ||
                exp <= utcNow)
            {
                return new RecordDeploymentEventResult(
                    RecordDeploymentEventStatus.LeaseRequiredOrInvalid,
                    target,
                    $"A valid non-expired deployment lease is required to transition to '{nextState}'.");
            }
        }

        var clearLease = DeploymentRepositoryHelpers.ShouldClearLeaseOnTransition(nextState);
        var attemptNumber = target.AttemptCount + 1;
        var updated = target with
        {
            State = nextState,
            AttemptCount = attemptNumber,
            ActiveLeaseId = clearLease ? null : target.ActiveLeaseId,
            LeaseExpiresUtc = clearLease ? null : target.LeaseExpiresUtc,
            LastReasonCode = reasonCode,
            LastMessage = message,
            DeferredUntilUtc = nextState == DeploymentState.Deferred ? deferredUntilUtc : null,
            UpdatedUtc = utcNow
        };

        await using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE deployment_targets
                SET state = @state,
                    attempt_count = @attempt_count,
                    active_lease_id = @active_lease_id,
                    lease_expires_utc = @lease_expires_utc,
                    last_reason_code = @last_reason_code,
                    last_message = @last_message,
                    deferred_until_utc = @deferred_until_utc,
                    updated_at_utc = @updated_at_utc
                WHERE target_id = @target_id;
                """;
            updateCmd.Parameters.AddWithValue("target_id", updated.Id);
            updateCmd.Parameters.AddWithValue("state", updated.State.ToString());
            updateCmd.Parameters.AddWithValue("attempt_count", updated.AttemptCount);
            PostgresMapping.AddNullableParameter(updateCmd, "active_lease_id", updated.ActiveLeaseId);
            PostgresMapping.AddNullableParameter(updateCmd, "lease_expires_utc", updated.LeaseExpiresUtc);
            PostgresMapping.AddNullableParameter(updateCmd, "last_reason_code", updated.LastReasonCode);
            PostgresMapping.AddNullableParameter(updateCmd, "last_message", updated.LastMessage);
            PostgresMapping.AddNullableParameter(updateCmd, "deferred_until_utc", updated.DeferredUntilUtc);
            updateCmd.Parameters.AddWithValue("updated_at_utc", updated.UpdatedUtc);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAttemptAsync(
            conn,
            tx,
            new DeploymentAttempt(
                Guid.NewGuid(),
                targetId,
                attemptNumber,
                nextState,
                reasonCode,
                message,
                exitCode,
                utcNow),
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new RecordDeploymentEventResult(RecordDeploymentEventStatus.Success, updated);
    }

    public async Task<IReadOnlyList<DeploymentAttempt>> ListAttemptsAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT attempt_id, target_id, attempt_number, resulting_state, reason_code, message, exit_code, occurred_at_utc
            FROM deployment_attempts
            WHERE target_id = @target_id
            ORDER BY attempt_number ASC, occurred_at_utc ASC;
            """;
        cmd.Parameters.AddWithValue("target_id", targetId);

        var attempts = new List<DeploymentAttempt>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new DeploymentAttempt(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                Enum.Parse<DeploymentState>(reader.GetString(3), ignoreCase: true),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return attempts;
    }

    private static async Task InsertAttemptAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DeploymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO deployment_attempts (
                attempt_id, target_id, attempt_number, resulting_state, reason_code, message, exit_code, occurred_at_utc
            )
            VALUES (
                @attempt_id, @target_id, @attempt_number, @resulting_state, @reason_code, @message, @exit_code, @occurred_at_utc
            );
            """;
        cmd.Parameters.AddWithValue("attempt_id", attempt.Id);
        cmd.Parameters.AddWithValue("target_id", attempt.TargetId);
        cmd.Parameters.AddWithValue("attempt_number", attempt.AttemptNumber);
        cmd.Parameters.AddWithValue("resulting_state", attempt.ResultingState.ToString());
        cmd.Parameters.AddWithValue("reason_code", attempt.ReasonCode);
        PostgresMapping.AddNullableParameter(cmd, "message", attempt.Message);
        PostgresMapping.AddNullableParameter(cmd, "exit_code", attempt.ExitCode);
        cmd.Parameters.AddWithValue("occurred_at_utc", attempt.OccurredUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<DeploymentTarget>>> ReadTargetsForJobsAsync(
        NpgsqlConnection conn,
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, List<DeploymentTarget>>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TargetColumns}
            FROM deployment_targets
            WHERE job_id = ANY(@job_ids)
            ORDER BY device_id ASC, target_id ASC;
            """;
        cmd.Parameters.AddWithValue("job_ids", jobIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var target = ReadTarget(reader);
            if (!map.TryGetValue(target.JobId, out var list))
            {
                list = new List<DeploymentTarget>();
                map[target.JobId] = list;
            }

            list.Add(target);
        }

        return map.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<DeploymentTarget>)kvp.Value);
    }

    private static PackageArtifact ReadPackage(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6));

    private static SignedManifestEnvelope ReadEnvelope(NpgsqlDataReader reader, int offset = 0)
    {
        var canonicalJson = reader.GetString(offset);
        var signatureBase64 = reader.GetString(offset + 1);
        var algorithm = reader.GetString(offset + 2);
        var publicKeyPem = reader.GetString(offset + 3);
        var manifest = JsonSerializer.Deserialize<SignedReleaseManifest>(
                           canonicalJson,
                           DeploymentRepositoryHelpers.CanonicalManifestJsonOptions)
                       ?? throw new InvalidOperationException("Failed to deserialize signed release manifest.");
        return new SignedManifestEnvelope(manifest, canonicalJson, signatureBase64, algorithm, publicKeyPem);
    }

    private static DeploymentJob ReadJob(NpgsqlDataReader reader, int offset = 0) =>
        new(
            reader.GetGuid(offset),
            reader.GetString(offset + 1),
            reader.GetGuid(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            Enum.Parse<DeploymentPolicy>(reader.GetString(offset + 5), ignoreCase: true),
            reader.GetString(offset + 6),
            reader.GetFieldValue<DateTimeOffset>(offset + 7));

    private static DeploymentTarget ReadTarget(NpgsqlDataReader reader, int offset = 0) =>
        new(
            reader.GetGuid(offset),
            reader.GetGuid(offset + 1),
            reader.GetGuid(offset + 2),
            Enum.Parse<DeploymentState>(reader.GetString(offset + 3), ignoreCase: true),
            reader.GetInt32(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetGuid(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            reader.IsDBNull(offset + 9) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 9),
            reader.GetFieldValue<DateTimeOffset>(offset + 10));
}
