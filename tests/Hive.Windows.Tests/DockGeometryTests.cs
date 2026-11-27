using Cordon.Shared.Windows;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class DockGeometryTests
{
    [Theory]
    [InlineData(TaskbarEdge.Bottom, 0, 1032, 1920, 48, 0, 0, 1920, 1032)]
    [InlineData(TaskbarEdge.Top, 0, 0, 1920, 48, 0, 48, 1920, 1032)]
    [InlineData(TaskbarEdge.Left, 0, 0, 72, 1080, 72, 0, 1848, 1080)]
    [InlineData(TaskbarEdge.Right, 1848, 0, 72, 1080, 0, 0, 1848, 1080)]
    public void ComputeOverlayPlacement_PlacesInsideTaskbar_ForAllFourEdges(
        TaskbarEdge edge,
        int tbX, int tbY, int tbW, int tbH,
        int waX, int waY, int waW, int waH)
    {
        var monitor = new MonitorWorkArea(
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(waX, waY, waW, waH),
            DpiScale: 1.0);
        var taskbar = new PixelRect(tbX, tbY, tbW, tbH);

        var result = DockGeometryCalculator.ComputeOverlayPlacement(
            monitor,
            taskbar,
            edge,
            overlayWidthPx: 40,
            overlayHeightPx: 40,
            isAutoHide: false);

        Assert.True(result.PlacedInsideTaskbar);
        Assert.False(result.FallbackToTrayRecommended);
        Assert.True(taskbar.Contains(result.OverlayBounds));
        Assert.True(monitor.MonitorBounds.Contains(result.OverlayBounds));
    }

    [Fact]
    public void ComputeOverlayPlacement_AvoidsReservedClockAndTrayRegions()
    {
        var monitor = new MonitorWorkArea(
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1032),
            DpiScale: 1.0);
        var taskbar = new PixelRect(0, 1032, 1920, 48);
        var clockAndTray = new PixelRect(1720, 1032, 200, 48);

        var result = DockGeometryCalculator.ComputeOverlayPlacement(
            monitor,
            taskbar,
            TaskbarEdge.Bottom,
            overlayWidthPx: 44,
            overlayHeightPx: 36,
            isAutoHide: false,
            reservedRegions: [clockAndTray]);

        Assert.True(result.PlacedInsideTaskbar);
        Assert.False(result.FallbackToTrayRecommended);
        Assert.False(result.OverlayBounds.Intersects(clockAndTray));
        Assert.True(result.OverlayBounds.Right <= clockAndTray.X);
    }

    [Fact]
    public void ComputeOverlayPlacement_AutoHide_PlacesAdjacentToWorkAreaOrRecommendsTrayIfBlocked()
    {
        var monitor = new MonitorWorkArea(
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1080),
            DpiScale: 1.0);
        var autoHideTaskbar = new PixelRect(0, 1078, 1920, 2);

        var result = DockGeometryCalculator.ComputeOverlayPlacement(
            monitor,
            autoHideTaskbar,
            TaskbarEdge.Bottom,
            overlayWidthPx: 44,
            overlayHeightPx: 36,
            isAutoHide: true);

        Assert.False(result.PlacedInsideTaskbar);
        Assert.False(result.FallbackToTrayRecommended);
        Assert.True(monitor.MonitorBounds.Contains(result.OverlayBounds));

        // When the entire bottom band is covered by a reserved region, recommend tray fallback
        var fullWidthReserved = new PixelRect(0, 1000, 1920, 80);
        var blocked = DockGeometryCalculator.ComputeOverlayPlacement(
            monitor,
            autoHideTaskbar,
            TaskbarEdge.Bottom,
            overlayWidthPx: 44,
            overlayHeightPx: 36,
            isAutoHide: true,
            reservedRegions: [fullWidthReserved]);

        Assert.False(blocked.PlacedInsideTaskbar);
        Assert.True(blocked.FallbackToTrayRecommended);
    }

    [Fact]
    public void ComputeOverlayPlacement_NegativeMonitorCoordinates_ClampsWithinMonitorBounds()
    {
        // Secondary monitor positioned to the left and above primary monitor
        var secondaryBounds = new PixelRect(-1920, -1080, 1920, 1080);
        var secondaryWorkArea = new PixelRect(-1920, -1080, 1920, 1032);
        var monitor = new MonitorWorkArea(secondaryBounds, secondaryWorkArea, DpiScale: 1.25);

        // Taskbar extending slightly past monitor edge to verify clamping
        var taskbar = new PixelRect(-1920, -48, 1950, 48);

        var result = DockGeometryCalculator.ComputeOverlayPlacement(
            monitor,
            taskbar,
            TaskbarEdge.Bottom,
            overlayWidthPx: 50,
            overlayHeightPx: 40,
            isAutoHide: false);

        Assert.True(result.PlacedInsideTaskbar);
        Assert.True(secondaryBounds.Contains(result.OverlayBounds));
        Assert.True(result.OverlayBounds.X >= -1920);
        Assert.True(result.OverlayBounds.Right <= 0);
        Assert.True(result.OverlayBounds.Bottom <= 0);
    }

    [Theory]
    [InlineData(1.0, 40, 40)]
    [InlineData(1.25, 40, 50)]
    [InlineData(1.5, 40, 60)]
    [InlineData(2.0, 40, 80)]
    public void DpiConversions_SupportStandardWindowsScales(double dpiScale, double dips, int expectedPixels)
    {
        var px = DockGeometryCalculator.DipsToPixels(dips, dpiScale);
        Assert.Equal(expectedPixels, px);

        var roundTripDips = DockGeometryCalculator.PixelsToDips(px, dpiScale);
        Assert.Equal(dips, roundTripDips, precision: 6);
    }

    [Theory]
    [InlineData(-5, "")]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(999, "99+")]
    public void BadgeDisplayFormatter_FormatsCountCorrectly(int count, string expected)
    {
        Assert.Equal(expected, BadgeDisplayFormatter.FormatCount(count));
    }

    [Fact]
    public void BadgeDisplayFormatter_FormatsAccessibleLabelWithConnectivityState()
    {
        Assert.Contains("no pending actions", BadgeDisplayFormatter.FormatAccessibleLabel(0, isStaleOrOffline: false));
        Assert.Contains("1 pending action", BadgeDisplayFormatter.FormatAccessibleLabel(1, isStaleOrOffline: false));
        Assert.Contains("99+ (125)", BadgeDisplayFormatter.FormatAccessibleLabel(125, isStaleOrOffline: true));
        Assert.Contains("stale or offline", BadgeDisplayFormatter.FormatAccessibleLabel(125, isStaleOrOffline: true));
    }
}
