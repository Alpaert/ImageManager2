using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App.Controls;

/// <summary>
/// Keeps scroll diagnostics off the UI thread. Diagnostics are best-effort: when the
/// bounded queue is full, dropping a record is preferable to delaying rendering.
/// </summary>
internal static class ScrollDiagnosticsLogger
{
    private const int QueueCapacity = 256;
    private static readonly System.Threading.Channels.Channel<string> Messages =
        System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(QueueCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

    static ScrollDiagnosticsLogger()
    {
        _ = Task.Run(DrainAsync);
    }

    public static void Log(string message) => Messages.Writer.TryWrite(message);

    private static async Task DrainAsync()
    {
        await foreach (var message in Messages.Reader.ReadAllAsync().ConfigureAwait(false))
            AppLogger.Info(message);
    }
}

public class SmartWaterfallPanel : Panel
{
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<SmartWaterfallPanel, string>(nameof(Mode), "None");

    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<SmartWaterfallPanel, double>(nameof(ColumnWidth), 160.0);

    public static readonly AttachedProperty<double> ItemWidthProperty =
        AvaloniaProperty.RegisterAttached<SmartWaterfallPanel, Control, double>("ItemWidth", double.NaN, true);

    public static void SetItemWidth(Control element, double value) => element.SetValue(ItemWidthProperty, value);
    public static double GetItemWidth(Control element) => element.GetValue(ItemWidthProperty);

    static SmartWaterfallPanel()
    {
        ModeProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) =>
        {
            x.InvalidateHorizontalGeometry();
            x.InvalidateMeasure();
        });
        ColumnWidthProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) =>
        {
            x.InvalidateHorizontalGeometry();
            x.InvalidateMeasure();
        });
    }

    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    // ==================== Viewport-aware layout fields ====================
    private ScrollViewer? _scrollViewer;
    private EventHandler<ScrollChangedEventArgs>? _scrollHandler;
    private DispatcherTimer? _scrollIdleTimer;
    private double _viewportHeight;
    private double _scrollOffsetY;
    private bool _scrollWired;
    private bool _scrollActive;
    private bool _lifecycleSyncPending;
    private bool _arrangePending;

    private readonly Dictionary<Control, double> _childWidths = new();
    private readonly List<RowInfo> _rows = new();
    private readonly List<Control> _horizontalGeometryChildren = new();
    private bool _horizontalGeometryValid;
    private double _horizontalGeometryWidth;
    private double _horizontalMeasuredHeight;
    private int _horizontalGeometryVersion;
    private int _appliedHorizontalGeometryVersion = -1;
    private double _lastHorizontalArrangeWidth;
    private bool _hasLastHorizontalArrangeSize;
    // Arrange diagnostics. They are reset for each layout pass and reported by ArrangeOverride.
    private bool _lastArrangeGeometryReused;
    private int _lastArrangeRows;
    private int _lastArrangeVisibilityRowsChanged;
    private bool _lastArrangeVisibilityDeferred;

    // Bitmap lifecycle: track which controls had their bitmaps released
    private readonly HashSet<Control> _bitmapFreed = new();
    private int _lastVisibleStartRow = -1;
    private int _lastVisibleEndRow = -1;
    private bool _visibleRangeChanged;
    private int _lastVerticalRowsCount;
    private long _lastVerticalRowsBuildMs;
    private PageManager? _cachedPageManager;
    private Control? _firstTrackedChild; // detect ItemsSource reset (page flip)

    private sealed class RowInfo
    {
        public double Y;
        public double Height;
        public List<Control> Children = new();
        public List<Rect> Bounds = new();
    }

    private void InvalidateHorizontalGeometry()
    {
        _horizontalGeometryValid = false;
        _horizontalGeometryChildren.Clear();
        _appliedHorizontalGeometryVersion = -1;
        _hasLastHorizontalArrangeSize = false;
    }

    private void EnsureScrollViewer()
    {
        if (_scrollWired) return;
        _scrollWired = true;
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollIdleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
                (_, _) => EndScrollActivity());
            _scrollOffsetY = _scrollViewer.Offset.Y;
            _viewportHeight = _scrollViewer.Viewport.Height;
            _scrollHandler = (_, _) =>
            {
                _scrollOffsetY = _scrollViewer.Offset.Y;
                _viewportHeight = _scrollViewer.Viewport.Height;
                BeginScrollActivity();
                // 节流：合并同一帧内的多次 scroll 事件，最多每帧重排一次
                if (!_arrangePending)
                {
                    _arrangePending = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _arrangePending = false;
                        InvalidateArrange();
                    }, Avalonia.Threading.DispatcherPriority.Render);
                }
            };
            _scrollViewer.ScrollChanged += _scrollHandler;
        }
    }

    private void BeginScrollActivity()
    {
        _scrollActive = true;
        _lifecycleSyncPending = true;
        _scrollIdleTimer?.Stop();
        _scrollIdleTimer?.Start();
    }

    private void EndScrollActivity()
    {
        _scrollIdleTimer?.Stop();
        if (!_scrollActive) return;
        _scrollActive = false;
        if (_lifecycleSyncPending)
        {
            _lifecycleSyncPending = false;
            // Force the deferred pass even if the user returned to the range that
            // was visible before scrolling began.
            _lastVisibleStartRow = -1;
            _lastVisibleEndRow = -1;
            InvalidateArrange();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollViewer != null && _scrollHandler != null)
        {
            _scrollViewer.ScrollChanged -= _scrollHandler;
            _scrollHandler = null;
        }
        _scrollIdleTimer?.Stop();
        _scrollIdleTimer = null;
        _scrollActive = false;
        _lifecycleSyncPending = false;
        _scrollWired = false;
        _scrollViewer = null;
    }

    private bool IsRowVisible(double y, double h)
    {
        if (_scrollViewer == null || _viewportHeight <= 0) return true;
        double buf = h * 2; // 2-row buffer zone
        return y + h >= _scrollOffsetY - buf && y <= _scrollOffsetY + _viewportHeight + buf;
    }

    /// <summary>
    /// Release bitmap data for controls in off-screen rows.
    /// When a row re-enters the viewport, trigger bitmap reload.
    /// </summary>
    private void ManageBitmapLifecycle(List<RowInfo> rows)
    {
        var lifecycleStopwatch = Stopwatch.StartNew();
        int previousVisibleStart = _lastVisibleStartRow;
        int previousVisibleEnd = _lastVisibleEndRow;
        // Find visible row range
        int visibleStart = -1, visibleEnd = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (IsRowVisible(rows[i].Y, rows[i].Height))
            {
                if (visibleStart < 0) visibleStart = i;
                visibleEnd = i;
            }
        }

        // Extend by 1 row on each side for smooth scrolling
        visibleStart = Math.Max(0, visibleStart - 1);
        visibleEnd = Math.Min(rows.Count - 1, visibleEnd + 1);

        if (_scrollActive)
        {
            // Keep off-screen bitmaps until the scroll settles, but restore items that
            // were freed during a previous idle period before they re-enter the viewport.
            RestoreFreedVisibleBitmaps(rows, visibleStart, visibleEnd);
            _lifecycleSyncPending = true;
            return;
        }

        // Only process if visible range changed
        if (visibleStart == _lastVisibleStartRow && visibleEnd == _lastVisibleEndRow)
            return;
        _lastVisibleStartRow = visibleStart;
        _lastVisibleEndRow = visibleEnd;
        _visibleRangeChanged = true;

        // Release bitmaps for rows outside visible range
        int released = 0;
        int reloadRequested = 0;
        int visibleItems = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            bool isVisible = i >= visibleStart && i <= visibleEnd;
            if (isVisible)
                visibleItems += rows[i].Children.Count;
            foreach (var child in rows[i].Children)
            {
                if (child.DataContext is ImageViewItem item)
                {
                    if (!isVisible && item.ThumbnailData != null)
                    {
                        item.ThumbnailData = null; // release byte[], GC reclaims Bitmap
                        item.IsLoaded = false;     // mark as unloaded so page-revisit path reloads it
                        _bitmapFreed.Add(child);
                        released++;
                    }
                    else if (isVisible && _bitmapFreed.Contains(child))
                    {
                        item.NotifyThumbnailNeeded();
                        _bitmapFreed.Remove(child);
                        // Trigger PageManager to reload this thumbnail (cached to avoid service locator in hot path)
                        _cachedPageManager ??= App.Services.GetRequiredService<PageManager>();
                        _cachedPageManager.LoadThumbnailsForItems(new List<ImageViewItem> { item });
                        reloadRequested++;
                    }
                }
            }
        }

        lifecycleStopwatch.Stop();
        ScrollDiagnosticsLogger.Log(
            $"ThumbViewport.Range mode={Mode} range={previousVisibleStart}-{previousVisibleEnd}" +
            $"=>{visibleStart}-{visibleEnd} rows={rows.Count} visibleItems={visibleItems} " +
            $"released={released} reloadRequested={reloadRequested} lifecycleMs={lifecycleStopwatch.ElapsedMilliseconds} " +
            $"offsetY={_scrollOffsetY:F0} viewportH={_viewportHeight:F0}");
    }

    private void RestoreFreedVisibleBitmaps(List<RowInfo> rows, int visibleStart, int visibleEnd)
    {
        for (int i = visibleStart; i <= visibleEnd; i++)
        {
            foreach (var child in rows[i].Children)
            {
                if (!_bitmapFreed.Remove(child) || child.DataContext is not ImageViewItem item)
                    continue;

                item.NotifyThumbnailNeeded();
                _cachedPageManager ??= App.Services.GetRequiredService<PageManager>();
                _cachedPageManager.LoadThumbnailsForItems(new List<ImageViewItem> { item });
            }
        }
    }

    // 查询 child 在 Panel 内部坐标系下的 Y 坐标与行高。视口外的 child 不会被 Arrange，
    // 其 Bounds 不可用；调用方据此直接驱动 ScrollViewer.Offset。
    public bool TryGetItemY(Control child, out double y, out double height)
    {
        foreach (var entry in _verticalLayout)
        {
            if (ReferenceEquals(entry.Child, child))
            {
                y = entry.Y;
                height = entry.H;
                return true;
            }
        }
        foreach (var row in _rows)
        {
            if (row.Children.Contains(child))
            {
                y = row.Y;
                height = row.Height;
                return true;
            }
        }
        y = 0;
        height = 0;
        return false;
    }

    // ==================== Measure / Arrange ====================

    private void ResetLifecycleState()
    {
        _bitmapFreed.Clear();
        _lastVisibleStartRow = -1;
        _lastVisibleEndRow = -1;
        _visibleRangeChanged = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Detect page flip: ItemsSource reset replaces all children
        if (Children.Count == 0)
        {
            ResetLifecycleState();
            _firstTrackedChild = null;
            return new Size(0, 0);
        }
        if (_firstTrackedChild != null && _firstTrackedChild != Children[0])
        {
            ResetLifecycleState();
        }
        if (Children.Count > 0)
            _firstTrackedChild = Children[0];

        var sw = Stopwatch.StartNew();
        var result = Mode switch
        {
            "Vertical" => MeasureVertical(availableSize),
            "Horizontal" => MeasureHorizontal(availableSize),
            _ => MeasureDefault(availableSize)
        };
        if (sw.ElapsedMilliseconds > 4)
            ScrollDiagnosticsLogger.Log(
                $"ScrollLayout.Measure mode={Mode} children={Children.Count} rows={GetLayoutRowCount()} " +
                $"available={availableSize.Width:F0}x{availableSize.Height:F0} elapsedMs={sw.ElapsedMilliseconds}");
        return result;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        var sw = Stopwatch.StartNew();
        _lastArrangeGeometryReused = false;
        _lastArrangeRows = 0;
        _lastArrangeVisibilityRowsChanged = 0;
        _lastArrangeVisibilityDeferred = false;
        EnsureScrollViewer();
        var result = Mode switch
        {
            "Vertical" => ArrangeVertical(finalSize),
            "Horizontal" => ArrangeHorizontal(finalSize),
            _ => ArrangeDefault(finalSize)
        };
        bool visibleRangeChanged = _visibleRangeChanged;
        _visibleRangeChanged = false;
        if (sw.ElapsedMilliseconds > 4 || visibleRangeChanged)
            ScrollDiagnosticsLogger.Log(
                $"ScrollLayout.Arrange mode={Mode} children={Children.Count} rows={GetLayoutRowCount()} " +
                $"visibleRows={_lastVisibleStartRow}-{_lastVisibleEndRow} " +
                $"verticalRowsBuildMs={_lastVerticalRowsBuildMs} final={finalSize.Width:F0}x{finalSize.Height:F0} " +
                $"geometryReused={_lastArrangeGeometryReused} arrangedRows={_lastArrangeRows} " +
                $"visibilityRowsChanged={_lastArrangeVisibilityRowsChanged} " +
                $"visibilityDeferred={_lastArrangeVisibilityDeferred} " +
                $"elapsedMs={sw.ElapsedMilliseconds}");
        return result;
    }

    private int GetLayoutRowCount() => Mode == "Vertical" ? _lastVerticalRowsCount : _rows.Count;

    #region Vertical (Masonry)

    private readonly List<(Control Child, double X, double Y, double W, double H)> _verticalLayout = new();

    private Size MeasureVertical(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        int colCount = Math.Max(1, (int)(width / ColumnWidth));
        double actualColWidth = width / colCount;
        SetValue(ItemWidthProperty, actualColWidth);
        double[] colHeights = new double[colCount];
        _verticalLayout.Clear();

        foreach (Control child in Children)
        {
            double childHeight;
            if (child is Control ctrl && ctrl.DataContext is ImageViewItem item
                && item.Width > 0 && item.Height > 0)
            {
                childHeight = actualColWidth / ((double)item.Width / item.Height) + 42;
            }
            else
            {
                childHeight = actualColWidth + 42;
            }

            int minCol = 0;
            for (int i = 1; i < colCount; i++)
                if (colHeights[i] < colHeights[minCol]) minCol = i;

            double x = minCol * actualColWidth;
            double y = colHeights[minCol];
            _verticalLayout.Add((child, x, y, actualColWidth, childHeight));
            colHeights[minCol] += childHeight;
        }
        return new Size(width, colHeights.Max());
    }

    private Size ArrangeVertical(Size finalSize)
    {
        // Build row list for bitmap lifecycle management
        var rowBuildStopwatch = Stopwatch.StartNew();
        var rows = new List<RowInfo>();
        foreach (var group in _verticalLayout.GroupBy(l => l.Y))
        {
            var row = new RowInfo { Y = group.Key, Height = group.Max(l => l.H), Children = group.Select(l => l.Child).ToList() };
            rows.Add(row);
        }
        _lastVerticalRowsCount = rows.Count;
        _lastVerticalRowsBuildMs = rowBuildStopwatch.ElapsedMilliseconds;
        ManageBitmapLifecycle(rows);

        foreach (var (child, x, y, w, h) in _verticalLayout)
        {
            if (!IsRowVisible(y, h))
            {
                child.IsVisible = false;
            }
            else
            {
                child.IsVisible = true;
                child.Arrange(new Rect(x, y, w, h));
            }
        }
        return finalSize;
    }

    #endregion

    #region Horizontal (Justified)

    private double TargetRowHeight => ColumnWidth * 180.0 / 160.0;

    private Size MeasureHorizontal(Size availableSize)
    {
        double rowHeight = TargetRowHeight;
        double containerWidth = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        SetValue(ItemWidthProperty, double.NaN);

        // Visibility changes can cause Avalonia to ask us to measure again. Reuse the
        // justified geometry when the actual layout inputs did not change, otherwise a
        // single scroll boundary crossing would turn back into a full layout pass.
        if (CanReuseHorizontalGeometry(containerWidth, rowHeight))
            return new Size(containerWidth, _horizontalMeasuredHeight);

        _childWidths.Clear();
        _rows.Clear();
        _horizontalGeometryChildren.Clear();

        double currentRowW = 0;
        var currentRow = new List<Control>();

        foreach (Control child in Children)
        {
            double childWidth = GetHorizontalChildWidth(child, rowHeight);
            _childWidths[child] = childWidth;
            _horizontalGeometryChildren.Add(child);

            if (currentRowW + childWidth > containerWidth && currentRow.Count > 0)
            {
                FlushRow(currentRow, ref currentRowW, containerWidth, rowHeight);
                currentRow.Clear();
                currentRowW = 0;
            }
            currentRow.Add(child);
            currentRowW += childWidth;
        }
        if (currentRow.Count > 0)
            FlushRow(currentRow, ref currentRowW, containerWidth, rowHeight);

        double totalHeight = _rows.Count > 0 ? _rows[^1].Y + _rows[^1].Height : 0;
        _horizontalGeometryWidth = containerWidth;
        _horizontalMeasuredHeight = totalHeight;
        _horizontalGeometryValid = true;
        _horizontalGeometryVersion++;
        _appliedHorizontalGeometryVersion = -1;
        return new Size(containerWidth, totalHeight);
    }

    private bool CanReuseHorizontalGeometry(double containerWidth, double rowHeight)
    {
        if (!_horizontalGeometryValid || _horizontalGeometryWidth != containerWidth
            || _horizontalGeometryChildren.Count != Children.Count)
            return false;

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!ReferenceEquals(_horizontalGeometryChildren[i], child)
                || !_childWidths.TryGetValue(child, out var existingWidth)
                || Math.Abs(existingWidth - GetHorizontalChildWidth(child, rowHeight)) > 0.01)
                return false;
        }

        return true;
    }

    private static double GetHorizontalChildWidth(Control child, double rowHeight)
    {
        if (child.DataContext is ImageViewItem item && item.Width > 0 && item.Height > 0)
            return rowHeight * (double)item.Width / item.Height + 10;
        return rowHeight * 0.75;
    }

    private void FlushRow(List<Control> row, ref double rowWidth, double containerWidth, double rowHeight)
    {
        double ratio = containerWidth / rowWidth;
        double actualHeight = rowHeight * ratio;
        double y = _rows.Count > 0 ? _rows[^1].Y + _rows[^1].Height : 0;
        _rows.Add(new RowInfo { Y = y, Height = actualHeight, Children = new List<Control>(row) });
        rowWidth = 0;
    }

    private Size ArrangeHorizontal(Size finalSize)
    {
        ManageBitmapLifecycle(_rows);

        bool geometryReused = _appliedHorizontalGeometryVersion == _horizontalGeometryVersion
            && _hasLastHorizontalArrangeSize
            && _lastHorizontalArrangeWidth == finalSize.Width;
        _lastArrangeGeometryReused = geometryReused;

        if (!geometryReused)
        {
            for (int rowIdx = 0; rowIdx < _rows.Count; rowIdx++)
            {
                var row = _rows[rowIdx];
                CacheHorizontalRowBounds(rowIdx, row, finalSize);
                // Horizontal mode relies on the ScrollViewer's clip for off-screen
                // content. Toggling IsVisible as a row crosses the viewport forces a
                // costly subtree layout, especially for wide rows. A geometry rebuild
                // is the one place where every child is made visible and arranged.
                if (SetRowVisibility(row, true))
                    _lastArrangeVisibilityRowsChanged++;
                ArrangeHorizontalRow(row);
            }

            _appliedHorizontalGeometryVersion = _horizontalGeometryVersion;
            _lastHorizontalArrangeWidth = finalSize.Width;
            _hasLastHorizontalArrangeSize = true;
        }
        else
        {
            // Keep cached child bounds and visibility untouched while scrolling.
            // Bitmap lifecycle still evaluates its independent buffered viewport.
            _lastArrangeVisibilityDeferred = _scrollActive;
        }

        return finalSize;
    }

    private void CacheHorizontalRowBounds(int rowIdx, RowInfo row, Size finalSize)
    {
        row.Bounds.Clear();
        bool isLast = rowIdx == _rows.Count - 1;
        double rowHeight = row.Height;

        if (isLast)
        {
            rowHeight = _rows.Count > 1 ? _rows[^2].Height : TargetRowHeight;
            double totalW = row.Children.Sum(child => GetHorizontalChildWidth(child, rowHeight));
            if (totalW > finalSize.Width && totalW > 0)
                rowHeight *= finalSize.Width / totalW;
        }

        double totalWidth = isLast
            ? 0
            : row.Children.Sum(child => _childWidths.TryGetValue(child, out var width) ? width : 100);
        double widthRatio = !isLast && totalWidth > 0 ? finalSize.Width / totalWidth : 1;
        double x = 0;
        foreach (var child in row.Children)
        {
            double width = isLast
                ? GetHorizontalChildWidth(child, rowHeight)
                : (_childWidths.TryGetValue(child, out var cachedWidth) ? cachedWidth : 100) * widthRatio;
            row.Bounds.Add(new Rect(x, row.Y, width, rowHeight));
            x += width;
        }
    }

    private bool SetRowVisibility(RowInfo row, bool isVisible)
    {
        bool changed = false;
        foreach (var child in row.Children)
        {
            if (child.IsVisible == isVisible)
                continue;
            child.IsVisible = isVisible;
            changed = true;
        }
        return changed;
    }

    private void ArrangeHorizontalRow(RowInfo row)
    {
        if (row.Bounds.Count != row.Children.Count)
            return;

        for (int i = 0; i < row.Children.Count; i++)
            row.Children[i].Arrange(row.Bounds[i]);
        _lastArrangeRows++;
    }

    #endregion

    #region Default Grid

    private Size MeasureDefault(Size availableSize)
    {
        double itemWidth = ColumnWidth;
        double cellWidth = itemWidth + 10; // Matches thumbnail template Margin=5 on both sides.
        SetValue(ItemWidthProperty, itemWidth);
        _childWidths.Clear();
        _rows.Clear();

        double curX = 0, curY = 0, maxRowH = 0;
        var currentRow = new List<Control>();

        foreach (Control child in Children)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            _childWidths[child] = cellWidth;

            if (curX + cellWidth > availableSize.Width && curX > 0)
            {
                _rows.Add(new RowInfo { Y = curY, Height = maxRowH, Children = new List<Control>(currentRow) });
                curX = 0; curY += maxRowH; maxRowH = 0;
                currentRow.Clear();
            }
            curX += cellWidth;
            maxRowH = Math.Max(maxRowH, child.DesiredSize.Height);
            currentRow.Add(child);
        }
        if (currentRow.Count > 0)
            _rows.Add(new RowInfo { Y = curY, Height = maxRowH, Children = new List<Control>(currentRow) });

        return new Size(availableSize.Width, curY + maxRowH);
    }

    private Size ArrangeDefault(Size finalSize)
    {
        foreach (var row in _rows)
        {
            double curX = 0;
            foreach (var child in row.Children)
            {
                double w = _childWidths.TryGetValue(child, out double cw) ? cw : 100;
                child.Arrange(new Rect(curX, row.Y, w, row.Height));
                curX += w;
            }
        }
        return finalSize;
    }

    #endregion
}
