using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ImageManager.App.Controls;

/// <summary>Virtualized responsive equal-cell grid for folder cards.</summary>
public sealed class ResponsiveFolderGridPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<ResponsiveFolderGridPanel, double>(nameof(MinItemWidth), 150d);
    public static readonly StyledProperty<double> ItemSpacingProperty = AvaloniaProperty.Register<ResponsiveFolderGridPanel, double>(nameof(ItemSpacing), 10d);
    public static readonly StyledProperty<double> ItemAspectRatioProperty = AvaloniaProperty.Register<ResponsiveFolderGridPanel, double>(nameof(ItemAspectRatio), 1d);
    public static readonly StyledProperty<double> ViewportBufferProperty = AvaloniaProperty.Register<ResponsiveFolderGridPanel, double>(nameof(ViewportBuffer), 500d);
    public static readonly AttachedProperty<double> ItemWidthProperty = AvaloniaProperty.RegisterAttached<ResponsiveFolderGridPanel, Control, double>("ItemWidth", double.NaN, inherits: true);

    private readonly Dictionary<int, Realized> _realized = new();
    private readonly Dictionary<object, Stack<Control>> _recycled = new();
    private Rect _viewport;
    private Layout _layout;
    private readonly record struct Realized(Control Control, object? RecycleKey, bool NeedsContainer);
    private readonly record struct Layout(double Width, double Height, int Columns, double Spacing, double ItemWidth, double ItemHeight);

    static ResponsiveFolderGridPanel()
    {
        MinItemWidthProperty.Changed.AddClassHandler<ResponsiveFolderGridPanel>((p, _) => p.InvalidateMeasure());
        ItemSpacingProperty.Changed.AddClassHandler<ResponsiveFolderGridPanel>((p, _) => p.InvalidateMeasure());
        ItemAspectRatioProperty.Changed.AddClassHandler<ResponsiveFolderGridPanel>((p, _) => p.InvalidateMeasure());
        ViewportBufferProperty.Changed.AddClassHandler<ResponsiveFolderGridPanel>((p, _) => p.InvalidateMeasure());
    }

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double ItemSpacing { get => GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }
    public double ItemAspectRatio { get => GetValue(ItemAspectRatioProperty); set => SetValue(ItemAspectRatioProperty, value); }
    public double ViewportBuffer { get => GetValue(ViewportBufferProperty); set => SetValue(ViewportBufferProperty, value); }
    public static void SetItemWidth(Control element, double value) => element.SetValue(ItemWidthProperty, value);
    public static double GetItemWidth(Control element) => element.GetValue(ItemWidthProperty);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); EffectiveViewportChanged += OnEffectiveViewportChanged; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { EffectiveViewportChanged -= OnEffectiveViewportChanged; ClearRealized(); _recycled.Clear(); base.OnDetachedFromVisualTree(e); }
    protected override void OnItemsChanged(IReadOnlyList<object?> items, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) { ClearRealized(); base.OnItemsChanged(items, e); InvalidateMeasure(); }

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout = CalculateLayout(availableSize.Width);
        EnsureRealized(GetViewport(availableSize));
        foreach (var (index, value) in _realized) Measure(value.Control);
        return new Size(_layout.Width, _layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout = CalculateLayout(finalSize.Width);
        EnsureRealized(GetViewport(finalSize));
        foreach (var (index, value) in _realized) { Measure(value.Control); value.Control.Arrange(GetRect(index)); }
        return new Size(Math.Max(finalSize.Width, _layout.Width), Math.Max(finalSize.Height, _layout.Height));
    }

    protected override Control? ContainerFromIndex(int index) => _realized.TryGetValue(index, out var item) ? item.Control : null;
    protected override int IndexFromContainer(Control control)
    {
        foreach (var (index, item) in _realized)
            if (ReferenceEquals(item.Control, control)) return index;
        return -1;
    }
    protected override IEnumerable<Control> GetRealizedContainers() => _realized.OrderBy(pair => pair.Key).Select(pair => pair.Value.Control).ToArray();
    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        var viewer = this.FindAncestorOfType<ScrollViewer>();
        if (viewer is not null)
        {
            var rect = GetRect(index);
            var contentOffset = GetContentOffsetY(viewer);
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, contentOffset + rect.Y - Math.Max(0, (viewer.Viewport.Height - rect.Height) / 2)));
        }
        var viewportHeight = viewer?.Viewport.Height > 0 ? viewer.Viewport.Height : 900;
        EnsureRealized(new Rect(0, GetRect(index).Y, Bounds.Width, viewportHeight));
        return ContainerFromIndex(index);
    }
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var index = from is Control control ? IndexFromContainer(control) : -1;
        var next = direction switch { NavigationDirection.First => 0, NavigationDirection.Last => Items.Count - 1, NavigationDirection.Left => index - 1, NavigationDirection.Right => index + 1, NavigationDirection.Up => index - _layout.Columns, NavigationDirection.Down => index + _layout.Columns, _ => -1 };
        return next >= 0 && next < Items.Count ? ScrollIntoView(next) : null;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e) { _viewport = e.EffectiveViewport; EnsureRealized(_viewport); InvalidateMeasure(); }
    private Rect GetViewport(Size availableSize)
    {
        if (_viewport.Width > 0 && _viewport.Height > 0) return _viewport;
        var viewer = this.FindAncestorOfType<ScrollViewer>();
        return viewer is { Viewport.Height: > 0 } ? new Rect(0, Math.Max(0, viewer.Offset.Y - GetContentOffsetY(viewer)), viewer.Viewport.Width, viewer.Viewport.Height) : new Rect(0, 0, Math.Max(0, availableSize.Width), 900);
    }
    private void EnsureRealized(Rect viewport)
    {
        if (Items.Count == 0 || _layout.ItemHeight <= 0 || viewport.Height <= 0) { ClearRealized(); return; }
        var stride = _layout.ItemHeight + _layout.Spacing;
        var rows = (Items.Count + _layout.Columns - 1) / _layout.Columns;
        var buffer = Math.Max(0, ViewportBuffer);
        var firstRow = Math.Max(0, (int)Math.Floor((viewport.Y - buffer) / stride));
        var lastRow = Math.Min(rows - 1, (int)Math.Floor((viewport.Bottom + buffer) / stride));
        var first = firstRow * _layout.Columns;
        var end = Math.Min(Items.Count, (lastRow + 1) * _layout.Columns);
        foreach (var index in _realized.Keys.Where(index => index < first || index >= end).ToArray()) Unrealize(index);
        for (var index = first; index < end; index++) if (!_realized.ContainsKey(index)) Realize(index);
    }
    private void Realize(int index)
    {
        var generator = ItemContainerGenerator ?? throw new InvalidOperationException("The virtualizing panel has no item container generator.");
        var item = Items[index]; var needs = generator.NeedsContainer(item, index, out var key);
        Control control;
        if (needs && key is not null && _recycled.TryGetValue(key, out var pool) && pool.TryPop(out var recycled)) control = recycled;
        else control = needs ? generator.CreateContainer(item, index, key) ?? throw new InvalidOperationException("Container generation failed.") : item as Control ?? throw new InvalidOperationException("A non-container item must request a container.");
        if (needs) generator.PrepareItemContainer(control, item, index);
        AddInternalChild(control); _realized.Add(index, new Realized(control, key, needs)); generator.ItemContainerPrepared(control, item, index);
    }
    private void Unrealize(int index)
    {
        if (!_realized.Remove(index, out var item)) return;
        RemoveInternalChild(item.Control); if (!item.NeedsContainer) return;
        var generator = ItemContainerGenerator ?? throw new InvalidOperationException("The virtualizing panel has no item container generator.");
        generator.ClearItemContainer(item.Control); if (item.RecycleKey is null) return;
        if (!_recycled.TryGetValue(item.RecycleKey, out var pool)) _recycled.Add(item.RecycleKey, pool = new Stack<Control>()); pool.Push(item.Control);
    }
    private void ClearRealized() { foreach (var index in _realized.Keys.ToArray()) Unrealize(index); }
    private Layout CalculateLayout(double availableWidth)
    {
        var width = double.IsFinite(availableWidth) && availableWidth >= 0 ? availableWidth : Math.Max(Bounds.Width, MinItemWidth);
        var spacing = Math.Max(0, ItemSpacing); var minimum = Math.Max(1, MinItemWidth);
        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (minimum + spacing)));
        var itemWidth = Math.Max(0, (width - spacing * (columns - 1)) / columns);
        var ratio = ItemAspectRatio > 0 && double.IsFinite(ItemAspectRatio) ? ItemAspectRatio : 1;
        var itemHeight = itemWidth / ratio; var rows = Items.Count == 0 ? 0 : (Items.Count + columns - 1) / columns;
        return new Layout(width, rows == 0 ? 0 : rows * itemHeight + (rows - 1) * spacing, columns, spacing, itemWidth, itemHeight);
    }
    private Rect GetRect(int index) { var row = index / _layout.Columns; var column = index % _layout.Columns; return new Rect(column * (_layout.ItemWidth + _layout.Spacing), row * (_layout.ItemHeight + _layout.Spacing), _layout.ItemWidth, _layout.ItemHeight); }
    private void Measure(Control control) { SetItemWidth(control, _layout.ItemWidth); control.Measure(new Size(_layout.ItemWidth, _layout.ItemHeight)); }
    private double GetContentOffsetY(ScrollViewer viewer) { var transform = this.TransformToVisual(viewer); return transform?.Transform(default).Y + viewer.Offset.Y ?? 0; }
}
