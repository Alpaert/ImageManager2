using System.Diagnostics;
using System.Runtime.CompilerServices;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Caching;

using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Video;
using Avalonia.Threading;
using IDispatcher = ImageManager.Core.Services.IDispatcher;

namespace ImageManager.App.Services;

public readonly record struct PageUiState(
    double ThumbnailBaseWidth,
    string WaterfallMode,
    double ThumbnailAspectRatio);

public readonly record struct PageChangedEventArgs(
    List<ImageViewItem> Items,
    int PageIndex,
    int TotalPages,
    string LoadedInfoText);

public class PageManager : IDisposable
{
    public const int DefaultPageSize = ImagePaging.Default;
    private const int MaxCachedPages = 3;
    private const int MaxConcurrentThumbnailLoads = 6;
    private const int MaxNonViewportThumbnailLoads = MaxConcurrentThumbnailLoads - 1;
    private static readonly double[] ZoomLevels = { 160, 183, 213, 256, 284, 320, 366, 427, 512, 640 };

    private readonly ThumbnailCacheService _thumbCache;
    private readonly IFolderRepository _folderRepo;
    private readonly IImageMetaRepository? _metaRepo;
    private readonly IDispatcher _dispatcher;
    private long _thumbnailRequestEpoch;
    private readonly ConditionalWeakTable<ImageViewItem, ThumbnailRequestState> _itemRequests = new();
    private readonly HashSet<(ImageViewItem Item, int Width, CancellationToken Token)> _queuedRequests = new();
    private readonly Dictionary<ImageViewItem, CancellationTokenSource> _pagedRetainedRequests = new();
    private readonly HashSet<ImageViewItem> _pagedBackgroundRequests = new();
    private readonly HashSet<ImageViewItem> _pagedOptionalPrefetch = new();
    private readonly Dictionary<ImageViewItem, CancellationTokenSource> _continuousRetainedRequests = new();
    private readonly HashSet<ImageViewItem> _continuousBackgroundRequests = new();
    private readonly HashSet<ImageViewItem> _continuousOptionalPrefetch = new();
    private readonly ThumbnailBitmapPrefetcher _pagedBitmapPrefetcher = new();
    private readonly ThumbnailBitmapPrefetcher _continuousBitmapPrefetcher = new();
    private sealed class ThumbnailRequestState { public long Version; }

    /// <summary>The window owns viewport requests; page models still cover the whole page.</summary>
    public bool UseViewportLoading { get; set; }

    private readonly PageCache _cache = new();
    private readonly ThumbnailScheduler _scheduler = new();
    private int _activePageIndex;
    private int _pageSize = DefaultPageSize;

    private int? _preSearchPageIndex;

    private int _thumbnailDecodeWidth = 200;
    private int _currentZoomLevel;
    private PageUiState _currentUiState;
    private long _zoomVersion = 0;  // 版本号机制，防止防抖竞态
    private CancellationTokenSource? _zoomDebounceCts;
    private CancellationTokenSource? _pageLoadCts;
    private CancellationTokenSource? _preloadCts;
    private readonly object _preloadStateLock = new();
    private PreloadRequest? _lastPreloadRequest;
    private long _preloadGeneration;
    private bool _scrollActive;
    private bool _disposed;
    private readonly object _viewportMetricsLock = new();
    private ThumbnailBatchMetrics? _viewportMetrics;

    private sealed record PreloadRequest(
        int CurrentPage,
        int TotalPages,
        List<string> ActiveFileList,
        Func<string, List<string>> GetTagsForFile,
        CancellationToken ParentToken);

    public event Action<PageChangedEventArgs>? PageChanged;

    /// <summary>
    /// Raised on the UI dispatcher after a debounced zoom invalidates decoded
    /// thumbnails. Continuous display owns a separate, virtualized item cache
    /// and uses this to refresh only its active ranges.
    /// </summary>
    public event Func<bool>? ThumbnailRefreshRequested;

    /// <summary>Decode width captured by explicit cache-warming commands.</summary>
    public int ThumbnailDecodeWidth => _thumbnailDecodeWidth;

    public int PageSize
    {
        get => _pageSize;
        set
        {
            var normalized = ImagePaging.Clamp(value);
            if (_pageSize == normalized)
                return;

            _pageSize = normalized;
            _preSearchPageIndex = null;
            CancelPageLoad();
            lock (_preloadStateLock)
                CancelPreloadLocked(clearLastRequest: true);
            _cache.Clear(ResetThumbnailState);
        }
    }

    public PageManager(
        ThumbnailCacheService thumbCache,
        IFolderRepository folderRepo,
        IImageMetaRepository? metaRepo = null,
        IDispatcher? dispatcher = null)
    {
        _thumbCache = thumbCache;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _dispatcher = dispatcher ?? new ImageManager.App.Helpers.AvaloniaDispatcher();
    }

    // ==================== Public API ====================

    public async Task ShowPageAsync(
        int pageIndex, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile,
        bool isSearchResult,
        string? currentFolder)
    {
        if (pageIndex < 0 || pageIndex >= totalPages) return;

        var sw = Stopwatch.StartNew();
        PerfLogger.Log($"[PageMgr] ShowPage START page={pageIndex}/{totalPages}");
        AppLogger.Memory($"Page.Show.Start page={pageIndex} cached={CachedPageCount} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1}");
        _thumbCache.TrimForPressure();

        // Cancel any in-flight thumbnail loads from previous page
        ClearPagedViewport();
        CancelPageLoad();
        _pageLoadCts = new CancellationTokenSource();
        var loadCt = _pageLoadCts.Token;

        _activePageIndex = pageIndex;

        var pageItems = await GetOrCreatePageItemsAsync(
            pageIndex, totalPages, activeFileList, getTagsForFile, loadCt);
        if (pageItems == null || loadCt.IsCancellationRequested)
            return;

        bool needsLoad = !pageItems.TrueForAll(i => i.IsLoaded && !i.IsRefreshingThumbnail);

        if (needsLoad && !UseViewportLoading)
        {
            PerfLogger.Log($"[PageMgr] LoadThumbnails START unloaded={pageItems.Count(i => !i.IsLoaded || i.IsRefreshingThumbnail)}");
            _ = LoadPageThumbnailsAsync(pageIndex, loadCt)
                .ContinueWith(_ => PreloadAdjacentPages(pageIndex, totalPages, activeFileList, getTagsForFile, loadCt),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
        }
        else
        {
            PreloadAdjacentPages(pageIndex, totalPages, activeFileList, getTagsForFile, loadCt);
        }

        PageChanged?.Invoke(new PageChangedEventArgs(
            pageItems, pageIndex, totalPages,
            $"当前页: {pageIndex + 1}/{totalPages}  每页 {PageSize} 张"));
        PerfLogger.Log($"[PageMgr] ShowPage END elapsed={sw.ElapsedMilliseconds}ms");
        AppLogger.Memory($"Page.Show.End page={pageIndex} cached={CachedPageCount} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1} elapsedMs={sw.ElapsedMilliseconds}");

        if (!isSearchResult && !string.IsNullOrEmpty(currentFolder))
            _ = Task.Run(() => _folderRepo.SetLastPageIndexAsync(currentFolder!, pageIndex));
        _dispatcher.Post(() =>
        {
            if (!_disposed && !loadCt.IsCancellationRequested && _activePageIndex == pageIndex)
                TrimPageCache(pageIndex, totalPages);
        });

        // Requests are pressure/idle/cooldown gated. Worker-initiated GC still pauses the UI.
        var pressure = MemoryPressureMonitor.Current;
        if (pressure >= MemoryPressureMonitor.PressureLevel.High)
            MemoryPressureMonitor.CompactLoh();

        await Task.CompletedTask;
    }

    private int CachedPageCount => _cache.Count;

    private void CancelPageLoad()
    {
        Interlocked.Increment(ref _thumbnailRequestEpoch);
        ClearPagedViewport(releaseBytes: false);
        if (_pageLoadCts != null)
        {
            _pageLoadCts.Cancel();
            _pageLoadCts.Dispose();
            _pageLoadCts = null;
        }
        lock (_preloadStateLock)
            CancelPreloadLocked(clearLastRequest: true);
    }

    /// <summary>
    /// Pauses adjacent-page preloading while a scroll animation is active.
    /// The latest request is retained and resumed with the normal preload delay
    /// when scrolling becomes idle again.
    /// </summary>
    public void SetScrollActivity(bool active)
    {
        lock (_preloadStateLock)
        {
            if (_disposed || _scrollActive == active)
                return;

            _scrollActive = active;
            if (active)
            {
                CancelPreloadLocked(clearLastRequest: false);
                return;
            }

            var request = _lastPreloadRequest;
            if (request != null && !request.ParentToken.IsCancellationRequested)
                StartPreloadLocked(request);
        }
    }

    public bool IsScrollActive
    {
        get
        {
            lock (_preloadStateLock)
                return _scrollActive;
        }
    }

    public void UpdatePagedViewport(
        IReadOnlyList<ImageViewItem> pageItems,
        IReadOnlyList<ImageViewItem> visibleItems,
        IReadOnlyList<ImageViewItem> retainedItems,
        bool forceRefresh = false,
        IReadOnlyList<ImageViewItem>? prefetchItems = null)
    {
        if (_disposed || !UseViewportLoading) return;
        UpdateThumbnailWindow(_pagedRetainedRequests, _pagedBackgroundRequests, _pagedOptionalPrefetch, _pagedBitmapPrefetcher,
            pageItems, visibleItems, retainedItems, prefetchItems ?? [],
            _pageLoadCts?.Token ?? CancellationToken.None, forceRefresh, lookaheadFirst: false);
    }

    public void UpdateContinuousThumbnailWindow(
        IReadOnlyList<ImageViewItem> visibleItems,
        IReadOnlyList<ImageViewItem> retainedItems,
        IReadOnlyList<ImageViewItem> prefetchItems,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (_disposed || cancellationToken.IsCancellationRequested) return;
        UpdateThumbnailWindow(_continuousRetainedRequests, _continuousBackgroundRequests, _continuousOptionalPrefetch, _continuousBitmapPrefetcher,
            [], visibleItems, retainedItems, prefetchItems, cancellationToken, forceRefresh, lookaheadFirst: true);
    }

    private void UpdateThumbnailWindow(
        Dictionary<ImageViewItem, CancellationTokenSource> requests,
        HashSet<ImageViewItem> backgroundRequests,
        HashSet<ImageViewItem> optionalPrefetch,
        ThumbnailBitmapPrefetcher bitmapPrefetcher,
        IReadOnlyList<ImageViewItem> pageItems,
        IReadOnlyList<ImageViewItem> visibleItems,
        IReadOnlyList<ImageViewItem> retainedItems,
        IReadOnlyList<ImageViewItem> prefetchItems,
        CancellationToken parent,
        bool forceRefresh,
        bool lookaheadFirst)
    {
        // More lookahead data does not mean more attached controls. The existing
        // encoded/decoded cache budgets still apply, and pressure cuts optional work.
        var extraLimit = MemoryPressureMonitor.Current switch
        {
            >= MemoryPressureMonitor.PressureLevel.High => 0,
            MemoryPressureMonitor.PressureLevel.Medium => 16,
            _ => 64
        };
        var lookahead = prefetchItems.Take(extraLimit).ToArray();
        var visible = visibleItems.ToHashSet();
        var retained = retainedItems.Concat(lookahead).Concat(visibleItems).ToHashSet();
        optionalPrefetch.Clear();
        optionalPrefetch.UnionWith(lookahead.Except(retainedItems).Except(visibleItems));
        foreach (var item in requests.Keys.Where(item => !retained.Contains(item)).ToArray())
        {
            var cts = requests[item];
            requests.Remove(item);
            backgroundRequests.Remove(item);
            cts.Cancel();
            cts.Dispose();
            ResetThumbnailState(item);
        }
        // Cached pages and deletion refill may already contain bytes outside this viewport.
        foreach (var item in pageItems.Where(item => !retained.Contains(item) && item.ThumbnailData != null))
            ResetThumbnailState(item);

        var backgroundOrder = lookaheadFirst ? lookahead.Concat(retainedItems) : retainedItems.Concat(lookahead);
        foreach (var item in visibleItems.Concat(backgroundOrder).Distinct())
        {
            // A newly visible item must not remain queued behind background work.
            // Completed overlapping requests retain the same bytes and bitmap key.
            var promote = visible.Contains(item) && backgroundRequests.Contains(item) &&
                (!item.IsLoaded || item.IsRefreshingThumbnail);
            if ((forceRefresh || promote || (requests.TryGetValue(item, out var existing) && existing.IsCancellationRequested)) &&
                requests.Remove(item, out var old))
            {
                old.Cancel();
                old.Dispose();
                backgroundRequests.Remove(item);
            }
            if (!requests.TryGetValue(item, out var cts))
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
                requests[item] = cts;
                if (!visible.Contains(item)) backgroundRequests.Add(item);
            }
            if (!item.IsLoaded || item.IsRefreshingThumbnail)
                QueueThumbnail(item, cts.Token,
                    visible.Contains(item) || !backgroundRequests.Contains(item) ? ThumbnailLoadSource.Viewport : ThumbnailLoadSource.Preload);
        }
        bitmapPrefetcher.UpdateCandidates(lookahead.Where(item => !visible.Contains(item)).ToArray(), parent);
    }

    public void ClearPagedViewport(bool releaseBytes = true)
    {
        foreach (var (item, cts) in _pagedRetainedRequests)
        {
            cts.Cancel();
            cts.Dispose();
            if (releaseBytes) ResetThumbnailState(item);
        }
        _pagedRetainedRequests.Clear();
        _pagedBackgroundRequests.Clear();
        _pagedOptionalPrefetch.Clear();
        _pagedBitmapPrefetcher.Clear();
    }

    public void ClearContinuousViewport()
    {
        foreach (var (item, cts) in _continuousRetainedRequests)
        {
            cts.Cancel();
            cts.Dispose();
            ResetThumbnailState(item);
        }
        _continuousRetainedRequests.Clear();
        _continuousBackgroundRequests.Clear();
        _continuousOptionalPrefetch.Clear();
        _continuousBitmapPrefetcher.Clear();
    }

    public void TrimForMemoryPressure()
    {
        // Stop optional warming immediately even if no viewport event follows.
        _pagedBitmapPrefetcher.Clear();
        _continuousBitmapPrefetcher.Clear();
        DropOptionalPrefetch(_pagedRetainedRequests, _pagedBackgroundRequests, _pagedOptionalPrefetch);
        DropOptionalPrefetch(_continuousRetainedRequests, _continuousBackgroundRequests, _continuousOptionalPrefetch);
        _thumbCache.TrimForPressure();
        AsyncThumbnailImage.TrimIdleCache();
        lock (_preloadStateLock) CancelPreloadLocked(clearLastRequest: true);
        foreach (var pair in _cache.GetAllPages())
            if (pair.Key != _activePageIndex && _cache.Remove(pair.Key, out var items) && items != null)
                foreach (var item in items) ResetThumbnailState(item);
    }

    private void DropOptionalPrefetch(
        Dictionary<ImageViewItem, CancellationTokenSource> requests,
        HashSet<ImageViewItem> backgroundRequests,
        HashSet<ImageViewItem> optionalPrefetch)
    {
        foreach (var item in optionalPrefetch)
        {
            if (requests.Remove(item, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            backgroundRequests.Remove(item);
            ResetThumbnailState(item);
        }
        optionalPrefetch.Clear();
    }

    public void CancelCurrentLoads()
    {
        _zoomDebounceCts?.Cancel();
        ClearPagedViewport();
        ClearContinuousViewport();
        CancelPageLoad();
    }

    public void LoadThumbnailsForItems(List<ImageViewItem> items)
    {
        if (UseViewportLoading)
        {
            foreach (var item in items)
                if ((!item.IsLoaded || item.IsRefreshingThumbnail) &&
                    _pagedRetainedRequests.TryGetValue(item, out var retained))
                    QueueViewportThumbnail(item, retained.Token);
            return;
        }
        var toLoad = items.Where(i => !i.IsLoaded || i.IsRefreshingThumbnail).ToList();
        // Use current page's CancellationToken so page flip instantly cancels scroll-triggered loads
        var ct = _pageLoadCts?.Token ?? default;
        if (toLoad.Count == 0) return;

        foreach (var item in toLoad)
            QueueViewportThumbnail(item, ct);
    }

    public void LoadViewportThumbnailsForItems(
        IEnumerable<ImageViewItem>? items,
        CancellationToken cancellationToken)
    {
        if (items == null || cancellationToken.IsCancellationRequested)
            return;

        var queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (item == null || (item.IsLoaded && !item.IsRefreshingThumbnail) || string.IsNullOrWhiteSpace(item.FilePath))
                continue;

            if (!queuedPaths.Add(item.FilePath))
                continue;

            QueueThumbnail(item, cancellationToken, ThumbnailLoadSource.Viewport);
        }
    }

    /// <summary>
    /// Warms a small adjacent continuous-display range. It shares cancellation
    /// with the viewport request and uses the background concurrency budget so
    /// a newly visible image keeps a decode slot.
    /// </summary>
    public void LoadPrefetchThumbnailsForItems(
        IEnumerable<ImageViewItem>? items,
        CancellationToken cancellationToken)
    {
        if (items == null || cancellationToken.IsCancellationRequested)
            return;

        var queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (item == null || (item.IsLoaded && !item.IsRefreshingThumbnail) || string.IsNullOrWhiteSpace(item.FilePath))
                continue;

            if (!queuedPaths.Add(item.FilePath))
                continue;

            QueueThumbnail(item, cancellationToken, ThumbnailLoadSource.Preload);
        }
    }

    public async Task LoadThumbnailsForItemsAsync(List<ImageViewItem> items)
    {
        // Incremental deletion calls this with the whole page. The panel will
        // request newly visible refill items; do not defeat virtualization here.
        if (UseViewportLoading)
        {
            // Cache invalidation after deletion cancels the old retained tokens.
            // Rebuild from the panel's latest geometry before scheduling the refill.
            ThumbnailRefreshRequested?.GetInvocationList().OfType<Func<bool>>().Any(handler => handler());
            LoadThumbnailsForItems(items);
            return;
        }
        var ct = _pageLoadCts?.Token ?? default;
        var toLoad = items.Where(i => !i.IsLoaded || i.IsRefreshingThumbnail).ToList();
        try
        {
            foreach (var item in toLoad)
            {
                if (ct.IsCancellationRequested) return;
                await LoadSingleThumbnailAsync(item, ct, source: ThumbnailLoadSource.Viewport);
                PostLoadedItems([item]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task RegenerateThumbnailsAsync(List<ImageViewItem> items)
    {
        var targetPaths = new HashSet<string>(
            items.Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
                .Select(item => item.FilePath),
            StringComparer.OrdinalIgnoreCase);
        if (targetPaths.Count == 0)
            return;

        var cachedItems = _cache.GetAllItems()
            .Where(item => targetPaths.Contains(item.FilePath))
            .ToList();

        // Context commands may start with temporary items. Include every cached
        // projection so a successful regeneration is visible immediately.
        var affectedItems = new List<ImageViewItem>();
        foreach (var item in items.Concat(cachedItems).Where(item => targetPaths.Contains(item.FilePath)))
        {
            if (!affectedItems.Any(existing => ReferenceEquals(existing, item)))
                affectedItems.Add(item);
        }

        foreach (var path in targetPaths)
            _thumbCache.InvalidateThumbnail(path);

        foreach (var item in affectedItems)
        {
            item.ThumbnailData = null;
            item.HasNoPreview = false;
            item.IsRefreshingThumbnail = false;
            item.IsLoaded = false;
            item.IsLoading = true;
            item.NotifyAll();
        }

        foreach (var group in affectedItems.GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var primary = group.First();
            await LoadSingleThumbnailAsync(primary, source: ThumbnailLoadSource.Viewport);
            foreach (var item in group.Skip(1))
            {
                item.ThumbnailData = primary.ThumbnailData;
                item.Width = primary.Width;
                item.Height = primary.Height;
                item.HasNoPreview = primary.HasNoPreview;
                item.IsRefreshingThumbnail = primary.IsRefreshingThumbnail;
                item.IsLoaded = primary.IsLoaded;
            }
            PostLoadedItems(group);
        }
    }

    public int EstimateVisibleItemCount(PageUiState state)
    {
        double itemW = state.ThumbnailBaseWidth;
        double itemH = state.WaterfallMode == "None"
            ? state.ThumbnailBaseWidth / Math.Max(0.01, state.ThumbnailAspectRatio)
            : state.ThumbnailBaseWidth * 0.75;
        int perRow = Math.Max(1, (int)(900 / itemW));
        int rows = Math.Max(2, (int)(400 / itemH) + 1);
        return Math.Max(12, Math.Min(PageSize, perRow * rows));
    }

    public int ComputeDecodeWidth()
    {
        var baseWidth = ZoomLevels[_currentZoomLevel];
        if (string.Equals(_currentUiState.WaterfallMode, "Horizontal", StringComparison.OrdinalIgnoreCase))
        {
            int horizontalWidth = (int)(baseWidth * 3);
            return Math.Clamp(horizontalWidth, 512, 2048);
        }

        int w = (int)(baseWidth * 2);
        return Math.Clamp(w, 300, 1600);
    }

    public async Task RefreshDecodeWidthForCurrentModeAsync()
    {
        int newDecodeWidth = ComputeDecodeWidth();
        if (newDecodeWidth == _thumbnailDecodeWidth)
            return;

        Interlocked.Increment(ref _thumbnailRequestEpoch);
        _thumbnailDecodeWidth = newDecodeWidth;
        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;
        await _thumbCache.ClearAsync();

        foreach (var item in _cache.GetAllItems())
        {
            item.IsRefreshingThumbnail = item.ThumbnailData is { Length: > 0 };
            item.IsLoaded = item.IsRefreshingThumbnail || item.HasNoPreview;
            item.IsLoading = !item.IsLoaded;
            item.NotifyAll();
        }
        var handled = ThumbnailRefreshRequested?.GetInvocationList().OfType<Func<bool>>().Any(handler => handler()) == true;
        if (!handled && !UseViewportLoading)
            _ = LoadPageThumbnailsAsync(_activePageIndex, _pageLoadCts?.Token ?? default);
    }

    public (double baseWidth, bool rebuildTriggered) OnZoomTickChanged(
        double value, int currentPage, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile)
    {
        double t = Math.Clamp(value, 1.0, 10.0);

        int idx = (int)t - 1;
        if (idx < 0) idx = 0;
        if (idx >= ZoomLevels.Length - 1) idx = ZoomLevels.Length - 2;

        double frac = t - (idx + 1);
        if (frac < 0) frac = 0;
        if (frac > 1) frac = 1;

        double baseWidth = ZoomLevels[idx] + (ZoomLevels[idx + 1] - ZoomLevels[idx]) * frac;

        // Keep WaterfallMode/AspectRatio from last UpdateUiState, replace baseWidth
        _currentUiState = new PageUiState(baseWidth, _currentUiState.WaterfallMode, _currentUiState.ThumbnailAspectRatio);

        int newLevel = (int)Math.Round(t - 1);
        if (newLevel < 0) newLevel = 0;
        if (newLevel >= ZoomLevels.Length) newLevel = ZoomLevels.Length - 1;

        if (newLevel != _currentZoomLevel)
        {
            _currentZoomLevel = newLevel;
            int newDecodeWidth = ComputeDecodeWidth();
            if (newDecodeWidth != _thumbnailDecodeWidth)
            {
                Interlocked.Increment(ref _thumbnailRequestEpoch);
                _thumbnailDecodeWidth = newDecodeWidth;
                _zoomDebounceCts?.Cancel();
                _zoomDebounceCts?.Dispose();
                _zoomDebounceCts = new CancellationTokenSource();
                var token = _zoomDebounceCts.Token;
                var capturedFileList = activeFileList;
                var expectedVersion = Interlocked.Increment(ref _zoomVersion);  // 生成新版本号

                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(300, token); }
                    catch { return; }
                    if (token.IsCancellationRequested) return;

                    // 版本号校验：确保只有最新的防抖任务生效
                    if (Interlocked.Read(ref _zoomVersion) != expectedVersion)
                    {
                        return;  // 已有更新的任务，放弃执行
                    }

                    var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
                    await dispatcher.InvokeAsync(async () =>
                    {
                        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;
                        await _thumbCache.ClearAsync();
                        // Keep decoded bytes visible while the final-size thumbnails are rebuilt.
                        // A failed replacement must not leave the viewport blank.
                        foreach (var item in _cache.GetAllItems())
                        {
                            if (item.ThumbnailData is { Length: > 0 })
                            {
                                item.IsRefreshingThumbnail = true;
                                item.IsLoading = false;
                            }
                            else if (!item.HasNoPreview)
                            {
                                item.IsLoaded = false;
                                item.IsLoading = true;
                            }
                        }
                        var handledByVirtualizedDisplay = ThumbnailRefreshRequested?
                            .GetInvocationList()
                            .OfType<Func<bool>>()
                            .Any(handler => handler()) == true;
                        if (!handledByVirtualizedDisplay && !UseViewportLoading)
                            _ = LoadPageThumbnailsAsync(_activePageIndex, _pageLoadCts?.Token ?? default);
                    });
                }, token);
                return (baseWidth, true);
            }
        }

        return (baseWidth, false);
    }

    public void InitializeDecodeWidth(int currentZoomLevel)
    {
        _currentZoomLevel = currentZoomLevel;
        _thumbnailDecodeWidth = ComputeDecodeWidth();
        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;
    }

    public void Dispose()
    {
        lock (_preloadStateLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelPreloadLocked(clearLastRequest: true);
        }
        CancelPageLoad();
        ClearContinuousViewport();
        _pagedBitmapPrefetcher.Dispose();
        _continuousBitmapPrefetcher.Dispose();
        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();

        // Page work is fire-and-forget and can still be unwinding from an
        // awaited gate after cancellation. These tiny process-lifetime gates
        // must remain valid until those continuations have observed cancel.
        // Disposing them here can turn normal shutdown into an
        // ObjectDisposedException or skip a reservation release.
    }

    public void InvalidateCache()
    {
        CancelPageLoad();
        _currentUiState = default;
        _cache.Clear(ResetThumbnailState);
    }

    public void InvalidateCacheExceptPage(int pageIndex, List<ImageViewItem> currentItems)
    {
        CancelPageLoad();
        _pageLoadCts = new CancellationTokenSource();
        _activePageIndex = pageIndex;
        foreach (var pair in _cache.GetAllPages())
        {
            if (pair.Key == pageIndex) continue;
            if (_cache.Remove(pair.Key, out var page) && page != null)
                foreach (var item in page)
                    ResetThumbnailState(item);
        }

        _cache.Set(pageIndex, currentItems);
    }

    public void UpdateUiState(PageUiState state) => _currentUiState = state;

    public void RemoveFromCache(int pageIndex)
    {
        _cache.Remove(pageIndex);
    }

    public void SetPageCache(int pageIndex, List<ImageViewItem> items)
    {
        _cache.Set(pageIndex, items);
    }

    public void SavePreSearchState(int pageIndex)
    {
        _preSearchPageIndex = pageIndex;
    }

    public bool TryRestorePreSearchState(out int pageIndex)
    {
        if (_preSearchPageIndex is int savedPageIndex)
        {
            _preSearchPageIndex = null;
            InvalidateCache();
            pageIndex = savedPageIndex;
            return true;
        }
        pageIndex = 0;
        return false;
    }

    private void ResetThumbnailState(ImageViewItem item)
    {
        Interlocked.Increment(ref _itemRequests.GetOrCreateValue(item).Version);
        item.ThumbnailData = null;
        item.HasNoPreview = false;
        item.IsRefreshingThumbnail = false;
        item.IsLoaded = false;
        item.IsLoading = true;
        item.NotifyAll();
    }

    // ==================== Private Methods ====================

    private async Task<List<ImageViewItem>?> GetOrCreatePageItemsAsync(
        int pageIndex,
        int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile,
        CancellationToken ct)
    {
        // InvalidateCache cancels page work before checking cache. Recheck
        // here so a stale dimension query cannot repopulate a newly-cleared cache.
        if (ct.IsCancellationRequested || _disposed)
            return null;

        if (_cache.TryGet(pageIndex, out var cachedItems))
            return cachedItems;

        // Repository queries can perform synchronous SQLite work before their Task yields.
        // Keep that work off the UI thread, but resume this caller's context so PageChanged
        // continues to update the bound collection on the dispatcher thread.
        var dimensions = await GetPageDimensionsAsync(pageIndex, activeFileList, ct);
        if (ct.IsCancellationRequested)
            return null;

        // Cancellation and cache invalidation can happen while the SQLite query is
        // running. Recheck while checking the cache to reject that stale page.
        if (ct.IsCancellationRequested || _disposed)
            return null;

        if (_cache.TryGet(pageIndex, out cachedItems))
            return cachedItems;

        var pageItems = CreatePlaceholderItems(
            pageIndex, totalPages, activeFileList, getTagsForFile, dimensions);
        _cache.Set(pageIndex, pageItems);
        PerfLogger.Log($"[PageMgr] CreatePlaceholders {pageItems.Count} items dimensions={dimensions.Count} page={pageIndex}");
        return pageItems;
    }

    private async Task<Dictionary<string, (int Width, int Height)>> GetPageDimensionsAsync(
        int pageIndex,
        List<string> activeFileList,
        CancellationToken ct)
    {
        if (_metaRepo == null)
            return new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);

        int start = pageIndex * PageSize;
        int count = Math.Min(PageSize, activeFileList.Count - start);
        if (count <= 0)
            return new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);

        var paths = activeFileList.GetRange(start, count);
        var sw = Stopwatch.StartNew();
        try
        {
            var dimensions = await Task.Run(
                () => _metaRepo.GetDimensionsByPathsAsync(paths), ct).ConfigureAwait(false);
            var invalidPaths = paths
                .Where(path => !dimensions.TryGetValue(path, out var size) || !HasUsableDimensions(size))
                .ToArray();

            // A previous thumbnail/original-frame extraction already knows the source
            // dimensions. Reuse that metadata before a thumbnail load can reach FFmpeg.
            if (invalidPaths.Length > 0)
            {
                var cachedDimensions = await _thumbCache
                    .GetCachedDimensionsAsync(invalidPaths, ct)
                    .ConfigureAwait(false);
                var repairs = cachedDimensions
                    .Where(pair => HasUsableDimensions(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                foreach (var (path, size) in repairs)
                    dimensions[path] = size;

                if (repairs.Count > 0)
                    await _metaRepo.UpdateDimensionsByPathsAsync(repairs).ConfigureAwait(false);
            }

            // Old video records commonly contain 0x0 or a placeholder 1x1. Neither
            // represents a real media aspect ratio and must not become a layout input.
            foreach (var path in paths.Where(path => dimensions.TryGetValue(path, out var size) && !HasUsableDimensions(size)).ToArray())
                dimensions.Remove(path);

            PerfLogger.Log($"[PageMgr] LoadDimensions page={pageIndex} requested={paths.Count} found={dimensions.Count} elapsed={sw.ElapsedMilliseconds}ms");
            return dimensions;
        }
        catch (Exception ex)
        {
            PerfLogger.Log($"[PageMgr] LoadDimensions FAIL page={pageIndex} elapsed={sw.ElapsedMilliseconds}ms error={ex.GetType().Name}");
            return new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private List<ImageViewItem> CreatePlaceholderItems(
        int pageIndex, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile,
        IReadOnlyDictionary<string, (int Width, int Height)> dimensions)
    {
        int start = pageIndex * PageSize;
        int count = Math.Min(PageSize, activeFileList.Count - start);
        var list = new List<ImageViewItem>();

        for (int i = 0; i < count; i++)
        {
            var file = activeFileList[start + i];
            var tags = getTagsForFile(file);
            var hasDimensions = dimensions.TryGetValue(file, out var size) &&
                                HasUsableDimensions(size);
            list.Add(new ImageViewItem
            {
                FilePath = file,
                FileName = System.IO.Path.GetFileName(file),
                Tags = tags,
                Width = hasDimensions ? size.Width : 1,
                Height = hasDimensions ? size.Height : 1,
                IsLoading = true
            });
        }

        return list;
    }

    private async Task LoadPageThumbnailsAsync(
        int pageIndex,
        CancellationToken ct = default,
        bool includeVideos = true,
        bool cacheOnlyVideos = false)
    {
        if (!_cache.TryGet(pageIndex, out var pageItems) || pageItems == null) return;

        var source = cacheOnlyVideos ? ThumbnailLoadSource.Preload : ThumbnailLoadSource.Page;
        var metrics = new ThumbnailBatchMetrics(source, pageIndex, _thumbnailDecodeWidth, GetActiveLoadSummary);
        var sw = Stopwatch.StartNew();
        try
        {
        var unloaded = pageItems.Where(i => !i.IsLoaded || i.IsRefreshingThumbnail)
            .Take(cacheOnlyVideos && UseViewportLoading ? Math.Min(24, EstimateVisibleItemCount(_currentUiState)) : int.MaxValue).ToList();
        if (unloaded.Count == 0) return;
        var imageItems = unloaded.Where(i => !FileTypeConstants.IsVideoFile(i.FilePath)).ToList();
        var videoItems = includeVideos
            ? unloaded.Where(i => FileTypeConstants.IsVideoFile(i.FilePath)).ToList()
            : new List<ImageViewItem>();
        var skippedVideos = includeVideos ? 0 : unloaded.Count - imageItems.Count;

        ThreadPool.GetAvailableThreads(out var w, out var io);
        ThreadPool.GetMaxThreads(out var mw, out var mio);
        var pressure = MemoryPressureMonitor.Current;
        PerfLogger.Log($"[PageMgr] LoadThumbnails unloaded={unloaded.Count} images={imageItems.Count} videos={videoItems.Count} skippedVideos={skippedVideos} ThreadPool={mw-w}/{mw}");
        AppLogger.Memory($"Page.Thumb.Start page={pageIndex} unloaded={unloaded.Count} images={imageItems.Count} videos={videoItems.Count} skippedVideos={skippedVideos} pressure={pressure} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1}");

        const int batchSize = 16;
        for (int batchStart = 0; batchStart < imageItems.Count; batchStart += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = imageItems.Skip(batchStart).Take(batchSize).ToList();
            var parallelism = Math.Min(RecommendedThumbnailParallelism(), MaxNonViewportThumbnailLoads);
            for (int i = 0; i < batch.Count; i += parallelism)
            {
                ct.ThrowIfCancellationRequested();
                var slice = batch.Skip(i).Take(parallelism).ToList();
                await Task.WhenAll(slice.Select(item => LoadSingleThumbnailAsync(item, ct, source, metrics)));
                PostLoadedItems(slice, metrics);
            }
            _thumbCache.TrimForPressure();

            // Only dispatch if we're still the active page load
            if (ct.IsCancellationRequested)
            {
                metrics.RequestLog("cancel", sw.ElapsedMilliseconds);
                AppLogger.Memory($"Page.Thumb.Cancel page={pageIndex} elapsedMs={sw.ElapsedMilliseconds}");
                return;
            }
        }
        foreach (var item in videoItems)
        {
            ct.ThrowIfCancellationRequested();
            if (cacheOnlyVideos)
                await LoadSingleThumbnailCacheOnlyAsync(item, ct, source, metrics);
            else
                await LoadSingleThumbnailAsync(item, ct, source, metrics);
            PostLoadedItems(new[] { item }, metrics);
            _thumbCache.TrimForPressure();
        }

        metrics.RequestLog("end", sw.ElapsedMilliseconds);
        AppLogger.Memory($"Page.Thumb.End page={pageIndex} loaded={unloaded.Count(i => i.IsLoaded)}/{unloaded.Count} videosLoaded={videoItems.Count(i => i.IsLoaded)}/{videoItems.Count} skippedVideos={skippedVideos} pressure={MemoryPressureMonitor.Current} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1} elapsedMs={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.RequestLog("cancel", sw.ElapsedMilliseconds);
            AppLogger.Memory($"Page.Thumb.Cancel page={pageIndex} elapsedMs={sw.ElapsedMilliseconds}");
            throw;
        }
    }

    private void PostLoadedItems(IEnumerable<ImageViewItem> items, ThumbnailBatchMetrics? metrics = null)
    {
        var loadedItems = items.Where(i => i.IsLoaded).ToList();
        if (loadedItems.Count == 0) return;

        metrics?.RegisterUiPost();
        var queuedAt = Stopwatch.GetTimestamp();
        _dispatcher.Post(() =>
        {
            metrics?.CompleteUiPost(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
            foreach (var loadedItem in loadedItems)
            {
                loadedItem.IsLoading = false;
                loadedItem.NotifyAll();
            }
        });
    }

    private static int RecommendedThumbnailParallelism()
    {
        return MemoryPressureMonitor.Current switch
        {
            MemoryPressureMonitor.PressureLevel.Critical => 1,
            MemoryPressureMonitor.PressureLevel.High => 2,
            MemoryPressureMonitor.PressureLevel.Medium => 3,
            _ => 6
        };
    }

    private void QueueViewportThumbnail(ImageViewItem item, CancellationToken ct) =>
        QueueThumbnail(item, ct, ThumbnailLoadSource.Viewport);

    private void QueueThumbnail(
        ImageViewItem item,
        CancellationToken ct,
        ThumbnailLoadSource source)
    {
        var requestKey = (item, _thumbnailDecodeWidth, ct);
        lock (_queuedRequests)
            if (!_queuedRequests.Add(requestKey)) return;
        ThumbnailBatchMetrics? metrics = null;
        if (source == ThumbnailLoadSource.Viewport)
        {
            lock (_viewportMetricsLock)
            {
                if (_viewportMetrics == null)
                {
                    _viewportMetrics = new ThumbnailBatchMetrics(
                        ThumbnailLoadSource.Viewport, _activePageIndex, _thumbnailDecodeWidth, GetActiveLoadSummary);
                    _ = FlushViewportMetricsAsync(_viewportMetrics);
                }

                metrics = _viewportMetrics;
                metrics.RecordRequested();
            }
        }

        _ = LoadQueuedThumbnailAsync(item, ct, source, metrics, requestKey);
    }

    private async Task LoadQueuedThumbnailAsync(
        ImageViewItem item,
        CancellationToken ct,
        ThumbnailLoadSource source,
        ThumbnailBatchMetrics? metrics,
        (ImageViewItem Item, int Width, CancellationToken Token) requestKey)
    {
        try
        {
            await LoadSingleThumbnailAsync(
                item, ct, source, metrics, requestAlreadyRecorded: metrics is not null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Viewport work is deliberately fire-and-forget. Cancellation while
            // waiting for a decode slot is expected when the viewport moves.
        }
        finally { lock (_queuedRequests) _queuedRequests.Remove(requestKey); }
    }

    private async Task FlushViewportMetricsAsync(ThumbnailBatchMetrics metrics)
    {
        await Task.Delay(250).ConfigureAwait(false);
        lock (_viewportMetricsLock)
        {
            if (ReferenceEquals(_viewportMetrics, metrics))
            {
                _viewportMetrics = null;
                metrics.SealRequests();
            }
        }

        await metrics.WaitForRequestsAsync().ConfigureAwait(false);
        PostLoadedItems(metrics.TakeLoadedItems(), metrics);
        metrics.RequestLog("end", metrics.ElapsedMilliseconds);
    }

    private string GetActiveLoadSummary() => _scheduler.GetActiveLoadSummary();

    private async Task LoadSingleThumbnailAsync(
        ImageViewItem item,
        CancellationToken ct = default,
        ThumbnailLoadSource source = ThumbnailLoadSource.Page,
        ThumbnailBatchMetrics? metrics = null,
        bool requestAlreadyRecorded = false)
    {
        var width = _thumbnailDecodeWidth;
        var epoch = Interlocked.Read(ref _thumbnailRequestEpoch);
        var state = _itemRequests.GetOrCreateValue(item);
        var version = Interlocked.Increment(ref state.Version);
        var path = item.FilePath;
        var queuedAt = Stopwatch.GetTimestamp();
        if (!requestAlreadyRecorded) metrics?.RecordRequested();
        bool IsCurrent() => !_disposed && !ct.IsCancellationRequested &&
            epoch == Interlocked.Read(ref _thumbnailRequestEpoch) &&
            version == Interlocked.Read(ref state.Version) &&
            width == _thumbnailDecodeWidth && item.FilePath == path;

        var slot = await _scheduler.AcquireSlotAsync(source, FileTypeConstants.IsVideoFile(path), metrics, ct).ConfigureAwait(false);
        try
        {
            ThumbnailCacheLoadResult result;
            // UI handoff must not occupy an I/O slot; otherwise a busy frame
            // stops all six reads and prevents lookahead from getting ahead.
            using (slot)
            {
                ct.ThrowIfCancellationRequested();
                result = await Task.Run(() =>
                    _thumbCache.GetOrCreateThumbnailWithDiagnosticsAsync(path, width, ct), ct).ConfigureAwait(false);
            }
                if (!IsCurrent()) { metrics?.RecordCanceled(); return; }
                var published = await PublishThumbnailAsync(() =>
                {
                    if (!IsCurrent()) return false;
                    if (result.Data != null)
                    {
                        SetDecodedDimensionsIfUnknown(item, result.Width, result.Height);
                        item.ThumbnailData = result.Data;
                        item.HasNoPreview = false;
                        item.IsLoaded = true;
                    }
                    else if (result.IsFinalWithoutThumbnail || FileTypeConstants.IsMusicFile(path))
                    {
                        item.ThumbnailData = null;
                        item.HasNoPreview = true;
                        item.IsLoaded = true;
                    }
                    // Failed refresh keeps the previous image.
                    item.IsRefreshingThumbnail = false;
                    item.IsLoading = false;
                    item.NotifyAll();
                    return true;
                }, metrics).ConfigureAwait(false);
                if (published) metrics?.RecordResult(result, Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                else metrics?.RecordCanceled();
        }
        catch (OperationCanceledException) { metrics?.RecordCanceled(); }
        catch
        {
            metrics?.RecordFailed();
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                item.IsRefreshingThumbnail = false;
                item.IsLoading = false;
                item.NotifyAll();
            }).ConfigureAwait(false);
        }
    }

    private async Task LoadSingleThumbnailCacheOnlyAsync(
        ImageViewItem item,
        CancellationToken ct = default,
        ThumbnailLoadSource source = ThumbnailLoadSource.Page,
        ThumbnailBatchMetrics? metrics = null)
    {
        var width = _thumbnailDecodeWidth;
        var epoch = Interlocked.Read(ref _thumbnailRequestEpoch);
        var state = _itemRequests.GetOrCreateValue(item);
        var version = Interlocked.Increment(ref state.Version);
        var path = item.FilePath;
        metrics?.RecordRequested();
        var queuedAt = Stopwatch.GetTimestamp();
        bool IsCurrent() => !_disposed && !ct.IsCancellationRequested &&
            epoch == Interlocked.Read(ref _thumbnailRequestEpoch) &&
            version == Interlocked.Read(ref state.Version) &&
            width == _thumbnailDecodeWidth && item.FilePath == path;
        var slot = await _scheduler.AcquireSlotAsync(source, FileTypeConstants.IsVideoFile(path), metrics, ct).ConfigureAwait(false);
        try
        {
            (byte[]? data, int w, int h) cached;
            using (slot)
            {
                cached = await Task.Run(() => _thumbCache.TryGetCachedThumbnail(path, width), ct).ConfigureAwait(false);
            }
                var (data, w, h) = cached;
                if (!IsCurrent()) { metrics?.RecordCanceled(); return; }
                var published = await PublishThumbnailAsync(() =>
                {
                    if (!IsCurrent()) return false;
                    if (data != null)
                    {
                        SetDecodedDimensionsIfUnknown(item, w, h);
                        item.ThumbnailData = data;
                        item.HasNoPreview = false;
                        item.IsRefreshingThumbnail = false;
                        item.IsLoaded = true;
                    }
                    item.IsLoading = false;
                    item.NotifyAll();
                    return true;
                }, metrics).ConfigureAwait(false);
                if (!published) metrics?.RecordCanceled();
                else if (data != null) metrics?.RecordCacheOnlyHit(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                else metrics?.RecordFailed();
        }
        catch (OperationCanceledException) { metrics?.RecordCanceled(); }
        catch { metrics?.RecordFailed(); }
    }

    private async Task<bool> PublishThumbnailAsync(Func<bool> publish, ThumbnailBatchMetrics? metrics)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        metrics?.RegisterUiPost();
        try
        {
            return await _dispatcher.InvokeAsync(() =>
            {
                metrics?.CompleteUiPost(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                metrics = null;
                return publish();
            }).ConfigureAwait(false);
        }
        finally
        {
            // Also balance diagnostics if a dispatcher rejects work during shutdown.
            metrics?.CompleteUiPost(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
        }
    }

    private static void SetDecodedDimensionsIfUnknown(ImageViewItem item, int width, int height)
    {
        if (item.Width > 1 && item.Height > 1)
            return;

        item.Width = width > 0 ? width : 1920;
        item.Height = height > 0 ? height : 1080;
    }

    private static bool HasUsableDimensions((int Width, int Height) size) =>
        size.Width > 1 && size.Height > 1;

    private void PreloadAdjacentPages(
        int currentPage, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile,
        CancellationToken parentCt)
    {
        if (parentCt.IsCancellationRequested) return;
        if (MemoryPressureMonitor.Current != MemoryPressureMonitor.PressureLevel.Low)
        {
            AppLogger.Memory($"Page.Preload.Skip page={currentPage} reason=pressure level={MemoryPressureMonitor.Current}");
            return;
        }

        var request = new PreloadRequest(
            currentPage,
            totalPages,
            activeFileList.ToList(),
            getTagsForFile,
            parentCt);

        lock (_preloadStateLock)
        {
            // The page load can finish after a newer page has already canceled its token.
            // Do not let that stale continuation replace the latest deferred preload request.
            if (_disposed || parentCt.IsCancellationRequested) return;
            _lastPreloadRequest = request;
            if (_scrollActive) return;
            StartPreloadLocked(request);
        }
    }

    private void StartPreloadLocked(PreloadRequest request)
    {
        CancelPreloadLocked(clearLastRequest: false);
        var cts = new CancellationTokenSource();
        _preloadCts = cts;
        var ct = cts.Token;
        var generation = ++_preloadGeneration;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                if (!IsCurrentPreload(generation, ct, request)) return;
                if (MemoryPressureMonitor.Current != MemoryPressureMonitor.PressureLevel.Low)
                {
                    AppLogger.Memory($"Page.Preload.Skip page={request.CurrentPage} reason=delayed-pressure level={MemoryPressureMonitor.Current}");
                    return;
                }

                int? preloadPrev = null, preloadNext = null;
                var preloadPrevious = PageSize <= 200;
                var preloadFollowing = PageSize <= 400;
                if (preloadPrevious && request.CurrentPage - 1 >= 0 &&
                    await EnsurePreloadPageAsync(
                        request.CurrentPage - 1, request.TotalPages,
                        request.ActiveFileList, request.GetTagsForFile, ct).ConfigureAwait(false))
                {
                    preloadPrev = request.CurrentPage - 1;
                }
                if (!IsCurrentPreload(generation, ct, request)) return;

                if (preloadFollowing && request.CurrentPage + 1 < request.TotalPages &&
                    await EnsurePreloadPageAsync(
                        request.CurrentPage + 1, request.TotalPages,
                        request.ActiveFileList, request.GetTagsForFile, ct).ConfigureAwait(false))
                {
                    preloadNext = request.CurrentPage + 1;
                }
                if (!IsCurrentPreload(generation, ct, request)) return;
                AppLogger.Memory($"Page.Preload.Start page={request.CurrentPage} prev={preloadPrev?.ToString() ?? "-"} next={preloadNext?.ToString() ?? "-"} cached={CachedPageCount}");
                if (preloadPrev.HasValue)
                    _ = LoadPageThumbnailsAsync(preloadPrev.Value, ct, includeVideos: true, cacheOnlyVideos: true);
                if (preloadNext.HasValue)
                    _ = LoadPageThumbnailsAsync(preloadNext.Value, ct, includeVideos: true, cacheOnlyVideos: true);
            }
            catch (OperationCanceledException)
            {
                // Expected when scrolling starts or the current page changes.
            }
        }, ct);
    }

    private async Task<bool> EnsurePreloadPageAsync(
        int pageIndex,
        int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile,
        CancellationToken ct)
    {
        var pageItems = await GetOrCreatePageItemsAsync(
            pageIndex, totalPages, activeFileList, getTagsForFile, ct).ConfigureAwait(false);
        return pageItems?.Any(item => !item.IsLoaded || item.IsRefreshingThumbnail) == true;
    }

    private bool IsCurrentPreload(long generation, CancellationToken token, PreloadRequest request)
    {
        lock (_preloadStateLock)
            return IsCurrentPreloadLocked(generation, token, request);
    }

    private bool IsCurrentPreloadLocked(long generation, CancellationToken token, PreloadRequest request)
    {
        return !_disposed && !_scrollActive &&
               generation == _preloadGeneration &&
               ReferenceEquals(_lastPreloadRequest, request) &&
               _preloadCts?.Token == token &&
               !token.IsCancellationRequested &&
               !request.ParentToken.IsCancellationRequested;
    }

    private void CancelPreloadLocked(bool clearLastRequest)
    {
        ++_preloadGeneration;
        var cts = _preloadCts;
        _preloadCts = null;
        if (clearLastRequest)
            _lastPreloadRequest = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private static (int Width, int Height) ParseJpegDimensions(byte[] jpeg)
    {
        int i = 2; // skip SOI marker (0xFF 0xD8)
        while (i < jpeg.Length - 9)
        {
            if (jpeg[i] != 0xFF) return (0, 0);
            byte m = jpeg[i + 1];
            if (m == 0xC0 || m == 0xC2) // SOF0 or SOF2 (progressive)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        return (0, 0);
    }

    private void TrimPageCache(int currentPage, int totalPages)
    {
        var maxCachedPages = RecommendedCachedPages();
        int evictedPages = _cache.Trim(currentPage, totalPages, maxCachedPages, ResetThumbnailState);
        if (evictedPages > 0)
            AppLogger.Memory($"Page.Trim current={currentPage} max={maxCachedPages} evictedPages={evictedPages} cached={_cache.Count} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1}");
    }

    private int RecommendedCachedPages()
    {
        var normalPageCount = PageSize switch
        {
            <= 200 => 3,
            <= 400 => 2,
            _ => 1
        };

        return MemoryPressureMonitor.Current switch
        {
            MemoryPressureMonitor.PressureLevel.Critical => 1,
            MemoryPressureMonitor.PressureLevel.High => 1,
            MemoryPressureMonitor.PressureLevel.Medium => 2,
            _ => Math.Min(MaxCachedPages, normalPageCount)
        };
    }
}
