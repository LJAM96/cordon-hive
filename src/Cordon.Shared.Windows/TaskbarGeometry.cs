namespace Cordon.Shared.Windows;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => new(0, 0, 0, 0);

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelPoint point) =>
        !IsEmpty &&
        point.X >= X &&
        point.X < Right &&
        point.Y >= Y &&
        point.Y < Bottom;

    public bool Contains(PixelRect other) =>
        !IsEmpty &&
        !other.IsEmpty &&
        other.X >= X &&
        other.Y >= Y &&
        other.Right <= Right &&
        other.Bottom <= Bottom;

    public bool Intersects(PixelRect other) =>
        !IsEmpty &&
        !other.IsEmpty &&
        X < other.Right &&
        Right > other.X &&
        Y < other.Bottom &&
        Bottom > other.Y;

    public PixelRect Intersect(PixelRect other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return Empty;
        }

        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right > left && bottom > top
            ? new PixelRect(left, top, right - left, bottom - top)
            : Empty;
    }
}

public sealed record MonitorWorkArea(
    PixelRect MonitorBounds,
    PixelRect WorkArea,
    double DpiScale);

public sealed record DockPlacementResult(
    PixelRect OverlayBounds,
    bool PlacedInsideTaskbar,
    bool FallbackToTrayRecommended,
    string Reason);
