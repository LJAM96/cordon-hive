namespace Hive.Contracts;

public sealed record MetricSampleDto(
    Guid BootId,
    long SampleSequence,
    DateTimeOffset SampledAtUtc,
    double? CpuPercent,
    double? MemoryUsedPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? GpuPercent,
    double? DiskActivePercent);

public sealed record VolumeSnapshotDto(
    string VolumeId,
    string DriveType,
    long TotalBytes,
    long FreeBytes,
    DateTimeOffset ObservedAtUtc);

public sealed record ProcessEntryDto(
    string ImageName,
    int Pid,
    DateTimeOffset? StartTimeUtc,
    double? CpuPercent,
    long WorkingSetBytes);

public sealed record ProcessSnapshotDto(
    Guid SnapshotId,
    DateTimeOffset SampledAtUtc,
    IReadOnlyList<ProcessEntryDto> Processes);

public sealed record MetricsBatchRequest(
    IReadOnlyList<MetricSampleDto> Samples,
    IReadOnlyList<VolumeSnapshotDto>? Volumes,
    ProcessSnapshotDto? ProcessSnapshot);

public sealed record MetricsBatchResponse(
    int AcceptedSamples,
    int DuplicateSamples,
    DateTimeOffset ServerTimeUtc);

public sealed record DeviceTelemetryResponse(
    Guid DeviceId,
    string ConnectivityState,
    DateTimeOffset? LastSeenUtc,
    MetricSampleDto? LatestSample,
    IReadOnlyList<MetricSampleDto> RecentSamples,
    IReadOnlyList<VolumeSnapshotDto> Volumes,
    ProcessSnapshotDto? LatestProcessSnapshot);
