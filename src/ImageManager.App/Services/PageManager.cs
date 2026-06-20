using System.Diagnostics;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Helpers;
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
    public const int PageSize = 200;
    private const int MaxCachedPages = 3;
    private static readonly double[] ZoomLevels = { 160, 183, 213, 256, 284, 320, 366, 427, 512, 640 };

    private readonly ThumbnailCacheService _thumbCache;
    private readonly IFolderRepository _folderRepo;

    private readonly Dictionary<int, List<ImageViewItem>> _pageCache = new();
    private readonly object _pageCacheLock = new();
    private int _activePageIndex;

    private List<ImageViewItem>? _preSearchPageItems;
    private int _preSearchPageIndex;

    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(6);
    private readonly SemaphoreSlim _videoLoadSemaphore = new(2);
    private int _thumbnailDecodeWidth = 200;
    private int _currentZoomLevel;
    private PageUiState _currentUiState;
    private long _zoomVersion = 0;  // 版本号机制，防止防抖竞态
    private CancellationTokenSource? _zoomDebounceCts;
    private CancellationTokenSource? _pageLoadCts;
    private CancellationTokenSource? _preloadCts;

    public event Action<PageChangedEventArgs>? PageChanged;

    public PageManager(ThumbnailCacheService thumbCache, IFolderRepository folderRepo)
    {
        _thumbCache = thumbCache;
        _folderRepo = folderRepo;
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

        List<ImageViewItem> pageItems;
        bool needsLoad;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!))
            {
                pageItems = CreatePlaceholderItems(pageIndex, totalPages, activeFileList, getTagsForFile);
                _pageCache[pageIndex] = pageItems;
                PerfLogger.Log($"[PageMgr] CreatePlaceholders {pageItems.Count} items elapsed={sw.ElapsedMilliseconds}ms");
            }
            needsLoad = !pageItems.TrueForAll(i => i.IsLoaded);
        }

        if (needsLoad)
        {
            PerfLogger.Log($"[PageMgr] LoadThumbnails START unloaded={pageItems.Count(i => !i.IsLoaded)}");
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

    private int CachedPageCount
    {
        get { lock (_pageCacheLock) return _pageCache.Count; }
    }

    private void CancelPageLoad()
    {
        if (_pageLoadCts != null)
        {
            _pageLoadCts.Cancel();
            _pageLoadCts.Dispose();
            _pageLoadCts = null;
        }
        if (_preloadCts != null)
        {
            _preloadCts.Cancel();
            _preloadCts.Dispose();
            _preloadCts = null;
        }
    }

    public void CancelCurrentLoads()
    {
        _zoomDebounceCts?.Cancel();
        CancelPageLoad();
    }

    public void LoadThumbnailsForItems(List<ImageViewItem> items)
    {
        var toLoad = items.Where(i => !i.IsLoaded).ToList();
        // Use current page's CancellationToken so page flip instantly cancels scroll-triggered loads
        var ct = _pageLoadCts?.Token ?? default;
        foreach (var item in toLoad)
            _ = LoadSingleThumbnailAsync(item, ct);
    }

    public async Task LoadThumbnailsForItemsAsync(List<ImageViewItem> items)
    {
        var toLoad = items.Where(i => !i.IsLoaded).ToList();
        foreach (var item in toLoad)
            await LoadSingleThumbnailAsync(item);
    }

    public async Task RegenerateThumbnailsAsync(List<ImageViewItem> items)
    {
        foreach (var item in items.DistinctBy(i => i.FilePath))
        {
            _thumbCache.InvalidateThumbnail(item.FilePath);
            item.ThumbnailData = null;
            item.IsLoaded = false;
            item.IsLoading = true;
            item.NotifyAll();
        }

        foreach (var item in items.DistinctBy(i => i.FilePath))
        {
            await LoadSingleThumbnailAsync(item);
            PostLoadedItems(new[] { item });
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
        int w = (int)(ZoomLevels[_currentZoomLevel] * 2);
        return Math.Clamp(w, 300, 1600);
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
                        // Mark cached items as unloaded — reloads in-place without destroying page
                        lock (_pageCacheLock)
                        {
                            foreach (var kv in _pageCache)
                                foreach (var item in kv.Value)
                                {
                                    item.IsLoaded = false;
                                    item.IsLoading = true;
                                }
                        }
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
        _thumbnailLoadSemaphore.Dispose();
        _videoLoadSemaphore.Dispose();
        _zoomDebounceCts?.Dispose();
        CancelPageLoad();
    }

    public void InvalidateCache()
    {
        _currentUiState = default;
        lock (_pageCacheLock)
        {
            foreach (var page in _pageCache.Values)
                foreach (var item in page)
                    item.ThumbnailData = null;
            _pageCache.Clear();
        }
    }

    public void InvalidateCacheExceptPage(int pageIndex, List<ImageViewItem> currentItems)
    {
        lock (_pageCacheLock)
        {
            foreach (var key in _pageCache.Keys.ToList())
            {
                if (key == pageIndex) continue;
                if (_pageCache.TryGetValue(key, out var page))
                    foreach (var item in page)
                        item.ThumbnailData = null;
                _pageCache.Remove(key);
            }

            _pageCache[pageIndex] = currentItems;
        }
    }

    public void UpdateUiState(PageUiState state) => _currentUiState = state;

    public void RemoveFromCache(int pageIndex)
    {
        lock (_pageCacheLock) { _pageCache.Remove(pageIndex); }
    }

    public void SetPageCache(int pageIndex, List<ImageViewItem> items)
    {
        lock (_pageCacheLock) { _pageCache[pageIndex] = items; }
    }

    public void SavePreSearchState(IReadOnlyList<ImageViewItem> currentItems, int pageIndex)
    {
        _preSearchPageItems = currentItems.ToList();
        _preSearchPageIndex = pageIndex;
    }

    public bool TryRestorePreSearchState(out List<ImageViewItem>? items, out int pageIndex)
    {
        if (_preSearchPageItems is { Count: > 0 })
        {
            lock (_pageCacheLock)
            {
                _pageCache.Clear();
                _pageCache[_preSearchPageIndex] = _preSearchPageItems;
            }
            items = _preSearchPageItems;
            pageIndex = _preSearchPageIndex;
            _preSearchPageItems = null;
            return true;
        }
        items = null;
        pageIndex = 0;
        return false;
    }

    // ==================== Private Methods ====================

    private List<ImageViewItem> CreatePlaceholderItems(
        int pageIndex, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile)
    {
        int start = pageIndex * PageSize;
        int count = Math.Min(PageSize, activeFileList.Count - start);
        var list = new List<ImageViewItem>();

        for (int i = 0; i < count; i++)
        {
            var file = activeFileList[start + i];
            var tags = getTagsForFile(file);
            list.Add(new ImageViewItem
            {
                FilePath = file,
                FileName = System.IO.Path.GetFileName(file),
                Tags = tags,
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
        List<ImageViewItem> pageItems;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!)) return;
        }

        var unloaded = pageItems.Where(i => !i.IsLoaded).ToList();
        if (unloaded.Count == 0) return;
        var imageItems = unloaded.Where(i => !FileTypeConstants.IsVideoFile(i.FilePath)).ToList();
        var videoItems = includeVideos
            ? unloaded.Where(i => FileTypeConstants.IsVideoFile(i.FilePath)).ToList()
            : new List<ImageViewItem>();
        var skippedVideos = includeVideos ? 0 : unloaded.Count - imageItems.Count;

        ThreadPool.GetAvailableThreads(out var w, out var io);
        ThreadPool.GetMaxThreads(out var mw, out var mio);
        var sw = Stopwatch.StartNew();
        var pressure = MemoryPressureMonitor.Current;
        PerfLogger.Log($"[PageMgr] LoadThumbnails unloaded={unloaded.Count} images={imageItems.Count} videos={videoItems.Count} skippedVideos={skippedVideos} ThreadPool={mw-w}/{mw}");
        AppLogger.Memory($"Page.Thumb.Start page={pageIndex} unloaded={unloaded.Count} images={imageItems.Count} videos={videoItems.Count} skippedVideos={skippedVideos} pressure={pressure} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1}");

        const int batchSize = 16;
        for (int batchStart = 0; batchStart < imageItems.Count; batchStart += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = imageItems.Skip(batchStart).Take(batchSize).ToList();
            var parallelism = RecommendedThumbnailParallelism();
            for (int i = 0; i < batch.Count; i += parallelism)
            {
                ct.ThrowIfCancellationRequested();
                var slice = batch.Skip(i).Take(parallelism).ToList();
                await Task.WhenAll(slice.Select(item => LoadSingleThumbnailAsync(item, ct)));
                PostLoadedItems(slice);
            }
            _thumbCache.TrimForPressure();

            // Only dispatch if we're still the active page load
            if (ct.IsCancellationRequested)
            {
                AppLogger.Memory($"Page.Thumb.Cancel page={pageIndex} elapsedMs={sw.ElapsedMilliseconds}");
                return;
            }
        }
        foreach (var item in videoItems)
        {
            ct.ThrowIfCancellationRequested();
            if (cacheOnlyVideos)
                await LoadSingleThumbnailCacheOnlyAsync(item, ct);
            else
                await LoadSingleThumbnailAsync(item, ct);
            PostLoadedItems(new[] { item });
            _thumbCache.TrimForPressure();
        }

        AppLogger.Memory($"Page.Thumb.End page={pageIndex} loaded={unloaded.Count(i => i.IsLoaded)}/{unloaded.Count} videosLoaded={videoItems.Count(i => i.IsLoaded)}/{videoItems.Count} skippedVideos={skippedVideos} pressure={MemoryPressureMonitor.Current} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private static void PostLoadedItems(IEnumerable<ImageViewItem> items)
    {
        var loadedItems = items.Where(i => i.IsLoaded).ToList();
        if (loadedItems.Count == 0) return;

        Dispatcher.UIThread.Post(() =>
        {
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

    private async Task LoadSingleThumbnailAsync(ImageViewItem item, CancellationToken ct = default)
    {
        bool isVideo = FileTypeConstants.IsVideoFile(item.FilePath);
        if (isVideo) PerfLogger.Log($"[Thumb] VIDEO start {Path.GetFileName(item.FilePath)}");
        var sw = isVideo ? Stopwatch.StartNew() : null;

        var semaphore = isVideo ? _videoLoadSemaphore : _thumbnailLoadSemaphore;

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (data, w, h) = await Task.Run(() =>
                _thumbCache.GetOrCreateThumbnailAsync(item.FilePath, _thumbnailDecodeWidth, ct), ct
            ).ConfigureAwait(false);

            if (data != null)
            {
                item.ThumbnailData = data;
                item.Width = w > 0 ? w : 1920;
                item.Height = h > 0 ? h : 1080;
                item.IsLoaded = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Page changed — discard silently
        }
        catch { if (isVideo) PerfLogger.Log($"[Thumb] VIDEO FAIL {Path.GetFileName(item.FilePath)}"); }
        finally { semaphore.Release(); }

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

    private async Task LoadSingleThumbnailCacheOnlyAsync(ImageViewItem item, CancellationToken ct = default)
    {
        bool isVideo = FileTypeConstants.IsVideoFile(item.FilePath);
        var semaphore = isVideo ? _videoLoadSemaphore : _thumbnailLoadSemaphore;

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (data, w, h) = await Task.Run(() =>
                _thumbCache.TryGetCachedThumbnail(item.FilePath, _thumbnailDecodeWidth), ct
            ).ConfigureAwait(false);

            if (data != null)
            {
                item.ThumbnailData = data;
                item.Width = w > 0 ? w : 1920;
                item.Height = h > 0 ? h : 1080;
                item.IsLoaded = true;
            }
            else
            {
                item.IsLoading = false;
            }
        }
        catch (OperationCanceledException) { }
        finally { semaphore.Release(); }
    }

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

        // Cancel previous preload
        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = new CancellationTokenSource();
        var ct = _preloadCts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000, ct);
            if (ct.IsCancellationRequested) return;
            if (MemoryPressureMonitor.Current != MemoryPressureMonitor.PressureLevel.Low)
            {
                AppLogger.Memory($"Page.Preload.Skip page={currentPage} reason=delayed-pressure level={MemoryPressureMonitor.Current}");
                return;
            }

            int? preloadPrev = null, preloadNext = null;
            lock (_pageCacheLock)
            {
                if (currentPage - 1 >= 0 && !_pageCache.ContainsKey(currentPage - 1))
                {
                    _pageCache[currentPage - 1] = CreatePlaceholderItems(
                        currentPage - 1, totalPages, activeFileList, getTagsForFile);
                    preloadPrev = currentPage - 1;
                }
                if (currentPage + 1 < totalPages && !_pageCache.ContainsKey(currentPage + 1))
                {
                    _pageCache[currentPage + 1] = CreatePlaceholderItems(
                        currentPage + 1, totalPages, activeFileList, getTagsForFile);
                    preloadNext = currentPage + 1;
                }
            }
            if (ct.IsCancellationRequested) return;
            AppLogger.Memory($"Page.Preload.Start page={currentPage} prev={preloadPrev?.ToString() ?? "-"} next={preloadNext?.ToString() ?? "-"} cached={CachedPageCount}");
            if (preloadPrev.HasValue)
                _ = LoadPageThumbnailsAsync(preloadPrev.Value, ct, includeVideos: true, cacheOnlyVideos: true);
            if (preloadNext.HasValue)
                _ = LoadPageThumbnailsAsync(preloadNext.Value, ct, includeVideos: true, cacheOnlyVideos: true);
        }, ct);
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
        lock (_pageCacheLock)
        {
            var maxCachedPages = RecommendedCachedPages();
            if (_pageCache.Count <= maxCachedPages) return;

            var mustKeep = new HashSet<int> { currentPage };
            if (maxCachedPages >= 2 && currentPage - 1 >= 0) mustKeep.Add(currentPage - 1);
            if (maxCachedPages >= 3 && currentPage + 1 < totalPages) mustKeep.Add(currentPage + 1);

            int evictedPages = 0;
            int evictedItems = 0;
            foreach (var key in _pageCache.Keys.ToList())
            {
                if (mustKeep.Contains(key)) continue;
                if (_pageCache.TryGetValue(key, out var evicted))
                {
                    foreach (var item in evicted) item.ThumbnailData = null;
                    evictedItems += evicted.Count;
                }
                _pageCache.Remove(key);
                evictedPages++;
            }
            if (evictedPages > 0)
                AppLogger.Memory($"Page.Trim current={currentPage} max={maxCachedPages} evictedPages={evictedPages} evictedItems={evictedItems} cached={_pageCache.Count} thumbCacheMB={_thumbCache.EstimatedMemoryBytes / 1048576.0:F1}");
        }
    }

    private static int RecommendedCachedPages()
    {
        return MemoryPressureMonitor.Current switch
        {
            MemoryPressureMonitor.PressureLevel.Critical => 1,
            MemoryPressureMonitor.PressureLevel.High => 1,
            MemoryPressureMonitor.PressureLevel.Medium => 2,
            _ => MaxCachedPages
        };
    }
}
