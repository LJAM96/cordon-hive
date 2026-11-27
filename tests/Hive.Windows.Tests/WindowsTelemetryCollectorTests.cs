using System.Diagnostics;
using Hive.Windows.Telemetry;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class WindowsTelemetryCollectorTests
{
    [Fact]
    public void SampleSystemMetrics_ReturnsValidMemoryAndSecondSampleComputesCpuPercent()
    {
        var collector = new WindowsTelemetryCollector();
        var bootId = Guid.NewGuid();

        var first = collector.SampleSystemMetrics(bootId, sequence: 1);

        Assert.Equal(bootId, first.BootId);
        Assert.Equal(1, first.SampleSequence);
        Assert.Null(first.CpuPercent);
        Assert.Null(first.GpuPercent);
        Assert.Null(first.DiskActivePercent);
        Assert.NotNull(first.MemoryTotalBytes);
        Assert.True(first.MemoryTotalBytes > 0);
        Assert.NotNull(first.MemoryUsedBytes);
        Assert.InRange(first.MemoryUsedBytes!.Value, 0, first.MemoryTotalBytes!.Value);
        Assert.NotNull(first.MemoryUsedPercent);
        Assert.InRange(first.MemoryUsedPercent!.Value, 0.0, 100.0);

        // Allow kernel/user CPU counters to advance before taking the second sample.
        Thread.Sleep(50);

        var second = collector.SampleSystemMetrics(bootId, sequence: 2);

        Assert.Equal(bootId, second.BootId);
        Assert.Equal(2, second.SampleSequence);
        Assert.NotNull(second.CpuPercent);
        Assert.InRange(second.CpuPercent!.Value, 0.0, 100.0);
        Assert.Null(second.GpuPercent);
        Assert.Null(second.DiskActivePercent);
    }

    [Fact]
    public void CaptureVolumes_ReturnsReadyVolumesWithValidCapacityBounds()
    {
        var collector = new WindowsTelemetryCollector();

        var volumes = collector.CaptureVolumes();

        Assert.NotEmpty(volumes);
        foreach (var volume in volumes)
        {
            Assert.False(string.IsNullOrWhiteSpace(volume.VolumeId));
            Assert.False(string.IsNullOrWhiteSpace(volume.DriveType));
            Assert.True(volume.TotalBytes > 0);
            Assert.InRange(volume.FreeBytes, 0L, volume.TotalBytes);
        }
    }

    [Fact]
    public void CaptureProcessSnapshot_ReturnsSanitizedNamesAndCompletesRapidly()
    {
        var collector = new WindowsTelemetryCollector();

        var sw = Stopwatch.StartNew();
        var first = collector.CaptureProcessSnapshot(maxProcesses: 200);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"CaptureProcessSnapshot took {sw.ElapsedMilliseconds}ms.");
        Assert.NotEqual(Guid.Empty, first.SnapshotId);
        Assert.NotEmpty(first.Processes);
        Assert.True(first.Processes.Count <= 200);

        foreach (var proc in first.Processes)
        {
            Assert.False(string.IsNullOrWhiteSpace(proc.ImageName));
            Assert.DoesNotContain('\\', proc.ImageName);
            Assert.DoesNotContain('/', proc.ImageName);
            Assert.False(proc.ImageName.Any(char.IsWhiteSpace), $"Unexpected whitespace in ImageName '{proc.ImageName}'.");
            Assert.True(proc.Pid >= 0);
            Assert.True(proc.WorkingSetBytes >= 0);
        }

        Thread.Sleep(30);
        var second = collector.CaptureProcessSnapshot(maxProcesses: 50);
        Assert.True(second.Processes.Count <= 50);
        foreach (var proc in second.Processes)
        {
            if (proc.CpuPercent.HasValue)
            {
                Assert.InRange(proc.CpuPercent.Value, 0.0, 100.0);
            }
        }
    }
}
