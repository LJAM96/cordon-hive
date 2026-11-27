using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hive.Contracts;
using Hive.Domain.Telemetry;

namespace Hive.Windows.Telemetry;

/// <summary>
/// Win32 P/Invoke and managed <see cref="DriveInfo"/>/<see cref="Process"/> telemetry collector.
/// Never uses WMI, never scans directories or files, and never reads MainWindowTitle or command lines.
/// </summary>
public sealed class WindowsTelemetryCollector : ITelemetryCollector
{
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _cpuLock = new();
    private readonly object _processLock = new();

    private bool _hasCpuBaseline;
    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;

    private DateTimeOffset? _prevProcessSampleAtUtc;
    private Dictionary<(int Pid, DateTimeOffset? StartTimeUtc), TimeSpan> _prevProcessCpuTimes = new();

    public WindowsTelemetryCollector(Func<DateTimeOffset>? utcNowProvider = null)
    {
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public MetricSampleDto SampleSystemMetrics(Guid bootId, long sequence)
    {
        var nowUtc = _utcNowProvider();
        var cpuPercent = SampleCpuPercent();
        var (memUsedPercent, memUsedBytes, memTotalBytes) = SampleMemoryStatus();

        return new MetricSampleDto(
            BootId: bootId,
            SampleSequence: sequence,
            SampledAtUtc: nowUtc,
            CpuPercent: cpuPercent,
            MemoryUsedPercent: memUsedPercent,
            MemoryUsedBytes: memUsedBytes,
            MemoryTotalBytes: memTotalBytes,
            GpuPercent: null,
            DiskActivePercent: null);
    }

    public IReadOnlyList<VolumeSnapshotDto> CaptureVolumes()
    {
        var nowUtc = _utcNowProvider();
        var volumes = new List<VolumeSnapshotDto>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return volumes;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                if (!drive.IsReady)
                {
                    continue;
                }

                var totalBytes = Math.Max(0L, drive.TotalSize);
                var freeBytes = Math.Clamp(drive.AvailableFreeSpace, 0L, totalBytes);
                volumes.Add(new VolumeSnapshotDto(
                    TelemetrySanitizer.SanitizeVolumeId(drive.Name),
                    drive.DriveType.ToString(),
                    totalBytes,
                    freeBytes,
                    nowUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException)
            {
                // Skip inaccessible or transiently disconnected drives without failing the batch.
            }
        }

        return volumes;
    }

    public ProcessSnapshotDto CaptureProcessSnapshot(int maxProcesses = 200)
    {
        var cap = Math.Clamp(maxProcesses, 1, 500);
        var nowUtc = _utcNowProvider();
        var processes = Process.GetProcesses();
        var rawEntries = new List<(string ImageName, int Pid, DateTimeOffset? StartTimeUtc, TimeSpan? TotalCpu, long WorkingSetBytes)>(processes.Length);

        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    var pid = proc.Id;
                    var imageName = TelemetrySanitizer.SanitizeImageName(proc.ProcessName);
                    long workingSet;
                    try
                    {
                        workingSet = Math.Max(0L, proc.WorkingSet64);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        continue;
                    }

                    DateTimeOffset? startTimeUtc = null;
                    try
                    {
                        startTimeUtc = new DateTimeOffset(proc.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Protected system processes often deny StartTime; keep null.
                    }

                    TimeSpan? totalCpu = null;
                    try
                    {
                        totalCpu = proc.TotalProcessorTime;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Protected system processes often deny TotalProcessorTime; keep null.
                    }

                    rawEntries.Add((imageName, pid, startTimeUtc, totalCpu, workingSet));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Process exited during enumeration; skip cleanly.
                }
            }
        }
        finally
        {
            foreach (var proc in processes)
            {
                proc.Dispose();
            }
        }

        var nextCpuMap = new Dictionary<(int Pid, DateTimeOffset? StartTimeUtc), TimeSpan>(rawEntries.Count);
        var entries = new List<ProcessEntryDto>(rawEntries.Count);
        var logicalCores = Math.Max(1, Environment.ProcessorCount);

        lock (_processLock)
        {
            var elapsedMs = _prevProcessSampleAtUtc.HasValue
                ? (nowUtc - _prevProcessSampleAtUtc.Value).TotalMilliseconds
                : 0.0;

            foreach (var item in rawEntries)
            {
                var key = (item.Pid, item.StartTimeUtc);
                double? cpuPercent = null;

                if (item.TotalCpu.HasValue)
                {
                    nextCpuMap[key] = item.TotalCpu.Value;

                    if (elapsedMs > 0 && _prevProcessCpuTimes.TryGetValue(key, out var prevCpu))
                    {
                        var deltaCpuMs = (item.TotalCpu.Value - prevCpu).TotalMilliseconds;
                        if (deltaCpuMs >= 0)
                        {
                            cpuPercent = TelemetrySanitizer.ClampPercent((deltaCpuMs / (elapsedMs * logicalCores)) * 100.0);
                        }
                    }
                }

                entries.Add(new ProcessEntryDto(
                    item.ImageName,
                    item.Pid,
                    item.StartTimeUtc,
                    cpuPercent,
                    item.WorkingSetBytes));
            }

            _prevProcessCpuTimes = nextCpuMap;
            _prevProcessSampleAtUtc = nowUtc;
        }

        var bounded = entries
            .OrderByDescending(p => p.WorkingSetBytes)
            .ThenBy(p => p.Pid)
            .Take(cap)
            .ToArray();

        return new ProcessSnapshotDto(
            Guid.NewGuid(),
            nowUtc,
            bounded);
    }

    private double? SampleCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return null;
        }

        var idle = ToUInt64(idleFt);
        var kernel = ToUInt64(kernelFt);
        var user = ToUInt64(userFt);

        lock (_cpuLock)
        {
            if (!_hasCpuBaseline)
            {
                _prevIdleTime = idle;
                _prevKernelTime = kernel;
                _prevUserTime = user;
                _hasCpuBaseline = true;
                return null;
            }

            var deltaIdle = idle >= _prevIdleTime ? idle - _prevIdleTime : 0UL;
            var deltaKernel = kernel >= _prevKernelTime ? kernel - _prevKernelTime : 0UL;
            var deltaUser = user >= _prevUserTime ? user - _prevUserTime : 0UL;

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;

            // On Windows, GetSystemTimes kernelTime includes idleTime.
            var total = deltaKernel + deltaUser;
            if (total == 0)
            {
                return null;
            }

            var busy = total >= deltaIdle ? total - deltaIdle : 0UL;
            return TelemetrySanitizer.ClampPercent((busy * 100.0) / total);
        }
    }

    private static (double? UsedPercent, long? UsedBytes, long? TotalBytes) SampleMemoryStatus()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return (null, null, null);
        }

        var totalUlong = status.ullTotalPhys;
        var usedUlong = status.ullTotalPhys >= status.ullAvailPhys
            ? status.ullTotalPhys - status.ullAvailPhys
            : 0UL;

        var totalBytes = (long)Math.Min(totalUlong, (ulong)long.MaxValue);
        var usedBytes = (long)Math.Min(usedUlong, (ulong)totalBytes);
        var usedPercent = TelemetrySanitizer.ClampPercent((usedUlong * 100.0) / totalUlong);

        return (usedPercent, usedBytes, totalBytes);
    }

    private static ulong ToUInt64(NativeMethods.FILETIME ft) =>
        ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
