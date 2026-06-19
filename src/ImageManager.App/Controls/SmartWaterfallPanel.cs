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
        ModeProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) => x.InvalidateMeasure());
        ColumnWidthProperty.Changed.AddClassHandler<SmartWaterfallPanel>((x, e) => x.InvalidateMeasure());
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
    private double _viewportHeight;
    private double _scrollOffsetY;
    private bool _scrollWired;
    private bool _arrangePending;

    private readonly Dictionary<Control, double> _childWidths = new();
    private readonly List<RowInfo> _rows = new();

    // Bitmap lifecycle: track which controls had their bitmaps released
    private readonly HashSet<Control> _bitmapFreed = new();
    private int _lastVisibleStartRow = -1;
    private int _lastVisibleEndRow = -1;
    private PageManager? _cachedPageManager;
    private Control? _firstTrackedChild; // detect ItemsSource reset (page flip)

    private sealed class RowInfo
    {
        public double Y;
        public double Height;
        public List<Control> Children = new();
    }

    private void EnsureScrollViewer()
    {
        if (_scrollWired) return;
        _scrollWired = true;
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollOffsetY = _scrollViewer.Offset.Y;
            _viewportHeight = _scrollViewer.Viewport.Height;
            _scrollHandler = (_, _) =>
            {
                _scrollOffsetY = _scrollViewer.Offset.Y;
                _viewportHeight = _scrollViewer.Viewport.Height;
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollViewer != null && _scrollHandler != null)
        {
            _scrollViewer.ScrollChanged -= _scrollHandler;
            _scrollHandler = null;
        }
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

        // Only process if visible range changed
        if (visibleStart == _lastVisibleStartRow && visibleEnd == _lastVisibleEndRow)
            return;
        _lastVisibleStartRow = visibleStart;
        _lastVisibleEndRow = visibleEnd;

        // Release bitmaps for rows outside visible range
        for (int i = 0; i < rows.Count; i++)
        {
            bool isVisible = i >= visibleStart && i <= visibleEnd;
            foreach (var child in rows[i].Children)
            {
                if (child.DataContext is ImageViewItem item)
                {
                    if (!isVisible && item.ThumbnailData != null)
                    {
                        item.ThumbnailData = null; // release byte[], GC reclaims Bitmap
                        item.IsLoaded = false;     // mark as unloaded so page-revisit path reloads it
                        _bitmapFreed.Add(child);
                    }
                    else if (isVisible && _bitmapFreed.Contains(child))
                    {
                        item.NotifyThumbnailNeeded();
                        _bitmapFreed.Remove(child);
                        // Trigger PageManager to reload this thumbnail (cached to avoid service locator in hot path)
                        _cachedPageManager ??= App.Services.GetRequiredService<PageManager>();
                        _cachedPageManager.LoadThumbnailsForItems(new List<ImageViewItem> { item });
                    }
                }
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
        if (sw.ElapsedMilliseconds > 5)
            PerfLogger.Log($"[Layout] MeasureOverride mode={Mode} children={Children.Count} elapsed={sw.ElapsedMilliseconds}ms");
        return result;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        var sw = Stopwatch.StartNew();
        EnsureScrollViewer();
        var result = Mode switch
        {
            "Vertical" => ArrangeVertical(finalSize),
            "Horizontal" => ArrangeHorizontal(finalSize),
            _ => ArrangeDefault(finalSize)
        };
        if (sw.ElapsedMilliseconds > 5)
            PerfLogger.Log($"[Layout] ArrangeOverride mode={Mode} children={Children.Count} elapsed={sw.ElapsedMilliseconds}ms");
        return result;
    }

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
        var rows = new List<RowInfo>();
        foreach (var group in _verticalLayout.GroupBy(l => l.Y))
        {
            var row = new RowInfo { Y = group.Key, Height = group.Max(l => l.H), Children = group.Select(l => l.Child).ToList() };
            rows.Add(row);
        }
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
        _childWidths.Clear();
        _rows.Clear();

        double currentRowW = 0;
        var currentRow = new List<Control>();

        foreach (Control child in Children)
        {
            double childWidth;
            if (child is Control ctrl && ctrl.DataContext is ImageViewItem item
                && item.Width > 0 && item.Height > 0)
                childWidth = rowHeight * (double)item.Width / item.Height + 10;
            else
                childWidth = rowHeight * 0.75;
            _childWidths[child] = childWidth;

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
        return new Size(containerWidth, totalHeight);
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

        for (int rowIdx = 0; rowIdx < _rows.Count; rowIdx++)
        {
            var row = _rows[rowIdx];

            // 视口裁剪：不可见行隐藏，保留在视觉树中以确保绑定正常
            if (!IsRowVisible(row.Y, row.Height))
            {
                foreach (var child in row.Children)
                    child.IsVisible = false;
                continue;
            }

            foreach (var child in row.Children)
                child.IsVisible = true;

            bool isLast = rowIdx == _rows.Count - 1;

            if (isLast)
            {
                double rowHeight = _rows.Count > 1 ? _rows[_rows.Count - 2].Height : TargetRowHeight;

                // 先算总宽度，若超出窗口则等比缩小行高
                double totalW = 0;
                foreach (var child in row.Children)
                {
                    if (child is Control ctrl && ctrl.DataContext is ImageViewItem item
                        && item.Width > 0 && item.Height > 0)
                        totalW += rowHeight * (double)item.Width / item.Height + 10;
                    else
                        totalW += rowHeight * 0.75;
                }
                if (totalW > finalSize.Width && totalW > 0)
                    rowHeight *= finalSize.Width / totalW;

                double x = 0;
                foreach (var child in row.Children)
                {
                    double w;
                    if (child is Control ctrl && ctrl.DataContext is ImageViewItem item
                        && item.Width > 0 && item.Height > 0)
                        w = rowHeight * (double)item.Width / item.Height + 10;
                    else
                        w = rowHeight * 0.75;
                    child.Arrange(new Rect(x, row.Y, w, rowHeight));
                    x += w;
                }
            }
            else
            {
                double totalW = 0;
                foreach (var c in row.Children)
                    totalW += _childWidths.TryGetValue(c, out double cw) ? cw : 100;
                double ratio = finalSize.Width / totalW;
                double actualHeight = row.Height;
                double x = 0;
                foreach (var child in row.Children)
                {
                    double w = (_childWidths.TryGetValue(child, out double cw) ? cw : 100) * ratio;
                    child.Arrange(new Rect(x, row.Y, w, actualHeight));
                    x += w;
                }
            }
        }
        return finalSize;
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
