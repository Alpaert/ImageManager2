namespace ImageManager.App.Models;

/// <summary>
/// Controls how the current result snapshot is projected into UI items.
/// Continuous mode is intentionally unavailable until its windowed
/// virtualization implementation exists.
/// </summary>
public enum ImageDisplayMode
{
    Paged,
    Continuous
}

public enum ContinuousScrollDirection
{
    None,
    Up,
    Down
}

/// <summary>
/// A lightweight range into the active result snapshot. It never owns
/// ImageViewItem instances or decoded thumbnail data.
/// </summary>
public readonly record struct ImageDisplayRange(int StartIndex, int Count)
{
    public int EndExclusive => StartIndex + Count;
    public bool IsEmpty => Count <= 0;

    public static ImageDisplayRange ForPage(int pageIndex, int pageSize, int totalCount)
    {
        if (pageIndex < 0 || pageSize <= 0 || totalCount <= 0)
            return default;

        var start = checked(pageIndex * pageSize);
        if (start >= totalCount)
            return default;

        return new ImageDisplayRange(start, Math.Min(pageSize, totalCount - start));
    }
}
