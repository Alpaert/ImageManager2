using Avalonia;
using ImageManager.Core.Models;

namespace ImageManager.App.Controls;

[Flags]
internal enum RegionResizeEdge
{
    None = 0,
    West = 1,
    East = 2,
    North = 4,
    South = 8
}

internal static class RegionSelectionGeometry
{
    internal static readonly RegionResizeEdge[] Handles =
    [
        RegionResizeEdge.North | RegionResizeEdge.West,
        RegionResizeEdge.North,
        RegionResizeEdge.North | RegionResizeEdge.East,
        RegionResizeEdge.East,
        RegionResizeEdge.South | RegionResizeEdge.East,
        RegionResizeEdge.South,
        RegionResizeEdge.South | RegionResizeEdge.West,
        RegionResizeEdge.West
    ];

    internal static Rect ImageBounds(Size viewport, PixelSize source, double zoom, Vector pan)
    {
        if (source.Width <= 0 || source.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return default;

        var margin = Math.Min(22, Math.Min(viewport.Width, viewport.Height) / 12);
        var fit = Math.Min((viewport.Width - 2 * margin) / source.Width,
            (viewport.Height - 2 * margin) / source.Height);
        var width = source.Width * fit * zoom;
        var height = source.Height * fit * zoom;
        return new Rect((viewport.Width - width) / 2 + pan.X, (viewport.Height - height) / 2 + pan.Y,
            width, height);
    }

    internal static Vector ClampPan(Size viewport, Rect bounds, Vector pan)
    {
        var maxX = Math.Max(0, (viewport.Width + bounds.Width) / 2 - Math.Min(bounds.Width, 32));
        var maxY = Math.Max(0, (viewport.Height + bounds.Height) / 2 - Math.Min(bounds.Height, 32));
        return new Vector(Math.Clamp(pan.X, -maxX, maxX), Math.Clamp(pan.Y, -maxY, maxY));
    }

    internal static Point ToImage(Point point, Rect bounds) =>
        new((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height);

    internal static Rect ToViewport(Rect region, Rect bounds) =>
        new(bounds.X + region.X * bounds.Width, bounds.Y + region.Y * bounds.Height,
            region.Width * bounds.Width, region.Height * bounds.Height);

    internal static Rect FromPixels(ImageRegion region, PixelSize source) =>
        new((double)region.X / source.Width, (double)region.Y / source.Height,
            (double)region.Width / source.Width, (double)region.Height / source.Height);

    internal static ImageRegion ToPixels(Rect region, PixelSize source)
    {
        var left = Math.Clamp((int)Math.Floor(region.Left * source.Width + 1e-7), 0, source.Width - 1);
        var top = Math.Clamp((int)Math.Floor(region.Top * source.Height + 1e-7), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.Right * source.Width - 1e-7), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom * source.Height - 1e-7), top + 1, source.Height);
        return new ImageRegion(left, top, right - left, bottom - top);
    }

    internal static Rect Draw(Point start, Point current)
    {
        var x = Math.Clamp(current.X, 0, 1);
        var y = Math.Clamp(current.Y, 0, 1);
        return new Rect(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Abs(start.X - x), Math.Abs(start.Y - y));
    }

    internal static Rect Move(Rect region, Vector delta) =>
        new(Math.Clamp(region.X + delta.X, 0, 1 - region.Width),
            Math.Clamp(region.Y + delta.Y, 0, 1 - region.Height), region.Width, region.Height);

    internal static Rect Resize(Rect region, Vector delta, RegionResizeEdge edge, Size displaySize)
    {
        var minWidth = Math.Min(region.Width, 12 / displaySize.Width);
        var minHeight = Math.Min(region.Height, 12 / displaySize.Height);
        var left = region.Left;
        var top = region.Top;
        var right = region.Right;
        var bottom = region.Bottom;
        if (edge.HasFlag(RegionResizeEdge.West))
            left = Math.Clamp(left + delta.X, 0, Math.Max(0, right - minWidth));
        if (edge.HasFlag(RegionResizeEdge.East))
            right = Math.Clamp(right + delta.X, Math.Min(1, left + minWidth), 1);
        if (edge.HasFlag(RegionResizeEdge.North))
            top = Math.Clamp(top + delta.Y, 0, Math.Max(0, bottom - minHeight));
        if (edge.HasFlag(RegionResizeEdge.South))
            bottom = Math.Clamp(bottom + delta.Y, Math.Min(1, top + minHeight), 1);

        return new Rect(left, top, right - left, bottom - top);
    }

    internal static Point HandlePoint(Rect region, RegionResizeEdge edge)
    {
        var x = edge.HasFlag(RegionResizeEdge.West) ? region.Left :
            edge.HasFlag(RegionResizeEdge.East) ? region.Right : region.Center.X;
        var y = edge.HasFlag(RegionResizeEdge.North) ? region.Top :
            edge.HasFlag(RegionResizeEdge.South) ? region.Bottom : region.Center.Y;
        return new Point(x, y);
    }

    internal static RegionResizeEdge FindHandle(Rect region, Point point)
    {
        const double hitRadiusSquared = 49;
        var nearest = RegionResizeEdge.None;
        var distance = hitRadiusSquared;
        foreach (var edge in Handles)
        {
            var delta = point - HandlePoint(region, edge);
            var squared = delta.X * delta.X + delta.Y * delta.Y;
            if (squared < distance)
            {
                nearest = edge;
                distance = squared;
            }
        }

        return nearest;
    }
}
