using Hive.Contracts;
using Hive.Domain.Devices;
using Hive.Domain.Telemetry;

namespace Hive.App.ViewModels;

public static class TelemetryPresentation
{
    public const string UnavailableOfflineText = "Unavailable (Offline)";
    public const string UnsupportedUnknownText = "Unsupported / Unknown";

    public static ConnectivityState ParseConnectivity(string? rawState, DateTimeOffset? lastSeenUtc)
    {
        if (!string.IsNullOrWhiteSpace(rawState) &&
            Enum.TryParse<ConnectivityState>(rawState, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return DeviceConnectivityEvaluator.Evaluate(lastSeenUtc, DateTimeOffset.UtcNow);
    }

    public static string GetConnectivityText(ConnectivityState state) => state switch
    {
        ConnectivityState.Online => "Online",
        ConnectivityState.Stale => "Stale",
        _ => "Offline"
    };

    public static string GetConnectivityGlyph(ConnectivityState state) => state switch
    {
        ConnectivityState.Online => "\uE73E",
        ConnectivityState.Stale => "\uE7BA",
        _ => "\uE711"
    };

    public static string FormatLastSeenLocal(DateTimeOffset? lastSeenUtc) =>
        lastSeenUtc is { } seen
            ? seen.ToLocalTime().ToString("g")
            : "Never";

    public static string FormatPercentMetric(double? rawPercent, bool isOffline)
    {
        if (isOffline)
        {
            return UnavailableOfflineText;
        }

        var clamped = TelemetrySanitizer.ClampPercent(rawPercent);
        return clamped is { } value
            ? $"{value:0.0}%"
            : UnsupportedUnknownText;
    }

    public static string FormatMemoryMetric(MetricSampleDto? sample, bool isOffline)
    {
        if (isOffline)
        {
            return UnavailableOfflineText;
        }

        if (sample is null)
        {
            return UnsupportedUnknownText;
        }

        var percent = TelemetrySanitizer.ClampPercent(sample.MemoryUsedPercent);
        if (percent is null)
        {
            return UnsupportedUnknownText;
        }

        if (sample.MemoryUsedBytes is >= 0 && sample.MemoryTotalBytes is > 0)
        {
            var usedGiB = sample.MemoryUsedBytes.Value / (1024.0 * 1024.0 * 1024.0);
            var totalGiB = sample.MemoryTotalBytes.Value / (1024.0 * 1024.0 * 1024.0);
            return $"{percent.Value:0.0}% ({usedGiB:0.0} / {totalGiB:0.0} GiB)";
        }

        return $"{percent.Value:0.0}%";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "Unknown";
        }

        const double giB = 1024.0 * 1024.0 * 1024.0;
        const double miB = 1024.0 * 1024.0;

        if (bytes >= giB)
        {
            return $"{bytes / giB:0.0} GiB";
        }

        return $"{bytes / miB:0.0} MiB";
    }
}

public sealed class VolumeItemViewModel
{
    public VolumeItemViewModel(VolumeSnapshotDto dto)
    {
        var total = Math.Max(0L, dto.TotalBytes);
        var free = Math.Clamp(dto.FreeBytes, 0L, total);

        VolumeId = TelemetrySanitizer.SanitizeVolumeId(dto.VolumeId);
        DriveType = string.IsNullOrWhiteSpace(dto.DriveType) ? "Unknown" : dto.DriveType.Trim();
        TotalBytes = total;
        FreeBytes = free;
        HealthLevel = StorageWarningEvaluator.Evaluate(dto.TotalBytes, dto.FreeBytes);

        if (total > 0)
        {
            var used = total - free;
            UsedPercent = Math.Clamp(used * 100.0 / total, 0.0, 100.0);
            CapacitySummaryText =
                $"{TelemetryPresentation.FormatBytes(free)} free of {TelemetryPresentation.FormatBytes(total)} ({UsedPercent:0.0}% used)";
        }
        else
        {
            UsedPercent = 0.0;
            CapacitySummaryText = "Capacity unknown";
        }

        ObservedLocalText = $"Observed {dto.ObservedAtUtc.ToLocalTime():g}";
    }

    public string VolumeId { get; }
    public string DriveType { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }
    public double UsedPercent { get; }
    public string CapacitySummaryText { get; }
    public string ObservedLocalText { get; }
    public StorageHealthLevel HealthLevel { get; }

    public string HealthText => HealthLevel switch
    {
        StorageHealthLevel.Healthy => "Healthy",
        StorageHealthLevel.Warning => "Warning",
        StorageHealthLevel.Critical => "Critical",
        _ => "Unknown"
    };

    public string HealthGlyph => HealthLevel switch
    {
        StorageHealthLevel.Healthy => "\uE73E",
        StorageHealthLevel.Warning => "\uE7BA",
        StorageHealthLevel.Critical => "\uE783",
        _ => "\uE9CE"
    };

    public string HeaderText => $"{VolumeId} ({DriveType})";
    public string AccessibilityName => $"Volume {VolumeId}, {HealthText}, {CapacitySummaryText}";
}

public sealed class ProcessItemViewModel
{
    public ProcessItemViewModel(ProcessEntryDto dto)
    {
        ImageName = TelemetrySanitizer.SanitizeImageName(dto.ImageName);
        Pid = Math.Max(0, dto.Pid);
        WorkingSetBytes = Math.Max(0L, dto.WorkingSetBytes);
        CpuPercent = TelemetrySanitizer.ClampPercent(dto.CpuPercent);
        StartTimeLocalText = dto.StartTimeUtc is { } started
            ? started.ToLocalTime().ToString("g")
            : "Unknown";
    }

    public string ImageName { get; }
    public int Pid { get; }
    public long WorkingSetBytes { get; }
    public double? CpuPercent { get; }
    public string StartTimeLocalText { get; }

    public string PidDisplay => $"PID {Pid}";
    public string WorkingSetMibText => $"{WorkingSetBytes / (1024.0 * 1024.0):0.0} MiB";
    public string CpuPercentText => CpuPercent is { } cpu ? $"{cpu:0.0}% CPU" : "CPU Unknown";
}
