using Cordon.Shared.Windows;
using Xunit;

namespace Hive.Windows.Tests;

public sealed class NotificationWidgetTests
{
    [Fact]
    public void TaskbarLocator_CapturePrimary_ReturnsValidMonitorBounds()
    {
        var snapshot = TaskbarLocator.CapturePrimary();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Monitor);
        Assert.True(snapshot.Monitor.MonitorBounds.Width > 0);
        Assert.True(snapshot.Monitor.MonitorBounds.Height > 0);
        Assert.True(snapshot.Monitor.WorkArea.Width > 0);
        Assert.True(snapshot.Monitor.WorkArea.Height > 0);
        Assert.True(snapshot.Monitor.DpiScale >= 1.0);
        if (snapshot.TaskbarBounds is { } taskbarBounds)
        {
            Assert.False(taskbarBounds.IsEmpty);
        }
    }

    [Fact]
    public void TaskbarLocator_Plus_DockGeometryCalculator_ClampsWithinMonitor()
    {
        var snapshot = TaskbarLocator.CapturePrimary();
        var overlayWidthPx = DockGeometryCalculator.DipsToPixels(44, snapshot.Monitor.DpiScale);
        var overlayHeightPx = DockGeometryCalculator.DipsToPixels(36, snapshot.Monitor.DpiScale);

        var placement = DockGeometryCalculator.ComputeOverlayPlacement(
            snapshot.Monitor,
            snapshot.TaskbarBounds,
            snapshot.Edge,
            overlayWidthPx,
            overlayHeightPx,
            snapshot.IsAutoHide);

        Assert.True(
            snapshot.Monitor.MonitorBounds.Contains(placement.OverlayBounds) ||
            placement.FallbackToTrayRecommended);
    }

    [Fact]
    public void NotificationBadgeState_PreservesUnresolvedCount_OnConnectivityWarningAndStale()
    {
        var state = new NotificationBadgeState
        {
            UnresolvedCount = 7,
            IsConnectivityWarning = true,
            IsStale = true
        };

        Assert.Equal(7, state.UnresolvedCount);
        Assert.Equal("7", state.BadgeText);
        Assert.True(state.ShowBadge);
        Assert.True(state.ShowWarningIndicator);
        Assert.Contains("7 pending actions", state.AccessibleLabel);
        Assert.Contains("stale or offline", state.AccessibleLabel);

        state.IsConnectivityWarning = false;
        state.IsStale = false;
        Assert.Equal(7, state.UnresolvedCount);
        Assert.False(state.ShowWarningIndicator);

        state.UnresolvedCount = 100;
        Assert.Equal("99+", state.BadgeText);
        Assert.Contains("99+ (100)", state.AccessibleLabel);

        state.UnresolvedCount = -5;
        Assert.Equal(0, state.UnresolvedCount);
        Assert.Equal(string.Empty, state.BadgeText);
        Assert.False(state.ShowBadge);
    }

    [Fact]
    public void TrayIconFallbackHost_UpdateAndDispose_DoesNotThrow()
    {
        using var host = new TrayIconFallbackHost();

        host.UpdateBadge(0, isConnectivityWarning: false);
        host.UpdateBadge(5, isConnectivityWarning: false);
        host.UpdateBadge(120, isConnectivityWarning: true);
        host.ShowFlyout();
        host.HideFlyout();
        host.Dispose();
        host.Dispose();
    }
}
