namespace Cordon.Shared.Windows;

/// <summary>
/// Pure, framework-neutral taskbar overlay placement and DPI conversion helpers.
/// </summary>
public static class DockGeometryCalculator
{
    private const double BaseMarginDips = 4.0;

    public static int DipsToPixels(double dips, double dpiScale)
    {
        ValidateDpiScale(dpiScale);
        return (int)Math.Round(dips * dpiScale, MidpointRounding.AwayFromZero);
    }

    public static double PixelsToDips(int pixels, double dpiScale)
    {
        ValidateDpiScale(dpiScale);
        return pixels / dpiScale;
    }

    public static DockPlacementResult ComputeOverlayPlacement(
        MonitorWorkArea monitor,
        PixelRect? taskbarBounds,
        TaskbarEdge edge,
        int overlayWidthPx,
        int overlayHeightPx,
        bool isAutoHide,
        IReadOnlyList<PixelRect>? reservedRegions = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (overlayWidthPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlayWidthPx), "Overlay width must be positive.");
        }

        if (overlayHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlayHeightPx), "Overlay height must be positive.");
        }

        var dpi = monitor.DpiScale > 0 && double.IsFinite(monitor.DpiScale) ? monitor.DpiScale : 1.0;
        var margin = Math.Max(2, DipsToPixels(BaseMarginDips, dpi));
        var bounds = monitor.MonitorBounds;
        var workArea = monitor.WorkArea.Intersect(bounds);
        if (workArea.IsEmpty)
        {
            workArea = bounds;
        }

        if (bounds.IsEmpty || bounds.Width < overlayWidthPx || bounds.Height < overlayHeightPx)
        {
            var clamped = ClampToBounds(
                new PixelRect(bounds.X, bounds.Y, overlayWidthPx, overlayHeightPx),
                bounds);
            return new DockPlacementResult(
                clamped,
                PlacedInsideTaskbar: false,
                FallbackToTrayRecommended: true,
                "Monitor bounds are smaller than overlay dimensions.");
        }

        var reserved = reservedRegions ?? Array.Empty<PixelRect>();

        if (!isAutoHide && taskbarBounds is { IsEmpty: false } rawTaskbar)
        {
            var taskbar = rawTaskbar.Intersect(bounds);
            if (!taskbar.IsEmpty &&
                TryPlaceInsideTaskbar(taskbar, bounds, edge, overlayWidthPx, overlayHeightPx, margin, reserved, out var insideRect))
            {
                return new DockPlacementResult(
                    insideRect,
                    PlacedInsideTaskbar: true,
                    FallbackToTrayRecommended: false,
                    "Placed inside taskbar free space.");
            }
        }

        // For auto-hide taskbars, missing taskbar bounds, or obstructed taskbars:
        // place just inside the work area adjacent to the taskbar edge without overlapping reserved regions or the taskbar itself.
        var nonAutoHideTaskbar = !isAutoHide && taskbarBounds is { IsEmpty: false } tb
            ? tb.Intersect(bounds)
            : PixelRect.Empty;

        if (TryPlaceAdjacentToEdge(workArea, bounds, nonAutoHideTaskbar, edge, overlayWidthPx, overlayHeightPx, margin, reserved, out var adjacentRect))
        {
            var reason = isAutoHide
                ? "Auto-hide taskbar: placed adjacent to work area edge."
                : taskbarBounds is null or { IsEmpty: true }
                    ? "Taskbar bounds unavailable: placed adjacent to work area edge."
                    : "Taskbar space unavailable: placed adjacent to taskbar in work area.";

            var recommendTray = !isAutoHide && (taskbarBounds is null || taskbarBounds.Value.IsEmpty);
            return new DockPlacementResult(
                adjacentRect,
                PlacedInsideTaskbar: false,
                FallbackToTrayRecommended: recommendTray,
                reason);
        }

        var fallback = ClampToBounds(
            new PixelRect(
                workArea.Right - overlayWidthPx - margin,
                workArea.Bottom - overlayHeightPx - margin,
                overlayWidthPx,
                overlayHeightPx),
            bounds);

        return new DockPlacementResult(
            fallback,
            PlacedInsideTaskbar: false,
            FallbackToTrayRecommended: true,
            "No safe non-overlapping taskbar or work area space; tray fallback recommended.");
    }

    private static bool TryPlaceInsideTaskbar(
        PixelRect taskbar,
        PixelRect monitorBounds,
        TaskbarEdge edge,
        int width,
        int height,
        int margin,
        IReadOnlyList<PixelRect> reserved,
        out PixelRect placed)
    {
        if (width > taskbar.Width || height > taskbar.Height)
        {
            placed = PixelRect.Empty;
            return false;
        }

        if (edge is TaskbarEdge.Bottom or TaskbarEdge.Top)
        {
            var y = taskbar.Y + (taskbar.Height - height) / 2;
            foreach (var x in GetHorizontalCandidates(taskbar, width, margin, reserved))
            {
                var candidate = new PixelRect(x, y, width, height);
                if (taskbar.Contains(candidate) &&
                    monitorBounds.Contains(candidate) &&
                    !IntersectsAny(candidate, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }
        }
        else
        {
            var x = taskbar.X + (taskbar.Width - width) / 2;
            foreach (var y in GetVerticalCandidates(taskbar, height, margin, reserved))
            {
                var candidate = new PixelRect(x, y, width, height);
                if (taskbar.Contains(candidate) &&
                    monitorBounds.Contains(candidate) &&
                    !IntersectsAny(candidate, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }
        }

        placed = PixelRect.Empty;
        return false;
    }

    private static bool TryPlaceAdjacentToEdge(
        PixelRect workArea,
        PixelRect monitorBounds,
        PixelRect excludedTaskbar,
        TaskbarEdge edge,
        int width,
        int height,
        int margin,
        IReadOnlyList<PixelRect> reserved,
        out PixelRect placed)
    {
        if (edge is TaskbarEdge.Bottom or TaskbarEdge.Top)
        {
            var y = edge == TaskbarEdge.Bottom
                ? workArea.Bottom - height - margin
                : workArea.Y + margin;

            foreach (var x in GetHorizontalCandidates(workArea, width, margin, reserved))
            {
                var candidate = new PixelRect(x, y, width, height);
                if (IsValidAdjacentCandidate(candidate, monitorBounds, excludedTaskbar, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }

            // Retry without outer margin if work area is tight
            var tightY = edge == TaskbarEdge.Bottom ? workArea.Bottom - height : workArea.Y;
            foreach (var x in GetHorizontalCandidates(workArea, width, 0, reserved))
            {
                var candidate = new PixelRect(x, tightY, width, height);
                if (IsValidAdjacentCandidate(candidate, monitorBounds, excludedTaskbar, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }
        }
        else
        {
            var x = edge == TaskbarEdge.Left
                ? workArea.X + margin
                : workArea.Right - width - margin;

            foreach (var y in GetVerticalCandidates(workArea, height, margin, reserved))
            {
                var candidate = new PixelRect(x, y, width, height);
                if (IsValidAdjacentCandidate(candidate, monitorBounds, excludedTaskbar, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }

            var tightX = edge == TaskbarEdge.Left ? workArea.X : workArea.Right - width;
            foreach (var y in GetVerticalCandidates(workArea, height, 0, reserved))
            {
                var candidate = new PixelRect(tightX, y, width, height);
                if (IsValidAdjacentCandidate(candidate, monitorBounds, excludedTaskbar, reserved))
                {
                    placed = candidate;
                    return true;
                }
            }
        }

        placed = PixelRect.Empty;
        return false;
    }

    private static bool IsValidAdjacentCandidate(
        PixelRect candidate,
        PixelRect monitorBounds,
        PixelRect excludedTaskbar,
        IReadOnlyList<PixelRect> reserved)
    {
        return monitorBounds.Contains(candidate) &&
               (excludedTaskbar.IsEmpty || !candidate.Intersects(excludedTaskbar)) &&
               !IntersectsAny(candidate, reserved);
    }

    private static IEnumerable<int> GetHorizontalCandidates(
        PixelRect container,
        int width,
        int margin,
        IReadOnlyList<PixelRect> reserved)
    {
        var rightMost = container.Right - width - margin;
        var leftMost = container.X + margin;

        yield return rightMost;
        yield return container.Right - width;

        // Place just to the left or right of any reserved regions (ordered from right to left)
        foreach (var r in reserved.OrderByDescending(r => r.X))
        {
            yield return r.X - width - margin;
            yield return r.X - width;
            yield return r.Right + margin;
            yield return r.Right;
        }

        yield return leftMost;
        yield return container.X;

        // Fallback sweep in step increments in case multiple fragmented reserved regions exist
        var step = Math.Max(8, width / 2);
        for (var x = rightMost - step; x >= leftMost; x -= step)
        {
            yield return x;
        }
    }

    private static IEnumerable<int> GetVerticalCandidates(
        PixelRect container,
        int height,
        int margin,
        IReadOnlyList<PixelRect> reserved)
    {
        var bottomMost = container.Bottom - height - margin;
        var topMost = container.Y + margin;

        yield return bottomMost;
        yield return container.Bottom - height;

        foreach (var r in reserved.OrderByDescending(r => r.Y))
        {
            yield return r.Y - height - margin;
            yield return r.Y - height;
            yield return r.Bottom + margin;
            yield return r.Bottom;
        }

        yield return topMost;
        yield return container.Y;

        var step = Math.Max(8, height / 2);
        for (var y = bottomMost - step; y >= topMost; y -= step)
        {
            yield return y;
        }
    }

    private static bool IntersectsAny(PixelRect candidate, IReadOnlyList<PixelRect> reserved)
    {
        for (var i = 0; i < reserved.Count; i++)
        {
            if (candidate.Intersects(reserved[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static PixelRect ClampToBounds(PixelRect rect, PixelRect bounds)
    {
        var width = Math.Min(rect.Width, Math.Max(0, bounds.Width));
        var height = Math.Min(rect.Height, Math.Max(0, bounds.Height));
        var maxX = Math.Max(bounds.X, bounds.Right - width);
        var maxY = Math.Max(bounds.Y, bounds.Bottom - height);
        var x = Math.Clamp(rect.X, bounds.X, maxX);
        var y = Math.Clamp(rect.Y, bounds.Y, maxY);
        return new PixelRect(x, y, width, height);
    }

    private static void ValidateDpiScale(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale), "DPI scale must be a finite positive number.");
        }
    }
}
