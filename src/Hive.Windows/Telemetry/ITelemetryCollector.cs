using Hive.Contracts;

namespace Hive.Windows.Telemetry;

/// <summary>
/// Captures privacy-preserving Windows host telemetry without WMI, directory scans,
/// window titles, or process command lines.
/// </summary>
public interface ITelemetryCollector
{
    MetricSampleDto SampleSystemMetrics(Guid bootId, long sequence);

    IReadOnlyList<VolumeSnapshotDto> CaptureVolumes();

    ProcessSnapshotDto CaptureProcessSnapshot(int maxProcesses = 200);
}
