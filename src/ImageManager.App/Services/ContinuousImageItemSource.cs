using System.Collections;
using ImageManager.App.Models;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Services;

/// <summary>
/// A fixed result snapshot that lazily materializes UI items for continuous display.
/// This type is intended to be accessed from the UI thread; it does not provide
/// synchronization for concurrent cache updates. It is intentionally index-only:
/// enumerating or copying every entry would defeat virtualization.
/// </summary>
public sealed class ContinuousImageItemSource : IList
{
    private readonly IReadOnlyList<string> _paths;
    private readonly IReadOnlyList<ImageDisplayItemSize> _sizes;
    private readonly Func<string, List<string>> _getTags;
    private readonly Dictionary<int, ImageViewItem> _cachedItems = new();

    public ContinuousImageItemSource(
        IReadOnlyList<string> paths,
        IReadOnlyList<ImageDisplayItemSize> sizes,
        Func<string, List<string>> getTags)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(getTags);

        if (paths.Count != sizes.Count)
        {
            throw new ArgumentException(
                "The path and size snapshots must contain the same number of items.",
                nameof(sizes));
        }

        _paths = paths;
        _sizes = sizes;
        _getTags = getTags;
    }

    public int Count => _paths.Count;

    public int CachedItemCount => _cachedItems.Count;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get => GetOrCreateItem(index);
        set => throw new NotSupportedException("The continuous image item source is read-only.");
    }

    public bool TryGetCachedItem(int index, out ImageViewItem? item) =>
        _cachedItems.TryGetValue(index, out item);

    /// <summary>
    /// Returns only view items already materialized by the virtualizing panel
    /// within a half-open source range. This never creates items as a side effect.
    /// </summary>
    public IReadOnlyList<ImageViewItem> GetCachedItems(ImageDisplayRange range)
    {
        var start = Math.Clamp(range.StartIndex, 0, Count);
        var requestedEnd = (long)range.StartIndex + Math.Max(0, range.Count);
        var end = (int)Math.Clamp(requestedEnd, start, Count);
        if (end <= start)
            return Array.Empty<ImageViewItem>();

        var cached = new List<ImageViewItem>(Math.Min(end - start, _cachedItems.Count));
        for (var index = start; index < end; index++)
        {
            if (_cachedItems.TryGetValue(index, out var item))
                cached.Add(item);
        }

        return cached;
    }

    /// <summary>
    /// Materializes only a bounded range for background thumbnail warmup. This
    /// never creates item containers; the virtualizing panel remains solely
    /// responsible for that work.
    /// </summary>
    public IReadOnlyList<ImageViewItem> GetOrCreateItems(ImageDisplayRange range)
    {
        var start = Math.Clamp(range.StartIndex, 0, Count);
        var requestedEnd = (long)range.StartIndex + Math.Max(0, range.Count);
        var end = (int)Math.Clamp(requestedEnd, start, Count);
        if (end <= start)
            return Array.Empty<ImageViewItem>();

        var items = new List<ImageViewItem>(end - start);
        for (var index = start; index < end; index++)
            items.Add(GetOrCreateItem(index));

        return items;
    }

    public IReadOnlyList<ImageViewItem> GetAllCachedItems() => _cachedItems.Values.ToArray();

    /// <summary>
    /// Keeps only items in the supplied half-open source range. Discarded items
    /// explicitly release thumbnail bytes before their view models are unreferenced.
    /// </summary>
    public void UpdateRetainedRange(ImageDisplayRange retainedRange)
    {
        var start = Math.Clamp(retainedRange.StartIndex, 0, Count);
        var requestedEnd = (long)retainedRange.StartIndex + Math.Max(0, retainedRange.Count);
        var end = (int)Math.Clamp(requestedEnd, start, Count);

        foreach (var pair in _cachedItems.Where(pair => pair.Key < start || pair.Key >= end).ToArray())
        {
            pair.Value.ThumbnailData = null;
            _cachedItems.Remove(pair.Key);
        }
    }

    /// <summary>Releases every lazily created view item without mutating the snapshot.</summary>
    public void ClearCache()
    {
        foreach (var item in _cachedItems.Values)
            item.ThumbnailData = null;

        _cachedItems.Clear();
    }

    public int Add(object? value) => throw new NotSupportedException("The continuous image item source is read-only.");

    void IList.Clear() => throw new NotSupportedException("The continuous image item source is read-only.");

    public bool Contains(object? value) => value is ImageViewItem item && _cachedItems.Values.Contains(item);

    public int IndexOf(object? value)
    {
        if (value is not ImageViewItem item)
            return -1;

        foreach (var pair in _cachedItems)
        {
            if (ReferenceEquals(pair.Value, item))
                return pair.Key;
        }

        return -1;
    }

    public void Insert(int index, object? value) => throw new NotSupportedException("The continuous image item source is read-only.");

    public void Remove(object? value) => throw new NotSupportedException("The continuous image item source is read-only.");

    public void RemoveAt(int index) => throw new NotSupportedException("The continuous image item source is read-only.");

    public void CopyTo(Array array, int index)
    {
        throw new NotSupportedException(
            "Continuous display items must be accessed by index through the virtualizing panel.");
    }

    public IEnumerator GetEnumerator() => throw new NotSupportedException(
        "Continuous display items must be accessed by index through the virtualizing panel.");

    private ImageViewItem GetOrCreateItem(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_cachedItems.TryGetValue(index, out var item))
            return item;

        var path = _paths[index];
        var size = _sizes[index];
        item = new ImageViewItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Tags = _getTags(path) ?? new List<string>(),
            Width = ToImageDimension(size.Width),
            Height = ToImageDimension(size.Height),
            IsLoading = true,
            IsLoaded = false
        };

        _cachedItems.Add(index, item);
        return item;
    }

    private static int ToImageDimension(double value) =>
        double.IsFinite(value) && value > 0 && value <= int.MaxValue
            ? Math.Max(1, (int)Math.Round(value))
            : 1;
}
