using Hive.Contracts;

namespace Hive.Domain.Telemetry;

public enum StorageHealthLevel
{
    Healthy,
    Warning,
    Critical,
    Unknown
}

public sealed record DeviceMetricSample(
    Guid DeviceId,
    Guid BootId,
    long SampleSequence,
    DateTimeOffset SampledAtUtc,
    DateTimeOffset ReceivedAtUtc,
    double? CpuPercent,
    double? MemoryUsedPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? GpuPercent,
    double? DiskActivePercent);

public sealed record DeviceVolume(
    Guid DeviceId,
    string VolumeId,
    string DriveType,
    long TotalBytes,
    long FreeBytes,
    DateTimeOffset ObservedAtUtc);

public sealed record DeviceProcessEntry(
    string ImageName,
    int Pid,
    DateTimeOffset? StartTimeUtc,
    double? CpuPercent,
    long WorkingSetBytes);

public sealed record DeviceProcessSnapshot(
    Guid SnapshotId,
    Guid DeviceId,
    DateTimeOffset SampledAtUtc,
    IReadOnlyList<DeviceProcessEntry> Processes);

/// <summary>
/// Sanitizes incoming telemetry so process command lines, file paths, and out-of-range
/// numbers are stripped before persistence or display, while preserving null for unsupported metrics.
/// </summary>
public static class TelemetrySanitizer
{
    public const int MaxImageNameLength = 128;
    public const int DefaultMaxProcesses = 200;

    public static double? ClampPercent(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0.0, 100.0);
    }

    public static string SanitizeImageName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "unknown";
        }

        var trimmed = rawName.Trim().Trim('"');

        // Strip any accidental arguments after whitespace if no path separator follows,
        // or extract executable name first when paths contain spaces.
        var lastSlash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (lastSlash >= 0 && lastSlash + 1 < trimmed.Length)
        {
            trimmed = trimmed[(lastSlash + 1)..];
        }

        var exeIdx = trimmed.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            trimmed = trimmed[..(exeIdx + 4)];
        }
        else
        {
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                trimmed = trimmed[..spaceIdx];
            }
        }

        trimmed = trimmed.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "unknown";
        }

        return trimmed.Length <= MaxImageNameLength ? trimmed : trimmed[..MaxImageNameLength];
    }

    public static string SanitizeVolumeId(string? rawVolumeId)
    {
        if (string.IsNullOrWhiteSpace(rawVolumeId))
        {
            return "unknown";
        }

        var trimmed = rawVolumeId.Trim();
        // Keep drive letter root (e.g., "C:\") or short label; avoid deep directory paths.
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }

        var slashIdx = trimmed.IndexOfAny(['\\', '/']);
        if (slashIdx > 0)
        {
            trimmed = trimmed[..slashIdx];
        }

        return trimmed.Length <= 64 ? trimmed : trimmed[..64];
    }

    public static DeviceMetricSample SanitizeSample(
        Guid deviceId,
        MetricSampleDto dto,
        DateTimeOffset receivedAtUtc)
    {
        var totalMem = dto.MemoryTotalBytes is > 0 ? dto.MemoryTotalBytes : null;
        var usedMem = dto.MemoryUsedBytes is >= 0 ? dto.MemoryUsedBytes : null;
        if (totalMem.HasValue && usedMem.HasValue && usedMem.Value > totalMem.Value)
        {
            usedMem = totalMem.Value;
        }

        return new DeviceMetricSample(
            deviceId,
            dto.BootId,
            dto.SampleSequence,
            dto.SampledAtUtc,
            receivedAtUtc,
            ClampPercent(dto.CpuPercent),
            ClampPercent(dto.MemoryUsedPercent),
            usedMem,
            totalMem,
            ClampPercent(dto.GpuPercent),
            ClampPercent(dto.DiskActivePercent));
    }

    public static DeviceVolume SanitizeVolume(Guid deviceId, VolumeSnapshotDto dto)
    {
        var total = Math.Max(0L, dto.TotalBytes);
        var free = Math.Clamp(dto.FreeBytes, 0L, total);
        var driveType = string.IsNullOrWhiteSpace(dto.DriveType) ? "Unknown" : dto.DriveType.Trim();
        if (driveType.Length > 32)
        {
            driveType = driveType[..32];
        }

        return new DeviceVolume(
            deviceId,
            SanitizeVolumeId(dto.VolumeId),
            driveType,
            total,
            free,
            dto.ObservedAtUtc);
    }

    public static DeviceProcessSnapshot SanitizeProcessSnapshot(
        Guid deviceId,
        ProcessSnapshotDto dto,
        int maxProcesses = DefaultMaxProcesses)
    {
        var cap = Math.Clamp(maxProcesses, 1, 500);
        var sanitized = (dto.Processes ?? Array.Empty<ProcessEntryDto>())
            .Select(p => new DeviceProcessEntry(
                SanitizeImageName(p.ImageName),
                Math.Max(0, p.Pid),
                p.StartTimeUtc,
                ClampPercent(p.CpuPercent),
                Math.Max(0L, p.WorkingSetBytes)))
            .OrderByDescending(p => p.WorkingSetBytes)
            .ThenBy(p => p.Pid)
            .Take(cap)
            .ToArray();

        return new DeviceProcessSnapshot(
            dto.SnapshotId == Guid.Empty ? Guid.NewGuid() : dto.SnapshotId,
            deviceId,
            dto.SampledAtUtc,
            sanitized);
    }
}

public static class StorageWarningEvaluator
{
    public const long WarningFreeBytesThreshold = 20L * 1024 * 1024 * 1024; // 20 GiB
    public const long CriticalFreeBytesThreshold = 5L * 1024 * 1024 * 1024; // 5 GiB

    public static StorageHealthLevel Evaluate(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0 || freeBytes < 0)
        {
            return StorageHealthLevel.Unknown;
        }

        var clampedFree = Math.Min(freeBytes, totalBytes);
        var freeRatio = (double)clampedFree / totalBytes;

        if (freeRatio < 0.05 || clampedFree < CriticalFreeBytesThreshold)
        {
            return StorageHealthLevel.Critical;
        }

        if (freeRatio < 0.10 || clampedFree < WarningFreeBytesThreshold)
        {
            return StorageHealthLevel.Warning;
        }

        return StorageHealthLevel.Healthy;
    }
}
