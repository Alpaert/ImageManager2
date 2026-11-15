using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;

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

    public static readonly StyledProperty<bool> IsPagedProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, bool>(nameof(IsPaged));
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, string>(nameof(Mode), "None");
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, double>(nameof(ColumnWidth), 160);
    public static readonly StyledProperty<double> GridThumbnailHeightProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, double>(nameof(GridThumbnailHeight), double.NaN);
    public static readonly StyledProperty<bool> KeepPaddingProperty =
        AvaloniaProperty.Register<VirtualizingWaterfallPanel, bool>(nameof(KeepPadding));

    private readonly Dictionary<int, RealizedContainer> _realized = new();
    private readonly Dictionary<object, Stack<Control>> _recycled = new();
    private readonly DispatcherTimer _effectiveViewportTimer;
    private Rect _effectiveViewport;
    private Rect? _pendingEffectiveViewport;
    private int _publishedGeometryItemCount = -1;
    private double? _lastPublishedViewportY;
    private long _lastEffectiveViewportEventTimestamp;
    private ImageDisplayGeometryIndex? _pagedGeometry;
    private bool _pagedGeometryDirty = true;
    private bool _rangesDirty = true;
    private (double Width, double Scaling, bool RoundLayout) _pagedGeometryMeasureKey;
    private readonly HashSet<ImageViewItem> _pagedSizeItems = new();
    private int[] _pagedVisibleIndexes = [];
    private int[] _pagedRetainedIndexes = [];
    private int[] _pagedPrefetchIndexes = [];
    private (double Y, double Height, double Buffer)? _queriedPagedViewport;
    private (ImageViewItem Item, double RelativeY)? _pagedAnchor;
    private int _pagedAnchorVersion;

    private ImageDisplayGeometryIndex? ActiveGeometry => IsPaged ? _pagedGeometry : GeometryIndex;

    private static readonly TimeSpan ViewportUpdateInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan HighFrequencyViewportInterval = TimeSpan.FromMilliseconds(50);

    private readonly record struct RealizedContainer(Control Control, object? Item, object? RecycleKey, bool NeedsContainer);

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
            panel._rangesDirty = true;
            panel.InvalidateMeasure();
        });
        IsPagedProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) => panel.InvalidatePagedGeometry());
        ModeProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) => panel.InvalidatePagedGeometry());
        ColumnWidthProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) => panel.InvalidatePagedGeometry());
        GridThumbnailHeightProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) => panel.InvalidatePagedGeometry());
        KeepPaddingProperty.Changed.AddClassHandler<VirtualizingWaterfallPanel>((panel, _) => panel.InvalidatePagedGeometry());
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

    public bool IsPaged { get => GetValue(IsPagedProperty); set => SetValue(IsPagedProperty, value); }
    public string Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public double GridThumbnailHeight { get => GetValue(GridThumbnailHeightProperty); set => SetValue(GridThumbnailHeightProperty, value); }
    public bool KeepPadding { get => GetValue(KeepPaddingProperty); set => SetValue(KeepPaddingProperty, value); }

    public IReadOnlyList<ImageViewItem> PagedVisibleItems { get; private set; } = [];
    public IReadOnlyList<ImageViewItem> PagedRetainedItems { get; private set; } = [];
    public IReadOnlyList<ImageViewItem> PagedPrefetchItems { get; private set; } = [];
    public event EventHandler? PagedViewportChanged;
    public bool IsPagedViewportCurrent => IsEffectivelyVisible && !_pagedGeometryDirty && !_rangesDirty;

    public IEnumerable<Control> RealizedControls => _realized.Values.Select(container => container.Control);

    public IEnumerable<ImageViewItem> GetPagedItemsIntersecting(Rect selection)
    {
        if (!IsPagedViewportCurrent || _pagedGeometry is not { } geometry)
            yield break;

        var scale = UseLayoutRounding ? LayoutHelper.GetLayoutScale(this) : 1;
        for (var index = 0; index < geometry.Count; index++)
        {
            if (!geometry.TryGetItemRect(index, out var rect) || Items[index] is not ImageViewItem item)
                continue;

            var origin = new Point(rect.X, rect.Y);
            var size = new Size(rect.Width, rect.Height);
            if (UseLayoutRounding)
            {
                origin = LayoutHelper.RoundLayoutPoint(origin, scale);
                size = LayoutHelper.RoundLayoutSizeUp(size, scale);
            }
            var bounds = _realized.TryGetValue(index, out var realized) && realized.Control.IsArrangeValid
                ? realized.Control.Bounds
                : new Rect(origin, size);
            if (selection.Intersects(bounds))
                yield return item;
        }
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
        _rangesDirty = true;
        _pagedGeometryDirty = true;
        _pagedAnchor = null;
        _pagedAnchorVersion++;
        foreach (var item in _pagedSizeItems)
            item.PropertyChanged -= OnPagedItemSizeChanged;
        _pagedSizeItems.Clear();
        PagedVisibleItems = [];
        PagedRetainedItems = [];
        PagedPrefetchItems = [];
        _lastPublishedViewportY = null;
        CurrentScrollDirection = ContinuousScrollDirection.None;
        _recycled.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    public void ResetPagedViewport()
    {
        if (!IsPaged)
            return;

        _rangesDirty = true;
        _effectiveViewport = default;
        _pagedAnchor = null;
        _pagedAnchorVersion++;
        CancelPendingViewportUpdate();
        ClearRealizedContainers();
        _recycled.Clear();
        PagedVisibleItems = [];
        PagedRetainedItems = [];
        PagedPrefetchItems = [];
        _lastPublishedViewportY = null;
        CurrentScrollDirection = ContinuousScrollDirection.None;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (IsPaged)
            EnsurePagedGeometry(availableSize.Width);
        if (!HasPendingViewportUpdate)
            EnsureRealizedForViewport(GetViewportForLayout(availableSize));

        var geometry = ActiveGeometry;
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
        var geometry = ActiveGeometry;
        if (geometry is not null)
        {
            foreach (var (index, container) in _realized)
            {
                if (geometry.TryGetItemRect(index, out var rect))
                {
                    if (!container.Control.IsMeasureValid)
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
        NotifyCollectionChangedEventArgs e)
    {
        _rangesDirty = true;
        if (IsPaged)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _lastPublishedViewportY = null;
                CurrentScrollDirection = ContinuousScrollDirection.None;
            }
            InvalidatePagedGeometry(preserveAnchor: e.Action != NotifyCollectionChangedAction.Reset);
            CancelPendingViewportUpdate(preserveLatestViewport: true);
            ReconcilePagedContainers(items, e);
            base.OnItemsChanged(items, e);
            return;
        }

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
            e.OldStartingIndex >= 0 && e.OldItems?.Count == 1 &&
            TryApplyIncrementalRemoval(items, e.OldStartingIndex))
        {
            base.OnItemsChanged(items, e);
            _publishedGeometryItemCount = items.Count;
            CurrentVisibleRange = default;
            CurrentRetainedRange = default;
            InvalidateMeasure();
            return;
        }

        // The item source can change while a scrollbar drag has a coalesced
        // viewport pending. Preserve that newest position so the replacement
        // source is realized for the current scroll offset.
        CancelPendingViewportUpdate(preserveLatestViewport: true);
        ClearRealizedContainers();
        base.OnItemsChanged(items, e);
        InvalidateMeasure();
    }

    private void ReconcilePagedContainers(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        if (_realized.Count == 0)
            return;

        var previous = _realized.ToArray();
        _realized.Clear();
        var added = e.NewItems?.Count ?? 0;
        var removed = e.OldItems?.Count ?? 0;
        var generator = ItemContainerGenerator;
        foreach (var (oldIndex, container) in previous)
        {
            var newIndex = e.Action switch
            {
                NotifyCollectionChangedAction.Add when oldIndex >= e.NewStartingIndex => oldIndex + added,
                NotifyCollectionChangedAction.Remove when oldIndex >= e.OldStartingIndex + removed => oldIndex - removed,
                NotifyCollectionChangedAction.Move when removed == 1 && oldIndex == e.OldStartingIndex => e.NewStartingIndex,
                NotifyCollectionChangedAction.Move when removed == 1 && oldIndex > e.OldStartingIndex &&
                    oldIndex <= e.NewStartingIndex => oldIndex - 1,
                NotifyCollectionChangedAction.Move when removed == 1 && oldIndex >= e.NewStartingIndex &&
                    oldIndex < e.OldStartingIndex => oldIndex + 1,
                _ => oldIndex
            };
            if (e.Action == NotifyCollectionChangedAction.Reset ||
                (uint)newIndex >= (uint)items.Count || !ReferenceEquals(container.Item, items[newIndex]))
            {
                RemoveRealizedContainer(container);
                continue;
            }

            _realized.Add(newIndex, container);
            if (oldIndex != newIndex)
                generator?.ItemContainerIndexChanged(container.Control, oldIndex, newIndex);
        }
    }

    private void InvalidatePagedGeometry(bool preserveAnchor = true)
    {
        if (!preserveAnchor)
        {
            _pagedAnchor = null;
            _pagedAnchorVersion++;
        }
        else if (!_pagedGeometryDirty)
        {
            CapturePagedAnchor();
        }
        _pagedGeometryDirty = true;
        _rangesDirty = true;
        InvalidateMeasure();
    }

    private void CapturePagedAnchor()
    {
        var viewport = _pendingEffectiveViewport ?? _effectiveViewport;
        if (_pagedGeometry is null || viewport.Y <= 0)
            return;

        var closestDistance = double.PositiveInfinity;
        foreach (var index in _pagedGeometry.QueryPagedViewport(viewport.Y, viewport.Height))
        {
            if (!_realized.TryGetValue(index, out var container) || container.Item is not ImageViewItem item ||
                !_pagedGeometry.TryGetItemRect(index, out var rect))
                continue;

            // Prefer the nearest row top even when another column contains a long card.
            var distance = Math.Abs(rect.Y - viewport.Y);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                _pagedAnchor = (item, rect.Y - viewport.Y);
            }
        }
    }

    private void EnsurePagedGeometry(double availableWidth)
    {
        var width = double.IsFinite(availableWidth) && availableWidth > 0
            ? availableWidth
            : ImageDisplayGeometryOptions.DefaultContainerWidth;
        var measureKey = (width, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, UseLayoutRounding);
        if (!_pagedGeometryDirty && _pagedGeometryMeasureKey == measureKey)
            return;
        if (!_pagedGeometryDirty)
            CapturePagedAnchor();

        var mode = Mode switch
        {
            "Vertical" => ImageDisplayLayoutMode.Vertical,
            "Horizontal" => ImageDisplayLayoutMode.Horizontal,
            _ => ImageDisplayLayoutMode.None
        };
        var sizes = new ImageDisplayItemSize[Items.Count];
        var currentSizeItems = new HashSet<ImageViewItem>();
        var anchorIndex = -1;
        for (var index = 0; index < sizes.Length; index++)
        {
            if (Items[index] is not ImageViewItem item)
                continue;
            sizes[index] = new ImageDisplayItemSize(item.Width, item.Height);
            if (mode != ImageDisplayLayoutMode.None)
                currentSizeItems.Add(item);
            if (ReferenceEquals(item, _pagedAnchor?.Item))
                anchorIndex = index;
        }
        foreach (var item in _pagedSizeItems)
        {
            if (!currentSizeItems.Contains(item))
                item.PropertyChanged -= OnPagedItemSizeChanged;
        }
        foreach (var item in currentSizeItems)
        {
            if (!_pagedSizeItems.Contains(item))
                item.PropertyChanged += OnPagedItemSizeChanged;
        }
        _pagedSizeItems.Clear();
        _pagedSizeItems.UnionWith(currentSizeItems);

        var baseWidth = double.IsFinite(ColumnWidth) && ColumnWidth > 0
            ? ColumnWidth
            : ImageDisplayGeometryOptions.DefaultThumbnailBaseWidth;
        var itemWidth = mode switch
        {
            ImageDisplayLayoutMode.None => baseWidth,
            ImageDisplayLayoutMode.Vertical => width / Math.Max(1, (int)(width / baseWidth)),
            _ => double.NaN
        };
        if (!SmartWaterfallPanel.GetItemWidth(this).Equals(itemWidth))
            SmartWaterfallPanel.SetItemWidth(this, itemWidth);

        // Grid cards share one fixed image height and template chrome. A single
        // representative card captures Avalonia's DPI and margin rounding.
        double? gridCellHeight = null;
        if (mode == ImageDisplayLayoutMode.None && Items.Count > 0 && IsEffectivelyVisible)
        {
            if (_realized.Count == 0)
                Realize(0);
            var sample = _realized.Values.First().Control;
            sample.Measure(new Size(baseWidth + ImageDisplayGeometryOptions.ItemGap, double.PositiveInfinity));
            gridCellHeight = sample.DesiredSize.Height;
        }

        _pagedGeometry = ImageDisplayGeometryIndex.BuildPaged(sizes,
            new ImageDisplayGeometryOptions(mode, width, baseWidth, 1), GridThumbnailHeight, KeepPadding, gridCellHeight);
        _pagedGeometryMeasureKey = measureKey;
        _pagedGeometryDirty = false;
        _rangesDirty = true;
        var anchorVersion = ++_pagedAnchorVersion;
        if (_pagedAnchor is { } anchor && _pagedGeometry.TryGetItemRect(anchorIndex, out var rect) &&
            this.FindAncestorOfType<ScrollViewer>() is { } viewer)
        {
            var previousOffset = viewer.Offset.Y;
            var contentY = GetContentOffsetY(viewer);
            var targetY = rect.Y - anchor.RelativeY;
            var expectedOffset = Math.Clamp(contentY + targetY, 0,
                Math.Max(0, contentY + _pagedGeometry.ExtentHeight - viewer.Viewport.Height));
            CancelPendingViewportUpdate();
            _effectiveViewport = new Rect(_effectiveViewport.X, expectedOffset - contentY, _effectiveViewport.Width, _effectiveViewport.Height);
            // Measure publishes the new extent before the anchor offset is applied.
            Dispatcher.UIThread.Post(() =>
            {
                if (anchorVersion == _pagedAnchorVersion && IsEffectivelyVisible &&
                    Math.Abs(viewer.Offset.Y - previousOffset) < 1)
                    viewer.Offset = new Vector(viewer.Offset.X, GetContentOffsetY(viewer) + targetY);
            }, DispatcherPriority.Loaded);
        }
        _pagedAnchor = null;
    }

    private void OnPagedItemSizeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageViewItem.Width) or nameof(ImageViewItem.Height))
            InvalidatePagedGeometry();
    }

    private bool TryApplyIncrementalRemoval(IReadOnlyList<object?> items, int removedIndex)
    {
        if (removedIndex >= items.Count + 1)
            return false;

        CancelPendingViewportUpdate(preserveLatestViewport: true);
        var generator = ItemContainerGenerator;
        if (generator is null)
            return false;

        // The source shifts every cached entry after the removed index left by one.
        // Move the corresponding controls too, rebinding each control to the same
        // ImageViewItem instance so decoded thumbnails survive the deletion.
        if (_realized.ContainsKey(removedIndex))
            Unrealize(removedIndex);

        // Move upward through the old keys. Moving downward would write key - 1
        // over a container that has not been read yet, orphaning that visual.
        foreach (var key in _realized.Keys.Where(key => key > removedIndex).OrderBy(key => key).ToArray())
        {
            var realized = _realized[key];
            _realized.Remove(key);
            var newIndex = key - 1;
            if (newIndex >= items.Count)
            {
                RemoveRealizedContainer(realized);
                continue;
            }

            generator.ItemContainerIndexChanged(realized.Control, key, newIndex);
            _realized[newIndex] = realized;
        }

        return true;
    }

    private void RemoveRealizedContainer(RealizedContainer realized)
    {
        RemoveInternalChild(realized.Control);
        if (!realized.NeedsContainer)
            return;

        var generator = ItemContainerGenerator;
        if (generator is null)
            return;
        generator.ClearItemContainer(realized.Control);
        if (realized.RecycleKey is null)
            return;

        if (!_recycled.TryGetValue(realized.RecycleKey, out var pool))
            _recycled.Add(realized.RecycleKey, pool = new Stack<Control>());
        pool.Push(realized.Control);
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
        var geometry = ActiveGeometry;
        if (geometry is null || !geometry.TryGetItemRect(index, out var rect))
            return null;

        var viewer = this.FindAncestorOfType<ScrollViewer>();
        var viewport = GetViewportForLayout(new Size(geometry.ExtentWidth, 900));
        var viewportHeight = viewer is { Viewport.Height: > 0 } ? viewer.Viewport.Height : viewport.Height;
        var centeringOffset = (viewportHeight - rect.Height) / 2;
        var targetY = rect.Y - (IsPaged ? centeringOffset : Math.Max(0, centeringOffset));
        if (viewer is not null)
        {
            _pagedAnchorVersion++;
            var contentY = GetContentOffsetY(viewer);
            viewer.Offset = new Vector(viewer.Offset.X, contentY + targetY);
            targetY = viewer.Offset.Y - contentY;
        }

        CancelPendingViewportUpdate();
        _effectiveViewport = new Rect(viewport.X, targetY, viewport.Width, viewportHeight);
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
        if (IsPaged && !IsEffectivelyVisible)
            return;
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
        if (EnsureRealizedForViewport(viewport))
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
                IsPaged ? viewer.Offset.Y - GetContentOffsetY(viewer) : Math.Max(0, viewer.Offset.Y - GetContentOffsetY(viewer)),
                viewer.Viewport.Width,
                viewer.Viewport.Height);
        }

        var width = Bounds.Width > 0
            ? Bounds.Width
            : double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, 900) : 900;
        return new Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
    }

    private double GetContentOffsetY(ScrollViewer viewer)
    {
        var transform = this.TransformToVisual(viewer);
        return transform?.Transform(default).Y + viewer.Offset.Y ?? 0;
    }

    private void MeasureContainer(Control control, ImageDisplayItemRect rect)
    {
        if (!IsPaged && !SmartWaterfallPanel.GetItemWidth(control).Equals(rect.Width))
            SmartWaterfallPanel.SetItemWidth(control, rect.Width);
        var height = IsPaged && _pagedGeometry?.Options.Mode == ImageDisplayLayoutMode.None
            ? double.PositiveInfinity
            : rect.Height;
        control.Measure(new Size(rect.Width, height));
    }

    private bool EnsureRealizedForViewport(Rect viewport)
    {
        var geometry = ActiveGeometry;
        if (IsPaged)
            return EnsurePagedViewport(viewport, geometry);
        if (geometry is null || geometry.Count == 0 || Items.Count != geometry.Count || viewport.Height <= 0)
        {
            if (_realized.Count == 0 && !_rangesDirty)
                return false;
            var hadContainers = _realized.Count > 0;
            ClearRealizedContainers();
            _rangesDirty = false;
            _lastPublishedViewportY = null;
            CurrentScrollDirection = ContinuousScrollDirection.None;
            PublishRanges(default, default);
            return hadContainers;
        }

        var visible = geometry.QueryViewport(viewport.Y, viewport.Height);
        var buffer = Math.Max(0, ViewportBuffer);
        var retained = geometry.QueryViewport(Math.Max(0, viewport.Y - buffer), viewport.Height + buffer * 2);
        if (retained.IsEmpty)
            retained = visible;

        var previousDirection = CurrentScrollDirection;
        UpdateScrollDirection(viewport.Y);
        // During a fast scrollbar drag the effective viewport can move many
        // times inside the same retained window. Keep the slider responsive by
        // avoiding container churn until the virtualized source window changes.
        if (!_rangesDirty && visible == CurrentVisibleRange && retained == CurrentRetainedRange &&
            previousDirection == CurrentScrollDirection)
            return false;

        var changed = false;
        foreach (var index in _realized.Keys.Where(index => index < retained.StartIndex || index >= retained.EndExclusive).ToArray())
        {
            Unrealize(index);
            changed = true;
        }

        for (var index = retained.StartIndex; index < retained.EndExclusive; index++)
        {
            if (!_realized.ContainsKey(index))
            {
                Realize(index);
                changed = true;
            }
        }

        _rangesDirty = false;
        PublishRanges(visible, retained);
        return changed;
    }

    private bool EnsurePagedViewport(Rect viewport, ImageDisplayGeometryIndex? geometry)
    {
        if (_pagedGeometryDirty || !IsEffectivelyVisible)
            return false;

        var buffer = Math.Max(0, ViewportBuffer);
        var viewportKey = (viewport.Y, viewport.Height, buffer);
        if (!_rangesDirty && _queriedPagedViewport == viewportKey)
            return false;
        _queriedPagedViewport = viewportKey;
        UpdateScrollDirection(viewport.Y);
        var visible = geometry?.QueryPagedViewport(viewport.Y, viewport.Height) ?? [];
        var retained = geometry?.QueryPagedViewport(viewport.Y - buffer, viewport.Height + buffer * 2) ?? [];
        var prefetch = geometry is not null && retained.Length > 0
            ? GetPagedPrefetchIndexes(geometry, viewport, buffer, retained, CurrentScrollDirection)
            : [];
        if (!_rangesDirty && visible.AsSpan().SequenceEqual(_pagedVisibleIndexes) &&
            retained.AsSpan().SequenceEqual(_pagedRetainedIndexes) &&
            prefetch.AsSpan().SequenceEqual(_pagedPrefetchIndexes))
            return false;

        var changed = false;
        var wanted = retained.ToHashSet();
        foreach (var index in _realized.Keys.Where(index => !wanted.Contains(index)).ToArray())
        {
            Unrealize(index);
            changed = true;
        }
        foreach (var index in retained)
        {
            if (!_realized.ContainsKey(index))
            {
                Realize(index);
                changed = true;
            }
        }

        _pagedVisibleIndexes = visible;
        _pagedRetainedIndexes = retained;
        _pagedPrefetchIndexes = prefetch;
        PagedVisibleItems = visible.Select(index => Items[index]).OfType<ImageViewItem>().ToArray();
        PagedRetainedItems = retained.Select(index => Items[index]).OfType<ImageViewItem>().ToArray();
        PagedPrefetchItems = prefetch.Select(index => Items[index]).OfType<ImageViewItem>().ToArray();
        _rangesDirty = false;
        PagedViewportChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    internal static int[] GetPagedPrefetchIndexes(
        ImageDisplayGeometryIndex geometry,
        Rect viewport,
        double buffer,
        int[] retainedIndexes,
        ContinuousScrollDirection direction)
    {
        const int screensAhead = 3;
        const int maximumItems = 48;
        var up = direction == ContinuousScrollDirection.Up;
        var edge = up ? viewport.Y - buffer : viewport.Bottom + buffer;
        var height = viewport.Height * screensAhead;
        var start = up ? edge - height : edge;
        return geometry.QueryPagedViewport(start, height)
            .Where(index => Array.BinarySearch(retainedIndexes, index) < 0)
            .OrderBy(index =>
            {
                geometry.TryGetItemRect(index, out var rect);
                return up ? edge - rect.Bottom : rect.Y - edge;
            })
            .Take(maximumItems)
            .ToArray();
    }

    private void PublishRanges(
        ImageDisplayRange visible,
        ImageDisplayRange retained)
    {
        CurrentVisibleRange = visible;
        CurrentRetainedRange = retained;
        VisibleRangeChanged?.Invoke(this, visible);
        RetainedRangeChanged?.Invoke(this, retained);
    }

    private void UpdateScrollDirection(double viewportY)
    {
        if (_lastPublishedViewportY is { } previousY)
        {
            // ScrollToIndex uses fractional geometry while the arranged viewport
            // can round it by less than one DIP. Do not reverse prefetch direction
            // for that jitter; retain the baseline so slow real movement accumulates.
            if (Math.Abs(viewportY - previousY) < 1)
                return;
            if (viewportY > previousY)
                CurrentScrollDirection = ContinuousScrollDirection.Down;
            else if (viewportY < previousY)
                CurrentScrollDirection = ContinuousScrollDirection.Up;
        }
        _lastPublishedViewportY = viewportY;
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
        _realized.Add(index, new RealizedContainer(container, item, recycleKey, needsContainer));
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

        // An ItemsControl source reset can leave a child that is no longer
        // represented in our index map. In particular, deleting the final item
        // must still remove every old card even after the map is already empty.
        foreach (var child in Children.ToArray())
            RemoveInternalChild(child);
    }
}
