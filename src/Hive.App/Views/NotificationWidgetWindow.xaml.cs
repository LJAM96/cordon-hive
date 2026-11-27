using Cordon.Shared.Windows;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Hive.App.Views;

public sealed record WidgetPreviewItem(string Title, string MetaText);

public sealed partial class NotificationWidgetWindow : Window, IWidgetHost
{
    private const double CollapsedWidthDip = 82.0;
    private const double CollapsedHeightDip = 34.0;
    private const double ExpandedWidthDip = 320.0;
    private const double ExpandedHeightDip = 264.0;
    private const double FlyoutGapDip = 4.0;

    private readonly DispatcherTimer _autoCollapseTimer;
    private TrayIconFallbackHost? _trayFallback;
    private TaskbarSnapshot? _lastSnapshot;
    private DockPlacementResult? _collapsedPlacement;
    private bool _usingTrayFallback;
    private bool _disposed;

    public NotificationBadgeState BadgeState { get; } = new();

    public bool IsUsingTrayFallback => _usingTrayFallback;

    public event EventHandler? OpenNotificationsRequested;

    public NotificationWidgetWindow()
    {
        InitializeComponent();

        _autoCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _autoCollapseTimer.Tick += AutoCollapseTimer_Tick;

        ConfigureAppWindow();
        ApplyVisualBadgeState();

        Closed += NotificationWidgetWindow_Closed;
    }

    public void UpdateBadge(int unresolvedCount, bool isConnectivityWarning)
    {
        if (_disposed)
        {
            return;
        }

        BadgeState.UnresolvedCount = unresolvedCount;
        BadgeState.IsConnectivityWarning = isConnectivityWarning;
        ApplyVisualBadgeState();

        if (_usingTrayFallback)
        {
            _trayFallback?.UpdateBadge(BadgeState.UnresolvedCount, BadgeState.ShowWarningIndicator);
        }
    }

    public void UpdateStatus(
        int unresolvedCount,
        bool isConnectivityWarning,
        bool isStale,
        IReadOnlyList<WidgetPreviewItem> previewItems)
    {
        if (_disposed)
        {
            return;
        }

        BadgeState.UnresolvedCount = unresolvedCount;
        BadgeState.IsConnectivityWarning = isConnectivityWarning;
        BadgeState.IsStale = isStale;

        PreviewItemsControl.ItemsSource = previewItems;
        FlyoutEmptyText.Visibility = previewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyVisualBadgeState();

        if (_usingTrayFallback)
        {
            _trayFallback?.UpdateBadge(BadgeState.UnresolvedCount, BadgeState.ShowWarningIndicator);
        }
    }

    public void ShowAtTaskbar()
    {
        if (_disposed)
        {
            return;
        }

        RefreshPlacement();
    }

    public void RefreshPlacement()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var snapshot = TaskbarLocator.CapturePrimary();
            _lastSnapshot = snapshot;

            var collapsedW = Math.Max(48, DockGeometryCalculator.DipsToPixels(CollapsedWidthDip, snapshot.Monitor.DpiScale));
            var collapsedH = Math.Max(28, DockGeometryCalculator.DipsToPixels(CollapsedHeightDip, snapshot.Monitor.DpiScale));

            var placement = DockGeometryCalculator.ComputeOverlayPlacement(
                snapshot.Monitor,
                snapshot.TaskbarBounds,
                snapshot.Edge,
                collapsedW,
                collapsedH,
                snapshot.IsAutoHide);

            _collapsedPlacement = placement;

            if (placement.FallbackToTrayRecommended || placement.OverlayBounds.IsEmpty)
            {
                ActivateTrayFallback();
                return;
            }

            DeactivateTrayFallback();
            PositionAppWindow(BadgeState.IsFlyoutOpen ? ComputeExpandedRect(snapshot, placement.OverlayBounds) : placement.OverlayBounds);
            AppWindow.Show(false);
        }
        catch
        {
            ActivateTrayFallback();
        }
    }

    public void ShowFlyout()
    {
        if (_disposed)
        {
            return;
        }

        if (_usingTrayFallback)
        {
            _trayFallback?.ShowFlyout();
            OpenNotificationsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        BadgeState.IsFlyoutOpen = true;
        FlyoutPanel.Visibility = Visibility.Visible;

        if (_lastSnapshot is { } snapshot && _collapsedPlacement is { } placement)
        {
            var expanded = ComputeExpandedRect(snapshot, placement.OverlayBounds);
            PositionAppWindow(expanded);
        }

        ResetAutoCollapseTimer();
    }

    public void HideFlyout()
    {
        if (_disposed)
        {
            return;
        }

        _autoCollapseTimer.Stop();
        BadgeState.IsFlyoutOpen = false;
        FlyoutPanel.Visibility = Visibility.Collapsed;

        if (_usingTrayFallback)
        {
            _trayFallback?.HideFlyout();
            return;
        }

        if (_collapsedPlacement is { } placement && !placement.OverlayBounds.IsEmpty)
        {
            PositionAppWindow(placement.OverlayBounds);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Tick -= AutoCollapseTimer_Tick;

        _trayFallback?.Dispose();
        _trayFallback = null;
        _usingTrayFallback = false;

        try
        {
            Close();
        }
        catch
        {
            // Ignore close errors if window was already torn down.
        }
    }

    private void ConfigureAppWindow()
    {
        try
        {
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Title = "Cordon Hive Notifications";

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }
        }
        catch
        {
            // Overlay presenter errors will fall back to TrayIconFallbackHost when RefreshPlacement runs.
        }
    }

    private void PositionAppWindow(PixelRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private PixelRect ComputeExpandedRect(TaskbarSnapshot snapshot, PixelRect collapsed)
    {
        var expandedW = Math.Max(collapsed.Width, DockGeometryCalculator.DipsToPixels(ExpandedWidthDip, snapshot.Monitor.DpiScale));
        var expandedH = Math.Max(collapsed.Height, DockGeometryCalculator.DipsToPixels(ExpandedHeightDip, snapshot.Monitor.DpiScale));
        var gap = Math.Max(2, DockGeometryCalculator.DipsToPixels(FlyoutGapDip, snapshot.Monitor.DpiScale));

        var monitor = snapshot.Monitor.MonitorBounds;
        var x = collapsed.Right - expandedW;
        var y = snapshot.Edge == TaskbarEdge.Top
            ? collapsed.Y
            : collapsed.Bottom - expandedH;

        if (snapshot.Edge == TaskbarEdge.Top)
        {
            Grid.SetRow(WidgetPillButton, 0);
            Grid.SetRow(FlyoutPanel, 1);
            FlyoutPanel.Margin = new Thickness(0, gap, 0, 0);
            WidgetPillButton.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            Grid.SetRow(FlyoutPanel, 0);
            Grid.SetRow(WidgetPillButton, 1);
            FlyoutPanel.Margin = new Thickness(0, 0, 0, gap);
            WidgetPillButton.VerticalAlignment = VerticalAlignment.Bottom;
        }

        var clampedW = Math.Min(expandedW, Math.Max(1, monitor.Width));
        var clampedH = Math.Min(expandedH, Math.Max(1, monitor.Height));
        var maxX = Math.Max(monitor.X, monitor.Right - clampedW);
        var maxY = Math.Max(monitor.Y, monitor.Bottom - clampedH);

        return new PixelRect(
            Math.Clamp(x, monitor.X, maxX),
            Math.Clamp(y, monitor.Y, maxY),
            clampedW,
            clampedH);
    }

    private void ActivateTrayFallback()
    {
        _usingTrayFallback = true;
        _autoCollapseTimer.Stop();
        BadgeState.IsFlyoutOpen = false;
        FlyoutPanel.Visibility = Visibility.Collapsed;

        try
        {
            AppWindow.Hide();
        }
        catch
        {
            // Ignore hide errors when window cannot be positioned.
        }

        _trayFallback ??= new TrayIconFallbackHost();
        _trayFallback.UpdateBadge(BadgeState.UnresolvedCount, BadgeState.ShowWarningIndicator);
    }

    private void DeactivateTrayFallback()
    {
        _usingTrayFallback = false;
        if (_trayFallback is not null)
        {
            _trayFallback.Dispose();
            _trayFallback = null;
        }
    }

    private void ApplyVisualBadgeState()
    {
        CountBadgeText.Text = BadgeState.BadgeText;
        CountBadgeBorder.Visibility = BadgeState.ShowBadge ? Visibility.Visible : Visibility.Collapsed;
        WarningIndicatorIcon.Visibility = BadgeState.ShowWarningIndicator ? Visibility.Visible : Visibility.Collapsed;
        FlyoutWarningBanner.Visibility = BadgeState.ShowWarningIndicator ? Visibility.Visible : Visibility.Collapsed;

        FlyoutSubtitleText.Text = BadgeState.UnresolvedCount switch
        {
            0 => "No pending actions",
            1 => "1 pending action",
            <= 99 => $"{BadgeState.UnresolvedCount} pending actions",
            _ => $"99+ ({BadgeState.UnresolvedCount}) pending actions"
        };

        AutomationProperties.SetName(WidgetPillButton, BadgeState.AccessibleLabel);
    }

    private void ResetAutoCollapseTimer()
    {
        _autoCollapseTimer.Stop();
        if (BadgeState.IsFlyoutOpen && !_disposed)
        {
            _autoCollapseTimer.Start();
        }
    }

    private void AutoCollapseTimer_Tick(object? sender, object e)
    {
        // Collapsing the flyout on idle NEVER acknowledges or completes any notification.
        HideFlyout();
    }

    private void WidgetPillButton_Click(object sender, RoutedEventArgs e)
    {
        if (BadgeState.IsFlyoutOpen)
        {
            HideFlyout();
        }
        else
        {
            ShowFlyout();
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
    }

    private void OpenNotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        OpenNotificationsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (BadgeState.IsFlyoutOpen)
        {
            ResetAutoCollapseTimer();
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (BadgeState.IsFlyoutOpen)
        {
            ResetAutoCollapseTimer();
        }
    }

    private void NotificationWidgetWindow_Closed(object sender, WindowEventArgs args)
    {
        _disposed = true;
        _autoCollapseTimer.Stop();
        _trayFallback?.Dispose();
        _trayFallback = null;
    }
}
