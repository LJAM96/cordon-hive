using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cordon.Shared.Windows;

public sealed record TaskbarSnapshot(
    MonitorWorkArea Monitor,
    PixelRect? TaskbarBounds,
    TaskbarEdge Edge,
    bool IsAutoHide);

/// <summary>
/// Safely queries the primary Windows taskbar and monitor work area geometry via Win32 APIs.
/// </summary>
public static class TaskbarLocator
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const long AbsAutoHide = 0x00000001;

    private const uint AbeLeft = 0;
    private const uint AbeTop = 1;
    private const uint AbeRight = 2;
    private const uint AbeBottom = 3;

    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const double StandardDpi = 96.0;
    private const int FallbackScreenWidth = 1920;
    private const int FallbackScreenHeight = 1080;

    public static TaskbarSnapshot CapturePrimary()
    {
        PixelRect? taskbarBounds = null;
        var edge = TaskbarEdge.Bottom;
        var isAutoHide = false;

        try
        {
            var posData = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
            };

            if (NativeMethods.SHAppBarMessage(AbmGetTaskbarPos, ref posData) != IntPtr.Zero)
            {
                var candidate = ToPixelRect(posData.rc);
                if (!candidate.IsEmpty)
                {
                    taskbarBounds = candidate;
                }

                edge = posData.uEdge switch
                {
                    AbeLeft => TaskbarEdge.Left,
                    AbeTop => TaskbarEdge.Top,
                    AbeRight => TaskbarEdge.Right,
                    AbeBottom => TaskbarEdge.Bottom,
                    _ => TaskbarEdge.Bottom
                };
            }

            var stateData = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
            };

            var stateResult = NativeMethods.SHAppBarMessage(AbmGetState, ref stateData);
            if (stateResult != IntPtr.Zero)
            {
                isAutoHide = (stateResult.ToInt64() & AbsAutoHide) != 0;
            }
        }
        catch
        {
            // Headless, service, locked, or RDP-disconnected sessions may not have an active shell.
        }

        var monitor = CaptureMonitorForTaskbar(taskbarBounds);
        return new TaskbarSnapshot(monitor, taskbarBounds, edge, isAutoHide);
    }

    private static MonitorWorkArea CaptureMonitorForTaskbar(PixelRect? taskbarBounds)
    {
        var monitorBounds = PixelRect.Empty;
        var workArea = PixelRect.Empty;
        var dpiScale = 1.0;

        try
        {
            var hMonitor = IntPtr.Zero;
            if (taskbarBounds is { IsEmpty: false } tb)
            {
                var rect = new RECT
                {
                    Left = tb.X,
                    Top = tb.Y,
                    Right = tb.Right,
                    Bottom = tb.Bottom
                };
                hMonitor = NativeMethods.MonitorFromRect(ref rect, MonitorDefaultToNearest);
            }

            if (hMonitor == IntPtr.Zero)
            {
                hMonitor = NativeMethods.MonitorFromPoint(new POINT { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            }

            if (hMonitor != IntPtr.Zero)
            {
                var info = new MONITORINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
                };

                if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                {
                    monitorBounds = ToPixelRect(info.rcMonitor);
                    workArea = ToPixelRect(info.rcWork);
                }

                dpiScale = TryGetMonitorDpiScale(hMonitor);
            }
        }
        catch
        {
            // Fall through to GetSystemMetrics / synthetic screen bounds below.
        }

        if (monitorBounds.IsEmpty)
        {
            var width = SafeGetSystemMetric(SmCxScreen);
            var height = SafeGetSystemMetric(SmCyScreen);
            if (width <= 0 || height <= 0)
            {
                width = FallbackScreenWidth;
                height = FallbackScreenHeight;
            }

            monitorBounds = new PixelRect(0, 0, width, height);
        }

        if (workArea.IsEmpty)
        {
            workArea = monitorBounds;
        }

        if (!double.IsFinite(dpiScale) || dpiScale < 1.0)
        {
            dpiScale = 1.0;
        }

        return new MonitorWorkArea(monitorBounds, workArea, dpiScale);
    }

    private static double TryGetMonitorDpiScale(IntPtr hMonitor)
    {
        try
        {
            var hr = NativeMethods.GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var dpiX, out _);
            if (hr == 0 && dpiX > 0)
            {
                var scale = dpiX / StandardDpi;
                return double.IsFinite(scale) && scale >= 1.0 ? scale : 1.0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            // Fallback to 96 DPI (1.0) when shcore.dll or GetDpiForMonitor is unavailable.
        }

        return 1.0;
    }

    private static int SafeGetSystemMetric(int metricIndex)
    {
        try
        {
            return NativeMethods.GetSystemMetrics(metricIndex);
        }
        catch
        {
            return 0;
        }
    }

    private static PixelRect ToPixelRect(RECT rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0
            ? new PixelRect(rect.Left, rect.Top, width, height)
            : PixelRect.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", ExactSpelling = true)]
        internal static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("shcore.dll", ExactSpelling = true)]
        internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
