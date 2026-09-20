using ImageManager.App.Models;

namespace ImageManager.App.Services;

/// <summary>
/// Describes the result and retained portions of a display window.
/// The snapshot contains indexes only; it does not own UI items or image data.
/// </summary>
public readonly record struct ImageDisplayWindowSnapshot(
    ImageDisplayRange VisibleRange,
    ImageDisplayRange RetainedRange,
    int TotalCount)
{
    public bool IsIndexRetained(int index) =>
        index >= RetainedRange.StartIndex && index < RetainedRange.EndExclusive;
}

/// <summary>
/// Computes a bounded, symmetric item window around the visible range.
/// This is intentionally independent of Avalonia and can be used by either
/// paged or continuous display implementations.
/// </summary>
public sealed class ImageDisplayWindowManager
{
    private ImageDisplayWindowSnapshot _snapshot;

    public ImageDisplayWindowManager(int totalCount = 0)
    {
        TotalCount = ClampTotalCount(totalCount);
        _snapshot = EmptySnapshot(TotalCount);
    }

    public int TotalCount { get; private set; }

    public ImageDisplayWindowSnapshot Current => _snapshot;

    public void SetTotalCount(int totalCount)
    {
        TotalCount = ClampTotalCount(totalCount);
        _snapshot = EmptySnapshot(TotalCount);
    }

    public ImageDisplayWindowSnapshot UpdateVisibleRange(
        int visibleStart,
        int visibleCount,
        int bufferItemCount)
    {
        var visible = ClampRange(visibleStart, visibleCount, TotalCount);
        if (visible.IsEmpty)
        {
            _snapshot = EmptySnapshot(TotalCount);
            return _snapshot;
        }

        var buffer = Math.Max(0, bufferItemCount);
        var retainedStart = ClampToInt((long)visible.StartIndex - buffer, 0, TotalCount);
        var retainedEnd = ClampToInt((long)visible.EndExclusive + buffer, 0, TotalCount);
        var retained = new ImageDisplayRange(retainedStart, retainedEnd - retainedStart);
        _snapshot = new ImageDisplayWindowSnapshot(visible, retained, TotalCount);
        return _snapshot;
    }

    public bool IsIndexRetained(int index) => _snapshot.IsIndexRetained(index);

    private static ImageDisplayWindowSnapshot EmptySnapshot(int totalCount) =>
        new(default, default, totalCount);

    private static int ClampTotalCount(int totalCount) => Math.Max(0, totalCount);

    private static ImageDisplayRange ClampRange(int start, int count, int totalCount)
    {
        if (totalCount <= 0 || count <= 0 || start >= totalCount)
            return default;

        // Clamp the half-open source range, preserving its original endpoint.
        // Replacing a negative start with zero before adding count would
        // accidentally extend a range that begins before the result set.
        var clampedStart = Math.Max(0L, start);
        var end = Math.Min((long)totalCount, (long)start + count);
        if (end <= clampedStart)
            return default;

        return new ImageDisplayRange((int)clampedStart, (int)(end - clampedStart));
    }

    private static int ClampToInt(long value, int min, int max) =>
        (int)Math.Clamp(value, min, max);
}
