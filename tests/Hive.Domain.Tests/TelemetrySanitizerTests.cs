using Hive.Contracts;
using Hive.Domain.Telemetry;
using Xunit;

namespace Hive.Domain.Tests;

public sealed class TelemetrySanitizerTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Tool\imager.exe --case 123", "imager.exe")]
    [InlineData("\"" + @"C:\Program Files\Tool\imager.exe" + "\"", "imager.exe")]
    [InlineData("/usr/bin/app arg", "app")]
    [InlineData("/opt/forensics/collector --verbose", "collector")]
    [InlineData("svchost.exe -k netsvcs", "svchost.exe")]
    [InlineData("python3 script.py", "python3")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("\"   \"", "unknown")]
    public void SanitizeImageName_StripsPathsAndArguments(string? rawName, string expected)
    {
        var result = TelemetrySanitizer.SanitizeImageName(rawName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeImageName_TruncatesOverMaxLength()
    {
        var longName = new string('a', TelemetrySanitizer.MaxImageNameLength + 25);
        var result = TelemetrySanitizer.SanitizeImageName(longName);

        Assert.Equal(TelemetrySanitizer.MaxImageNameLength, result.Length);
        Assert.Equal(new string('a', TelemetrySanitizer.MaxImageNameLength), result);
    }

    [Fact]
    public void ClampPercent_NullRemainsNull()
    {
        Assert.Null(TelemetrySanitizer.ClampPercent(null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampPercent_NonFiniteReturnsNull(double value)
    {
        Assert.Null(TelemetrySanitizer.ClampPercent(value));
    }

    [Theory]
    [InlineData(-0.001, 0.0)]
    [InlineData(-42.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(54.25, 54.25)]
    [InlineData(100.0, 100.0)]
    [InlineData(100.001, 100.0)]
    [InlineData(999.9, 100.0)]
    public void ClampPercent_ClampsOutOfRangeValues(double input, double expected)
    {
        var result = TelemetrySanitizer.ClampPercent(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeVolume_ClampsFreeBytesToTotalBytesAndNormalizesDrivePath()
    {
        var deviceId = Guid.NewGuid();
        var observedAt = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var dto = new VolumeSnapshotDto(
            VolumeId: @"c:\Cases\Evidence",
            DriveType: "  Fixed  ",
            TotalBytes: 500_000_000_000L,
            FreeBytes: 750_000_000_000L,
            ObservedAtUtc: observedAt);

        var volume = TelemetrySanitizer.SanitizeVolume(deviceId, dto);

        Assert.Equal(deviceId, volume.DeviceId);
        Assert.Equal(@"C:\", volume.VolumeId);
        Assert.Equal("Fixed", volume.DriveType);
        Assert.Equal(500_000_000_000L, volume.TotalBytes);
        Assert.Equal(500_000_000_000L, volume.FreeBytes);
        Assert.Equal(observedAt, volume.ObservedAtUtc);
    }

    [Fact]
    public void SanitizeVolume_NegativeValuesClampedToZeroAndEmptyDriveTypeDefaultsToUnknown()
    {
        var deviceId = Guid.NewGuid();
        var dto = new VolumeSnapshotDto(
            VolumeId: "d:",
            DriveType: "   ",
            TotalBytes: -100,
            FreeBytes: -50,
            ObservedAtUtc: DateTimeOffset.UtcNow);

        var volume = TelemetrySanitizer.SanitizeVolume(deviceId, dto);

        Assert.Equal(@"D:\", volume.VolumeId);
        Assert.Equal("Unknown", volume.DriveType);
        Assert.Equal(0L, volume.TotalBytes);
        Assert.Equal(0L, volume.FreeBytes);
    }

    [Fact]
    public void SanitizeProcessSnapshot_CapsCountAndSortsByWorkingSetDescending()
    {
        var deviceId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var sampledAt = new DateTimeOffset(2026, 11, 27, 12, 5, 0, TimeSpan.Zero);

        var entries = new[]
        {
            new ProcessEntryDto(@"C:\Tools\small.exe --secret", 101, null, 1.5, 10_000),
            new ProcessEntryDto(@"C:\Tools\largest.exe --token abc", 202, null, 150.0, 900_000),
            new ProcessEntryDto("/usr/bin/medium --flag", 303, null, -5.0, 450_000),
            new ProcessEntryDto("tiny.exe", 404, null, double.NaN, 5_000),
            new ProcessEntryDto("second.exe", 505, null, 12.0, 600_000)
        };

        var dto = new ProcessSnapshotDto(snapshotId, sampledAt, entries);
        var sanitized = TelemetrySanitizer.SanitizeProcessSnapshot(deviceId, dto, maxProcesses: 3);

        Assert.Equal(snapshotId, sanitized.SnapshotId);
        Assert.Equal(deviceId, sanitized.DeviceId);
        Assert.Equal(sampledAt, sanitized.SampledAtUtc);
        Assert.Equal(3, sanitized.Processes.Count);

        Assert.Equal("largest.exe", sanitized.Processes[0].ImageName);
        Assert.Equal(202, sanitized.Processes[0].Pid);
        Assert.Equal(900_000L, sanitized.Processes[0].WorkingSetBytes);
        Assert.Equal(100.0, sanitized.Processes[0].CpuPercent);

        Assert.Equal("second.exe", sanitized.Processes[1].ImageName);
        Assert.Equal(505, sanitized.Processes[1].Pid);
        Assert.Equal(600_000L, sanitized.Processes[1].WorkingSetBytes);
        Assert.Equal(12.0, sanitized.Processes[1].CpuPercent);

        Assert.Equal("medium", sanitized.Processes[2].ImageName);
        Assert.Equal(303, sanitized.Processes[2].Pid);
        Assert.Equal(450_000L, sanitized.Processes[2].WorkingSetBytes);
        Assert.Equal(0.0, sanitized.Processes[2].CpuPercent);
    }

    [Fact]
    public void SanitizeProcessSnapshot_DefaultMaxProcessesCapsAt200()
    {
        var entries = Enumerable.Range(1, 250)
            .Select(i => new ProcessEntryDto($"proc{i}.exe", i, null, 1.0, i * 1024L))
            .ToArray();

        var dto = new ProcessSnapshotDto(Guid.Empty, DateTimeOffset.UtcNow, entries);
        var sanitized = TelemetrySanitizer.SanitizeProcessSnapshot(Guid.NewGuid(), dto);

        Assert.NotEqual(Guid.Empty, sanitized.SnapshotId);
        Assert.Equal(TelemetrySanitizer.DefaultMaxProcesses, sanitized.Processes.Count);
        Assert.Equal("proc250.exe", sanitized.Processes[0].ImageName);
        Assert.Equal("proc51.exe", sanitized.Processes[^1].ImageName);
    }
}
