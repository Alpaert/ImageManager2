using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Models;
using ImageManager.App.Services;

namespace ImageManager.App.Controls;

/// <summary>
/// Virtualizes a precomputed image layout. The geometry index owns the full logical
/// extent; this panel only owns controls around the effective viewport.
/// </summary>
public sealed class VirtualizingWaterfallPanel : VirtualizingPanel
{
    public static readonly StyledProperty<ImageDisplayGeometryIndex?> GeometryIndexProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, ImageDisplayGeometryIndex?>(nameof(GeometryIndex));

    public static readonly StyledProperty<double> ViewportBufferProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, double>(nameof(ViewportBuffer), 800d);

    private readonly Dictionary<int, RealizedContainer> _realized = new();
    private readonly Dictionary<object, Stack<Control>> _recycled = new();
    private readonly DispatcherTimer _effectiveViewportTimer;
    private Rect _effectiveViewport;
    private Rect? _pendingEffectiveViewport;
    private int _publishedGeometryItemCount = -1;
    private double? _lastPublishedViewportY;
    private long _lastEffectiveViewportEventTimestamp;

    private static readonly TimeSpan ViewportUpdateInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan HighFrequencyViewportInterval = TimeSpan.FromMilliseconds(50);

    private readonly record struct RealizedContainer(Control Control, object? RecycleKey, bool NeedsContainer);

    public VirtualizingWaterfallPanel()
    {
        _effectiveViewportTimer = new DispatcherTimer(
            ViewportUpdateInterval,
            DispatcherPriority.Render,
            OnEffectiveViewportTimerTick);
    }

    static VirtualizingWaterfallPanel()
    {
        GeometryIndexProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) =>
        {
            // A resize changes positions but not items. Retain the visible
            // containers in that case so their decoded thumbnail visuals are not
            // removed for a white frame before the next Measure/Arrange pass.
            var count = panel.GeometryIndex?.Count ?? 0;
            if (panel.GeometryIndex is null || panel._publishedGeometryItemCount != count || panel.Items.Count != count)
            {
                panel.CancelPendingViewportUpdate(preserveLatestViewport: true);
                panel.ClearRealizedContainers();
            }
            panel._publishedGeometryItemCount = count;
            panel.InvalidateMeasure();
        });
        ViewportBufferProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) =>
        {
            panel.InvalidateMeasure();
        });
    }

    public ImageDisplayGeometryIndex? GeometryIndex
    {
        get => GetValue(GeometryIndexProperty);
        set => SetValue(GeometryIndexProperty, value);
    }

    /// <summary>Extra logical pixels retained before and after the viewport.</summary>
    public double ViewportBuffer
    {
        get => GetValue(ViewportBufferProperty);
        set => SetValue(ViewportBufferProperty, value);
    }

    /// <summary>Raised after the panel updates its visible source-index range.</summary>
    public event EventHandler<ImageDisplayRange>? VisibleRangeChanged;

    /// <summary>
    /// Raised with the source range whose containers may remain cached. Consumers use
    /// it to release their own per-item state without retaining the whole result set.
    /// </summary>
    public event EventHandler<ImageDisplayRange>? RetainedRangeChanged;

    /// <summary>Most recently calculated visible source range.</summary>
    public ImageDisplayRange CurrentVisibleRange { get; private set; }

    /// <summary>Most recently calculated source range retained by this panel.</summary>
    public ImageDisplayRange CurrentRetainedRange { get; private set; }

    /// <summary>Direction of the latest effective viewport movement.</summary>
    public ContinuousScrollDirection CurrentScrollDirection { get; private set; }

    public int RealizedCount => _realized.Count;

    /// <summary>
    /// Scrolls the owning viewer to a source index and realizes only its local
    /// viewport window. This is the public path-based navigation entry point for
    /// continuous display; it never expands the item source.
    /// </summary>
    public bool ScrollToIndex(int index) => ScrollIntoView(index) is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        CancelPendingViewportUpdate(preserveLatestViewport: true);
        ClearRealizedContainers();
        _recycled.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!HasPendingViewportUpdate)
            EnsureRealizedForViewport(GetViewportForLayout(availableSize));

        var geometry = GeometryIndex;
        if (geometry is not null)
        {
            foreach (var (index, container) in _realized)
            {
                if (geometry.TryGetItemRect(index, out var rect))
                    MeasureContainer(container.Control, rect);
            }
        }
        return geometry is null
            ? default
            : new Size(geometry.ExtentWidth, geometry.ExtentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!HasPendingViewportUpdate)
            EnsureRealizedForViewport(GetViewportForLayout(finalSize));
        var geometry = GeometryIndex;
        if (geometry is not null)
        {
            foreach (var (index, container) in _realized)
            {
                if (geometry.TryGetItemRect(index, out var rect))
                {
                    // Reuse the existing thumbnail template's attached width contract so
                    // paged and continuous item templates remain identical.
                    MeasureContainer(container.Control, rect);
                    container.Control.Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
                }
            }
        }

        return geometry is null
            ? finalSize
            : new Size(Math.Max(finalSize.Width, geometry.ExtentWidth), Math.Max(finalSize.Height, geometry.ExtentHeight));
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // The item source can change while a scrollbar drag has a coalesced
        // viewport pending. Preserve that newest position so the replacement
        // source is realized for the current scroll offset.
        CancelPendingViewportUpdate(preserveLatestViewport: true);
        ClearRealizedContainers();
        base.OnItemsChanged(items, e);
        InvalidateMeasure();
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var container) ? container.Control : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
        {
            if (ReferenceEquals(realized.Control, container))
                return index;
        }

        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers() =>
        _realized.OrderBy(pair => pair.Key).Select(pair => pair.Value.Control).ToArray();

    protected override Control? ScrollIntoView(int index)
    {
        var geometry = GeometryIndex;
        if (geometry is null || !geometry.TryGetItemRect(index, out var rect))
            return null;

        var viewer = this.FindAncestorOfType<ScrollViewer>();
        var viewport = GetViewportForLayout(new Size(geometry.ExtentWidth, 900));
        var maxY = Math.Max(0, geometry.ExtentHeight - viewport.Height);
        var targetY = Math.Clamp(rect.Y - Math.Max(0, (viewport.Height - rect.Height) / 2), 0, maxY);
        if (viewer is not null)
        {
            viewer.Offset = new Vector(viewer.Offset.X, targetY);
        }

        CancelPendingViewportUpdate();
        _effectiveViewport = new Rect(viewport.X, targetY, viewport.Width, viewport.Height);
        EnsureRealizedForViewport(_effectiveViewport);
        InvalidateMeasure();
        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        var indexes = _realized.Keys.Order().ToArray();
        if (indexes.Length == 0)
            return null;

        var currentIndex = from is Control control ? IndexFromContainer(control) : -1;
        var position = Array.IndexOf(indexes, currentIndex);
        var next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => indexes.Length - 1,
            NavigationDirection.Up or NavigationDirection.Left => position > 0 ? position - 1 : wrap ? indexes.Length - 1 : -1,
            NavigationDirection.Down or NavigationDirection.Right => position >= 0 && position < indexes.Length - 1 ? position + 1 : wrap ? 0 : -1,
            _ => -1
        };

        return next >= 0 ? _realized[indexes[next]].Control : null;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var previousEvent = _lastEffectiveViewportEventTimestamp;
        _lastEffectiveViewportEventTimestamp = now;

        if (previousEvent != 0 && Stopwatch.GetElapsedTime(previousEvent, now) < HighFrequencyViewportInterval)
        {
            _pendingEffectiveViewport = e.EffectiveViewport;
            if (!_effectiveViewportTimer.IsEnabled)
                _effectiveViewportTimer.Start();
            return;
        }

        StopPendingViewportTimer();
        ProcessEffectiveViewport(e.EffectiveViewport);
    }

    private void OnEffectiveViewportTimerTick(object? sender, EventArgs e)
    {
        var viewport = _pendingEffectiveViewport;
        _pendingEffectiveViewport = null;
        _effectiveViewportTimer.Stop();
        if (viewport is { } latestViewport)
            ProcessEffectiveViewport(latestViewport);
    }

    private void ProcessEffectiveViewport(Rect viewport)
    {
        _effectiveViewport = viewport;
        EnsureRealizedForViewport(viewport);
        InvalidateMeasure();
    }

    private bool HasPendingViewportUpdate =>
        _effectiveViewportTimer.IsEnabled && _pendingEffectiveViewport.HasValue;

    private void CancelPendingViewportUpdate(bool preserveLatestViewport = false)
    {
        if (preserveLatestViewport && _pendingEffectiveViewport is { } pendingViewport)
            _effectiveViewport = pendingViewport;
        StopPendingViewportTimer();
        _lastEffectiveViewportEventTimestamp = 0;
    }

    private void StopPendingViewportTimer()
    {
        _effectiveViewportTimer.Stop();
        _pendingEffectiveViewport = null;
    }

    private Rect GetViewportForLayout(Size availableSize)
    {
        if (_effectiveViewport.Width > 0 && _effectiveViewport.Height > 0)
            return _effectiveViewport;

        // ScrollViewer measures its content with an infinite scroll-axis size.
        // Treating that as zero leaves the first viewport unrealized forever;
        // treating Arrange's full logical extent as the viewport realizes every
        // item. Prefer the viewer's actual viewport and use only a bounded
        // first-layout fallback until EffectiveViewportChanged arrives.
        var viewer = this.FindAncestorOfType<ScrollViewer>();
        if (viewer is not null && viewer.Viewport.Width > 0 && viewer.Viewport.Height > 0)
        {
            return new Rect(
                viewer.Offset.X,
                viewer.Offset.Y,
                viewer.Viewport.Width,
                viewer.Viewport.Height);
        }

        var width = Bounds.Width > 0
            ? Bounds.Width
            : double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, 900) : 900;
        return new Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
    }

    private static void MeasureContainer(Control control, ImageDisplayItemRect rect)
    {
        // Width must propagate to the image template before measuring it. New
        // containers may also be created during Arrange after a viewport change.
        SmartWaterfallPanel.SetItemWidth(control, rect.Width);
        control.Measure(new Size(rect.Width, rect.Height));
    }

    private void EnsureRealizedForViewport(Rect viewport)
    {
        var geometry = GeometryIndex;
        if (geometry is null || geometry.Count == 0 || Items.Count != geometry.Count || viewport.Height <= 0)
        {
            ClearRealizedContainers();
            _lastPublishedViewportY = null;
            CurrentScrollDirection = ContinuousScrollDirection.None;
            PublishRanges(default, default, null);
            return;
        }

        var visible = geometry.QueryViewport(viewport.Y, viewport.Height);
        var buffer = Math.Max(0, ViewportBuffer);
        var retained = geometry.QueryViewport(Math.Max(0, viewport.Y - buffer), viewport.Height + buffer * 2);
        if (retained.IsEmpty)
            retained = visible;

        // During a fast scrollbar drag the effective viewport can move many
        // times inside the same retained window. Keep the slider responsive by
        // avoiding container churn until the virtualized source window changes.
        if (visible == CurrentVisibleRange && retained == CurrentRetainedRange)
        {
            _effectiveViewport = viewport;
            return;
        }

        foreach (var index in _realized.Keys.Where(index => index < retained.StartIndex || index >= retained.EndExclusive).ToArray())
            Unrealize(index);

        for (var index = retained.StartIndex; index < retained.EndExclusive; index++)
        {
            if (!_realized.ContainsKey(index))
                Realize(index);
        }

        PublishRanges(visible, retained, viewport.Y);
    }

    private void PublishRanges(
        ImageDisplayRange visible,
        ImageDisplayRange retained,
        double? viewportY)
    {
        if (viewportY is { } currentY && _lastPublishedViewportY is { } previousY)
        {
            if (currentY > previousY)
                CurrentScrollDirection = ContinuousScrollDirection.Down;
            else if (currentY < previousY)
                CurrentScrollDirection = ContinuousScrollDirection.Up;
        }
        _lastPublishedViewportY = viewportY;
        CurrentVisibleRange = visible;
        CurrentRetainedRange = retained;
        VisibleRangeChanged?.Invoke(this, visible);
        RetainedRangeChanged?.Invoke(this, retained);
    }

    private void Realize(int index)
    {
        var items = Items ?? throw new InvalidOperationException("The virtualizing panel is not attached to an ItemsControl.");
        var item = items[index];
        var generator = ItemContainerGenerator
            ?? throw new InvalidOperationException("The virtualizing panel has no item container generator.");
        var needsContainer = generator.NeedsContainer(item, index, out var recycleKey);
        Control container;
        if (needsContainer && recycleKey is not null && _recycled.TryGetValue(recycleKey, out var pool) && pool.TryPop(out var recycled))
        {
            container = recycled;
        }
        else
        {
            container = needsContainer
                ? generator.CreateContainer(item, index, recycleKey)
                    ?? throw new InvalidOperationException("The item container generator returned no container.")
                : item as Control
                    ?? throw new InvalidOperationException("A non-container item must request an item container.");
        }

        if (needsContainer)
            generator.PrepareItemContainer(container, item, index);

        AddInternalChild(container);
        _realized.Add(index, new RealizedContainer(container, recycleKey, needsContainer));
        generator.ItemContainerPrepared(container, item, index);
    }

    private void Unrealize(int index)
    {
        if (!_realized.Remove(index, out var realized))
            return;

        RemoveInternalChild(realized.Control);
        if (!realized.NeedsContainer)
            return;

        var generator = ItemContainerGenerator
            ?? throw new InvalidOperationException("The virtualizing panel has no item container generator.");
        generator.ClearItemContainer(realized.Control);
        if (realized.RecycleKey is null)
            return;

        if (!_recycled.TryGetValue(realized.RecycleKey, out var pool))
            _recycled.Add(realized.RecycleKey, pool = new Stack<Control>());
        pool.Push(realized.Control);
    }

    private void ClearRealizedContainers()
    {
        foreach (var index in _realized.Keys.ToArray())
            Unrealize(index);
    }
}
