using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ImageManager.App.ViewModels;

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

    private readonly Dictionary<Control, double> _childWidths = new();
    private readonly List<RowInfo> _rows = new();

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
                InvalidateArrange();
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
        double buf = h;
        return y + h >= _scrollOffsetY - buf && y <= _scrollOffsetY + _viewportHeight + buf;
    }

    // ==================== Measure / Arrange ====================

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size(0, 0);
        return Mode switch
        {
            "Vertical" => MeasureVertical(availableSize),
            "Horizontal" => MeasureHorizontal(availableSize),
            _ => MeasureDefault(availableSize)
        };
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        EnsureScrollViewer();
        return Mode switch
        {
            "Vertical" => ArrangeVertical(finalSize),
            "Horizontal" => ArrangeHorizontal(finalSize),
            _ => ArrangeDefault(finalSize)
        };
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
        foreach (var (child, x, y, w, h) in _verticalLayout)
        {
            child.Measure(new Size(w, h));
            child.Arrange(new Rect(x, y, w, h));
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
        for (int rowIdx = 0; rowIdx < _rows.Count; rowIdx++)
        {
            var row = _rows[rowIdx];
            bool isLast = rowIdx == _rows.Count - 1;
            double rowHeight = TargetRowHeight;

            if (isLast)
            {
                double x = 0;
                foreach (var child in row.Children)
                {
                    double w = _childWidths.TryGetValue(child, out double cw) ? cw : rowHeight * 0.75;
                    child.Measure(new Size(w, rowHeight));
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
                    child.Measure(new Size(w, actualHeight));
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
        SetValue(ItemWidthProperty, itemWidth);
        _childWidths.Clear();
        _rows.Clear();

        double curX = 0, curY = 0, maxRowH = 0;
        var currentRow = new List<Control>();

        foreach (Control child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            _childWidths[child] = child.DesiredSize.Width;

            if (curX + child.DesiredSize.Width > availableSize.Width && curX > 0)
            {
                _rows.Add(new RowInfo { Y = curY, Height = maxRowH, Children = new List<Control>(currentRow) });
                curX = 0; curY += maxRowH; maxRowH = 0;
                currentRow.Clear();
            }
            curX += child.DesiredSize.Width;
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
