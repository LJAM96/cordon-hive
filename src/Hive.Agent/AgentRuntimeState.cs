using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Windows.Ipc;

namespace Hive.Agent;

public sealed class AgentRuntimeState
{
    public const int MaxIpcProcesses = 80;
    public const int IpcEnvelopeSafetyMarginBytes = 1024;
    private const int FallbackTruncatedNameLength = 128;

    private readonly object _lock = new();
    private DateTimeOffset? _lastHeartbeatUtc;
    private bool _protectedModeActive;
    private MetricSampleDto? _latestSample;
    private IReadOnlyList<VolumeSnapshotDto> _latestVolumes = Array.Empty<VolumeSnapshotDto>();
    private ProcessSnapshotDto? _latestProcessSnapshot;

    public string AgentVersion =>
        typeof(AgentRuntimeState).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public DateTimeOffset? LastHeartbeatUtc
    {
        get { lock (_lock) { return _lastHeartbeatUtc; } }
    }

    public bool ProtectedModeActive
    {
        get { lock (_lock) { return _protectedModeActive; } }
        set { lock (_lock) { _protectedModeActive = value; } }
    }

    public MetricSampleDto? LatestSample
    {
        get { lock (_lock) { return _latestSample; } }
    }

    public IReadOnlyList<VolumeSnapshotDto> LatestVolumes
    {
        get { lock (_lock) { return _latestVolumes; } }
    }

    public ProcessSnapshotDto? LatestProcessSnapshot
    {
        get { lock (_lock) { return _latestProcessSnapshot; } }
    }

    public void RecordHeartbeatSuccess(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            _lastHeartbeatUtc = utcNow;
        }
    }

    public void UpdateSample(MetricSampleDto sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_lock)
        {
            _latestSample = sample;
        }
    }

    public void UpdateVolumes(IReadOnlyList<VolumeSnapshotDto> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        lock (_lock)
        {
            _latestVolumes = volumes;
        }
    }

    public void UpdateProcessSnapshot(ProcessSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            _latestProcessSnapshot = snapshot;
        }
    }

    public ConnectivityState GetConnectivityState(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            return DeviceConnectivityEvaluator.Evaluate(_lastHeartbeatUtc, utcNow);
        }
    }

    public IpcLocalTelemetryResponse GetLocalTelemetryResponse(DateTimeOffset utcNow) =>
        BuildBoundedLocalTelemetry(utcNow, Guid.Empty.ToString("N")).Response;

    internal BoundedLocalTelemetry BuildBoundedLocalTelemetry(DateTimeOffset utcNow, string correlationId)
    {
        MetricSampleDto? latestSample;
        IReadOnlyList<VolumeSnapshotDto> latestVolumes;
        ProcessSnapshotDto? latestProcessSnapshot;
        DateTimeOffset? lastHeartbeatUtc;
        ConnectivityState connectivity;

        lock (_lock)
        {
            latestSample = _latestSample;
            latestVolumes = _latestVolumes;
            latestProcessSnapshot = _latestProcessSnapshot;
            lastHeartbeatUtc = _lastHeartbeatUtc;
            connectivity = DeviceConnectivityEvaluator.Evaluate(_lastHeartbeatUtc, utcNow);
        }

        var originalProcessCount = latestProcessSnapshot?.Processes.Count ?? 0;
        var orderedProcesses = latestProcessSnapshot is { Processes.Count: > 0 }
            ? latestProcessSnapshot.Processes
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(MaxIpcProcesses)
                .ToArray()
            : Array.Empty<ProcessEntryDto>();

        var allVolumes = latestVolumes as VolumeSnapshotDto[] ?? latestVolumes.ToArray();
        var processCount = orderedProcesses.Length;
        var volumeCount = allVolumes.Length;
        var keepSnapshot = latestProcessSnapshot is not null;
        var keepSample = latestSample is not null;
        int? maxImageNameLength = null;
        int? maxVolumeIdLength = null;

        var targetMaxBytes = ProtocolConstants.MaxIpcMessageBytes - IpcEnvelopeSafetyMarginBytes;
        var connectivityText = connectivity.ToString();
        var effectiveCorrelationId = correlationId ?? string.Empty;

        var (response, envelope, byteCount) = BuildCandidate();

        while (byteCount > targetMaxBytes && processCount > 0)
        {
            if (processCount == 1 && maxImageNameLength is null)
            {
                maxImageNameLength = FallbackTruncatedNameLength;
            }
            else
            {
                processCount /= 2;
                maxImageNameLength = null;
            }

            (response, envelope, byteCount) = BuildCandidate();
        }

        while (byteCount > targetMaxBytes && volumeCount > 0)
        {
            if (volumeCount == 1 && maxVolumeIdLength is null)
            {
                maxVolumeIdLength = FallbackTruncatedNameLength;
            }
            else
            {
                volumeCount /= 2;
                maxVolumeIdLength = null;
            }

            (response, envelope, byteCount) = BuildCandidate();
        }

        if (byteCount > targetMaxBytes && keepSnapshot)
        {
            keepSnapshot = false;
            (response, envelope, byteCount) = BuildCandidate();
        }

        if (byteCount > targetMaxBytes && keepSample)
        {
            keepSample = false;
            (response, envelope, byteCount) = BuildCandidate();
        }

        var returnedProcessCount = response.LatestProcessSnapshot?.Processes.Count ?? 0;
        return new BoundedLocalTelemetry(response, envelope, originalProcessCount, returnedProcessCount);

        (IpcLocalTelemetryResponse Response, IpcEnvelope Envelope, int ByteCount) BuildCandidate()
        {
            ProcessSnapshotDto? snapshot = null;
            if (keepSnapshot && latestProcessSnapshot is not null)
            {
                var processes = SliceProcesses(orderedProcesses, processCount, maxImageNameLength);
                snapshot = latestProcessSnapshot with { Processes = processes };
            }

            var volumes = SliceVolumes(allVolumes, volumeCount, maxVolumeIdLength);
            var candidateResponse = new IpcLocalTelemetryResponse(
                keepSample ? latestSample : null,
                volumes,
                snapshot,
                connectivityText,
                lastHeartbeatUtc);

            var candidateEnvelope = new IpcEnvelope(
                ProtocolConstants.IpcProtocolVersion,
                IpcMessageTypes.GetLocalTelemetry,
                effectiveCorrelationId,
                NamedPipeFraming.SerializePayload(candidateResponse));

            var size = JsonSerializer.SerializeToUtf8Bytes(candidateEnvelope, NamedPipeFraming.SerializerOptions).Length;
            return (candidateResponse, candidateEnvelope, size);
        }
    }

    private static ProcessEntryDto[] SliceProcesses(
        ProcessEntryDto[] orderedProcesses,
        int count,
        int? maxImageNameLength)
    {
        if (count <= 0 || orderedProcesses.Length == 0)
        {
            return Array.Empty<ProcessEntryDto>();
        }

        var actualCount = Math.Min(count, orderedProcesses.Length);
        var slice = actualCount == orderedProcesses.Length
            ? orderedProcesses
            : orderedProcesses[..actualCount];

        if (maxImageNameLength is not int maxLen)
        {
            return slice;
        }

        return slice
            .Select(p => p.ImageName.Length > maxLen ? p with { ImageName = p.ImageName[..maxLen] } : p)
            .ToArray();
    }

    private static VolumeSnapshotDto[] SliceVolumes(
        VolumeSnapshotDto[] allVolumes,
        int count,
        int? maxVolumeIdLength)
    {
        if (count <= 0 || allVolumes.Length == 0)
        {
            return Array.Empty<VolumeSnapshotDto>();
        }

        var actualCount = Math.Min(count, allVolumes.Length);
        var slice = actualCount == allVolumes.Length
            ? allVolumes
            : allVolumes[..actualCount];

        if (maxVolumeIdLength is not int maxLen)
        {
            return slice;
        }

        return slice
            .Select(v => v.VolumeId.Length > maxLen ? v with { VolumeId = v.VolumeId[..maxLen] } : v)
            .ToArray();
    }

    internal readonly record struct BoundedLocalTelemetry(
        IpcLocalTelemetryResponse Response,
        IpcEnvelope Envelope,
        int OriginalProcessCount,
        int ReturnedProcessCount);
}
