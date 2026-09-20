using ImageManager.App.Models;

namespace ImageManager.App.Services;

/// <summary>
/// Layout modes understood by the logical, UI-independent display geometry index.
/// </summary>
public enum ImageDisplayLayoutMode
{
    None,
    Vertical,
    Horizontal
}

/// <summary>
/// Lightweight source dimensions. These values deliberately do not own an image,
/// a thumbnail, or an Avalonia control.
/// </summary>
public readonly record struct ImageDisplayItemSize(double Width, double Height);

/// <summary>
/// A UI-independent item rectangle in logical display coordinates.
/// </summary>
public readonly record struct ImageDisplayItemRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

/// <summary>
/// Inputs for computing a complete logical result extent. The constants mirror the
/// current thumbnail template approximately: a 5px border margin on each side and
/// a 42px allowance for filename, tags, and vertical chrome. They are intentionally
/// stable estimates so a future virtualizing panel can keep its scroll extent fixed.
/// </summary>
public readonly record struct ImageDisplayGeometryOptions(
    ImageDisplayLayoutMode Mode,
    double ContainerWidth,
    double ThumbnailBaseWidth,
    double GridAspectRatio)
{
    public const double ItemGap = 10;
    public const double TextAndChromeHeight = 42;
    public const double DefaultContainerWidth = 1000;
    public const double DefaultThumbnailBaseWidth = 160;
    public const double DefaultAspectRatio = 1;

    public static ImageDisplayGeometryOptions Default => new(
        ImageDisplayLayoutMode.None,
        DefaultContainerWidth,
        DefaultThumbnailBaseWidth,
        DefaultAspectRatio);
}

/// <summary>
/// Immutable geometry for the complete logical result set. It exists so continuous
/// display can virtualize controls while still reporting the full scroll extent.
/// Item rectangles use struct storage only. Viewport lookup is O(log N) via a
/// top-sorted lookup table, prefix maximum bottoms, and an index range tree. It
/// returns a continuous candidate range, which may conservatively include a few
/// non-intersecting masonry items.
/// </summary>
public sealed class ImageDisplayGeometryIndex
{
    private const double FallbackImageAspectRatio = 4d / 3d;
    private const double MinAspectRatio = 0.05;
    private const double MaxAspectRatio = 20;
    private const double MaxContainerWidth = 10_000_000;
    private const double MaxBaseWidth = 100_000;

    private readonly ImageDisplayItemRect[] _rectangles;
    private readonly double[] _itemTops;
    private readonly double[] _prefixMaxBottoms;
    private readonly int[] _lookupIndexes;
    private readonly int[] _rangeMinimumIndexes;
    private readonly int[] _rangeMaximumIndexes;
    private readonly int _rangeTreeLeafCount;

    private ImageDisplayGeometryIndex(
        ImageDisplayGeometryOptions options,
        ImageDisplayItemRect[] rectangles,
        double extentWidth,
        double extentHeight)
    {
        Options = options;
        _rectangles = rectangles;
        ExtentWidth = extentWidth;
        ExtentHeight = extentHeight;
        _itemTops = new double[rectangles.Length];
        _prefixMaxBottoms = new double[rectangles.Length];
        _lookupIndexes = Enumerable.Range(0, rectangles.Length).ToArray();
        Array.Sort(_lookupIndexes, (left, right) => rectangles[left].Y.CompareTo(rectangles[right].Y));

        double maxBottom = 0;
        for (var position = 0; position < _lookupIndexes.Length; position++)
        {
            var index = _lookupIndexes[position];
            var rect = rectangles[index];
            _itemTops[position] = rect.Y;
            maxBottom = Math.Max(maxBottom, rect.Bottom);
            _prefixMaxBottoms[position] = maxBottom;
        }

        _rangeTreeLeafCount = 1;
        while (_rangeTreeLeafCount < rectangles.Length)
            _rangeTreeLeafCount <<= 1;
        _rangeMinimumIndexes = new int[_rangeTreeLeafCount * 2];
        _rangeMaximumIndexes = new int[_rangeTreeLeafCount * 2];
        Array.Fill(_rangeMinimumIndexes, int.MaxValue);
        Array.Fill(_rangeMaximumIndexes, int.MinValue);
        for (var position = 0; position < _lookupIndexes.Length; position++)
        {
            var leaf = _rangeTreeLeafCount + position;
            _rangeMinimumIndexes[leaf] = _lookupIndexes[position];
            _rangeMaximumIndexes[leaf] = _lookupIndexes[position];
        }
        for (var node = _rangeTreeLeafCount - 1; node > 0; node--)
        {
            _rangeMinimumIndexes[node] = Math.Min(_rangeMinimumIndexes[node * 2], _rangeMinimumIndexes[node * 2 + 1]);
            _rangeMaximumIndexes[node] = Math.Max(_rangeMaximumIndexes[node * 2], _rangeMaximumIndexes[node * 2 + 1]);
        }
    }

    public ImageDisplayGeometryOptions Options { get; }
    public int Count => _rectangles.Length;
    public double ExtentWidth { get; }
    public double ExtentHeight { get; }

    public static ImageDisplayGeometryIndex Build(
        IReadOnlyList<ImageDisplayItemSize> items,
        ImageDisplayGeometryOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);

        var normalized = Normalize(options);
        if (items.Count == 0)
            return new ImageDisplayGeometryIndex(normalized, Array.Empty<ImageDisplayItemRect>(), normalized.ContainerWidth, 0);

        ImageDisplayItemRect[] rectangles;
        double height;
        switch (normalized.Mode)
        {
            case ImageDisplayLayoutMode.Vertical:
                rectangles = BuildVertical(items, normalized, out height);
                break;
            case ImageDisplayLayoutMode.Horizontal:
                rectangles = BuildHorizontal(items, normalized, out height);
                break;
            default:
                rectangles = BuildGrid(items.Count, normalized, out height);
                break;
        }
        // A narrow viewport can be smaller than a stable grid cell. The logical
        // extent must still cover every item rectangle rather than reporting a
        // width that clips its right edge.
        var extentWidth = normalized.ContainerWidth;
        foreach (var rectangle in rectangles)
            extentWidth = Math.Max(extentWidth, rectangle.Right);
        return new ImageDisplayGeometryIndex(normalized, rectangles, extentWidth, height);
    }

    public bool TryGetItemRect(int index, out ImageDisplayItemRect rect)
    {
        if ((uint)index < (uint)_rectangles.Length)
        {
            rect = _rectangles[index];
            return true;
        }

        rect = default;
        return false;
    }

    /// <summary>
    /// Returns a bounded continuous candidate range for a Y viewport. This is an
    /// O(log N) lookup; Vertical mode can include intervening items in other columns.
    /// </summary>
    public ImageDisplayRange QueryViewport(double viewportY, double viewportHeight)
    {
        if (_rectangles.Length == 0 || !double.IsFinite(viewportY) || double.IsNaN(viewportHeight) || viewportHeight <= 0)
            return default;

        var viewportBottom = double.IsPositiveInfinity(viewportHeight)
            ? double.PositiveInfinity
            : viewportY + viewportHeight;
        if (double.IsNaN(viewportBottom) || viewportBottom <= viewportY)
            return default;

        // First prefix whose maximum bottom is strictly beyond the viewport top.
        var start = UpperBound(_prefixMaxBottoms, viewportY);
        // First item whose top is at or after the viewport bottom cannot intersect.
        var end = LowerBound(_itemTops, viewportBottom);
        if (end <= start)
            return default;

        var (minimumIndex, maximumIndex) = GetIndexBounds(start, end);
        return minimumIndex > maximumIndex
            ? default
            : new ImageDisplayRange(minimumIndex, maximumIndex - minimumIndex + 1);
    }

    private static ImageDisplayGeometryOptions Normalize(ImageDisplayGeometryOptions options)
    {
        var width = IsUsable(options.ContainerWidth)
            ? Math.Clamp(options.ContainerWidth, 1, MaxContainerWidth)
            : ImageDisplayGeometryOptions.DefaultContainerWidth;
        var baseWidth = IsUsable(options.ThumbnailBaseWidth)
            ? Math.Clamp(options.ThumbnailBaseWidth, 1, MaxBaseWidth)
            : ImageDisplayGeometryOptions.DefaultThumbnailBaseWidth;
        var gridAspect = IsUsable(options.GridAspectRatio)
            ? Math.Clamp(options.GridAspectRatio, MinAspectRatio, MaxAspectRatio)
            : ImageDisplayGeometryOptions.DefaultAspectRatio;
        return options with { ContainerWidth = width, ThumbnailBaseWidth = baseWidth, GridAspectRatio = gridAspect };
    }

    private static ImageDisplayItemRect[] BuildGrid(int count, ImageDisplayGeometryOptions options, out double extentHeight)
    {
        // SmartWaterfallPanel's grid uses the item width plus the template's
        // 5px margin on each side. The text is overlaid, so it does not add
        // another 42px to the cell height.
        var cellWidth = options.ThumbnailBaseWidth + ImageDisplayGeometryOptions.ItemGap;
        var columns = Math.Max(1, (int)Math.Floor(options.ContainerWidth / cellWidth));
        var cellHeight = options.ThumbnailBaseWidth / options.GridAspectRatio
            + ImageDisplayGeometryOptions.ItemGap;
        var result = new ImageDisplayItemRect[count];
        for (var index = 0; index < count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            result[index] = new ImageDisplayItemRect(column * cellWidth, row * cellHeight, cellWidth, cellHeight);
        }

        extentHeight = ((count + columns - 1) / columns) * cellHeight;
        return result;
    }

    private static ImageDisplayItemRect[] BuildVertical(
        IReadOnlyList<ImageDisplayItemSize> items,
        ImageDisplayGeometryOptions options,
        out double extentHeight)
    {
        // Keep the same column and item-height formulas as SmartWaterfallPanel.
        var columns = Math.Max(1, (int)Math.Floor(options.ContainerWidth / options.ThumbnailBaseWidth));
        var columnWidth = Math.Max(1, options.ContainerWidth / columns);
        var heights = new double[columns];
        var result = new ImageDisplayItemRect[items.Count];

        for (var index = 0; index < items.Count; index++)
        {
            var column = 0;
            for (var candidate = 1; candidate < columns; candidate++)
            {
                if (heights[candidate] < heights[column])
                    column = candidate;
            }

            var itemHeight = columnWidth / GetImageAspectRatio(items[index])
                + ImageDisplayGeometryOptions.TextAndChromeHeight;
            // SmartWaterfallPanel arranges masonry columns edge-to-edge. The
            // thumbnail template itself supplies the 5px margins on both sides.
            var x = column * columnWidth;
            result[index] = new ImageDisplayItemRect(x, heights[column], columnWidth, itemHeight);
            heights[column] += itemHeight;
        }

        extentHeight = Math.Max(0, heights.Max());
        return result;
    }

    private static ImageDisplayItemRect[] BuildHorizontal(
        IReadOnlyList<ImageDisplayItemSize> items,
        ImageDisplayGeometryOptions options,
        out double extentHeight)
    {
        var targetRowHeight = options.ThumbnailBaseWidth * 180d / 160d;
        var widths = new double[items.Count];
        for (var index = 0; index < items.Count; index++)
            widths[index] = targetRowHeight * GetImageAspectRatio(items[index]) + ImageDisplayGeometryOptions.ItemGap;

        var result = new ImageDisplayItemRect[items.Count];
        var rowStart = 0;
        var rowWidth = 0d;
        var y = 0d;
        var previousRowHeight = 0d;
        for (var index = 0; index < items.Count; index++)
        {
            if (rowWidth + widths[index] > options.ContainerWidth && index > rowStart)
            {
                var nextY = FlushHorizontalRow(result, widths, rowStart, index, rowWidth, y, targetRowHeight,
                    options.ContainerWidth);
                previousRowHeight = nextY - y;
                y = nextY;
                rowStart = index;
                rowWidth = 0;
            }
            rowWidth += widths[index];
        }

        // SmartWaterfallPanel uses the preceding row's actual height for the final
        // row, then recomputes each item width from that height. Keep that unusual
        // but visible behavior here so the two display modes agree exactly.
        extentHeight = Math.Max(0, FlushFinalHorizontalRow(result, items, rowStart, items.Count, y,
            previousRowHeight > 0 ? previousRowHeight : targetRowHeight, options.ContainerWidth));
        return result;
    }

    private static double FlushHorizontalRow(
        ImageDisplayItemRect[] result,
        double[] widths,
        int start,
        int endExclusive,
        double rowWidth,
        double y,
        double targetHeight,
        double containerWidth)
    {
        if (start >= endExclusive || rowWidth <= 0)
            return y;

        var height = targetHeight * containerWidth / rowWidth;
        var widthScale = height / targetHeight;
        var x = 0d;
        for (var index = start; index < endExclusive; index++)
        {
            var width = widths[index] * widthScale;
            result[index] = new ImageDisplayItemRect(x, y, width, height);
            x += width;
        }
        // The template's vertical margins provide the inter-row spacing, just as
        // they do in the paged SmartWaterfallPanel.
        return y + height;
    }

    private static double FlushFinalHorizontalRow(
        ImageDisplayItemRect[] result,
        IReadOnlyList<ImageDisplayItemSize> items,
        int start,
        int endExclusive,
        double y,
        double rowHeight,
        double containerWidth)
    {
        if (start >= endExclusive)
            return y;

        var totalWidth = 0d;
        for (var index = start; index < endExclusive; index++)
            totalWidth += rowHeight * GetImageAspectRatio(items[index]) + ImageDisplayGeometryOptions.ItemGap;
        if (totalWidth > containerWidth && totalWidth > 0)
            rowHeight *= containerWidth / totalWidth;

        var x = 0d;
        for (var index = start; index < endExclusive; index++)
        {
            var width = rowHeight * GetImageAspectRatio(items[index]) + ImageDisplayGeometryOptions.ItemGap;
            result[index] = new ImageDisplayItemRect(x, y, width, rowHeight);
            x += width;
        }
        return y + rowHeight;
    }

    private static double GetImageAspectRatio(ImageDisplayItemSize item)
    {
        if (!IsUsable(item.Width) || !IsUsable(item.Height))
            return FallbackImageAspectRatio;

        return Math.Clamp(item.Width / item.Height, MinAspectRatio, MaxAspectRatio);
    }

    private static bool IsUsable(double value) => double.IsFinite(value) && value > 0;

    private static int LowerBound(double[] values, double value)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(double[] values, double value)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private (int MinimumIndex, int MaximumIndex) GetIndexBounds(int start, int endExclusive)
    {
        var minimumIndex = int.MaxValue;
        var maximumIndex = int.MinValue;
        var left = start + _rangeTreeLeafCount;
        var right = endExclusive + _rangeTreeLeafCount;
        while (left < right)
        {
            if ((left & 1) != 0)
            {
                minimumIndex = Math.Min(minimumIndex, _rangeMinimumIndexes[left]);
                maximumIndex = Math.Max(maximumIndex, _rangeMaximumIndexes[left]);
                left++;
            }
            if ((right & 1) != 0)
            {
                right--;
                minimumIndex = Math.Min(minimumIndex, _rangeMinimumIndexes[right]);
                maximumIndex = Math.Max(maximumIndex, _rangeMaximumIndexes[right]);
            }
            left >>= 1;
            right >>= 1;
        }
        return (minimumIndex, maximumIndex);
    }
}
