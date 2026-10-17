using System.Diagnostics;
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
        IImageMetaRepository? metaRepo = null)
    {
        _thumbCache = thumbCache;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
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
        CancelPageLoad();
        _pageLoadCts = new CancellationTokenSource();
        var loadCt = _pageLoadCts.Token;

        _activePageIndex = pageIndex;

        var pageItems = await GetOrCreatePageItemsAsync(
            pageIndex, totalPages, activeFileList, getTagsForFile, loadCt);
        if (pageItems == null || loadCt.IsCancellationRequested)
            return;

        bool needsLoad = !pageItems.TrueForAll(i => i.IsLoaded && !i.IsRefreshingThumbnail);

        if (needsLoad)
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
        _ = Task.Run(() => TrimPageCache(pageIndex, totalPages));

        // LOH compaction check after page render (lightweight — Compaction runs on thread pool)
        var pressure = MemoryPressureMonitor.Current;
        if (pressure >= MemoryPressureMonitor.PressureLevel.High)
            MemoryPressureMonitor.CompactLoh();

        await Task.CompletedTask;
    }

    private int CachedPageCount => _cache.Count;

    private void CancelPageLoad()
    {
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

    public void CancelCurrentLoads()
    {
        _zoomDebounceCts?.Cancel();
        CancelPageLoad();
    }

    public void LoadThumbnailsForItems(List<ImageViewItem> items)
    {
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
        var toLoad = items.Where(i => !i.IsLoaded || i.IsRefreshingThumbnail).ToList();
        foreach (var item in toLoad)
            await LoadSingleThumbnailAsync(item, default, source: ThumbnailLoadSource.Viewport);
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

        _thumbnailDecodeWidth = newDecodeWidth;
        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;
        await _thumbCache.ClearAsync();

        foreach (var item in _cache.GetAllItems())
        {
            item.IsLoaded = false;
            item.IsLoading = true;
            item.ThumbnailData = null;
            item.NotifyAll();
        }

        _ = LoadPageThumbnailsAsync(_activePageIndex);
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
                        if (!handledByVirtualizedDisplay)
                            _ = LoadPageThumbnailsAsync(_activePageIndex);
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

    private static void ResetThumbnailState(ImageViewItem item)
    {
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
        var unloaded = pageItems.Where(i => !i.IsLoaded || i.IsRefreshingThumbnail).ToList();
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

    private static void PostLoadedItems(IEnumerable<ImageViewItem> items, ThumbnailBatchMetrics? metrics = null)
    {
        var loadedItems = items.Where(i => i.IsLoaded).ToList();
        if (loadedItems.Count == 0) return;

        metrics?.RegisterUiPost();
        var queuedAt = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() =>
        {
            metrics?.CompleteUiPost(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
            foreach (var loadedItem in loadedItems)
            {
                loadedItem.IsLoading = false;
                loadedItem.NotifyAll();
            }
        }, DispatcherPriority.Normal);
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

        _ = LoadQueuedThumbnailAsync(item, ct, source, metrics);
    }

    private async Task LoadQueuedThumbnailAsync(
        ImageViewItem item,
        CancellationToken ct,
        ThumbnailLoadSource source,
        ThumbnailBatchMetrics? metrics)
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
        bool isVideo = FileTypeConstants.IsVideoFile(item.FilePath);
        if (isVideo) PerfLogger.Log($"[Thumb] VIDEO start {Path.GetFileName(item.FilePath)}");
        var sw = isVideo ? Stopwatch.StartNew() : null;

        var queuedAt = Stopwatch.GetTimestamp();
        if (!requestAlreadyRecorded)
            metrics?.RecordRequested();

        using (await _scheduler.AcquireSlotAsync(source, isVideo, metrics, ct).ConfigureAwait(false))
        {
            try
            {
                var result = await Task.Run(() =>
                    _thumbCache.GetOrCreateThumbnailWithDiagnosticsAsync(item.FilePath, _thumbnailDecodeWidth, ct), ct
                ).ConfigureAwait(false);
                if (result.Data != null)
                {
                    item.ThumbnailData = result.Data;
                    item.HasNoPreview = false;
                    item.IsRefreshingThumbnail = false;
                    SetDecodedDimensionsIfUnknown(item, result.Width, result.Height);
                    item.IsLoaded = true;
                    metrics?.RecordLoadedItem(item);
                }
                else if (result.IsFinalWithoutThumbnail)
                {
                    item.ThumbnailData = null;
                    item.HasNoPreview = true;
                    item.IsRefreshingThumbnail = false;
                    item.IsLoaded = true;
                    metrics?.RecordLoadedItem(item);
                }
                else if (FileTypeConstants.IsMusicFile(item.FilePath))
                {
                    // An unreadable embedded cover is not negatively cached, but must still
                    // render as a music placeholder instead of an empty thumbnail.
                    item.ThumbnailData = null;
                    item.HasNoPreview = true;
                    item.IsRefreshingThumbnail = false;
                    item.IsLoaded = true;
                    metrics?.RecordLoadedItem(item);
                }
                else if (item.IsRefreshingThumbnail)
                {
                    item.IsRefreshingThumbnail = false;
                }
                metrics?.RecordResult(result, Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                metrics?.RecordCanceled();
                // Page changed — discard silently
            }
            catch
            {
                metrics?.RecordFailed();
                item.IsRefreshingThumbnail = false;
                if (isVideo) PerfLogger.Log($"[Thumb] VIDEO FAIL {Path.GetFileName(item.FilePath)}");
            }
        }

        if (!item.IsLoaded && ct.IsCancellationRequested)
            return;

        if (!item.IsLoaded)
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.IsLoading = false;
                item.NotifyAll();
            }, DispatcherPriority.Normal);
        }

        if (isVideo) PerfLogger.Log($"[Thumb] VIDEO done {Path.GetFileName(item.FilePath)} elapsed={sw!.ElapsedMilliseconds}ms");
    }

    private async Task LoadSingleThumbnailCacheOnlyAsync(
        ImageViewItem item,
        CancellationToken ct = default,
        ThumbnailLoadSource source = ThumbnailLoadSource.Page,
        ThumbnailBatchMetrics? metrics = null)
    {
        bool isVideo = FileTypeConstants.IsVideoFile(item.FilePath);
        metrics?.RecordRequested();
        var queuedAt = Stopwatch.GetTimestamp();

        using (await _scheduler.AcquireSlotAsync(source, isVideo, metrics, ct).ConfigureAwait(false))
        {
            try
            {
                var (data, w, h) = await Task.Run(() =>
                    _thumbCache.TryGetCachedThumbnail(item.FilePath, _thumbnailDecodeWidth), ct
                ).ConfigureAwait(false);

                if (data != null)
                {
                    item.ThumbnailData = data;
                    SetDecodedDimensionsIfUnknown(item, w, h);
                    item.IsLoaded = true;
                    metrics?.RecordCacheOnlyHit(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                }
                else
                {
                    item.IsLoading = false;
                    metrics?.RecordFailed();
                }
            }
            catch (OperationCanceledException) { metrics?.RecordCanceled(); }
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
