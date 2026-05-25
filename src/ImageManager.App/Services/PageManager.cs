using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Imaging;

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

    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(4);
    private int _thumbnailDecodeWidth = 200;
    private int _currentZoomLevel;
    private PageUiState _currentUiState;
    private CancellationTokenSource? _zoomDebounceCts;

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

        _activePageIndex = pageIndex;

        List<ImageViewItem> pageItems;
        bool needsLoad;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!))
            {
                pageItems = CreatePlaceholderItems(pageIndex, totalPages, activeFileList, getTagsForFile);
                _pageCache[pageIndex] = pageItems;
            }
            needsLoad = !pageItems.TrueForAll(i => i.IsLoaded);
        }

        if (needsLoad)
            _ = LoadPageThumbnailsAsync(pageIndex);

        PageChanged?.Invoke(new PageChangedEventArgs(
            pageItems, pageIndex, totalPages,
            $"当前页: {pageIndex + 1}/{totalPages}  每页 {PageSize} 张"));

        if (!isSearchResult && !string.IsNullOrEmpty(currentFolder))
            _ = Task.Run(() => _folderRepo.SetLastPageIndexAsync(currentFolder!, pageIndex));
        PreloadAdjacentPages(pageIndex, totalPages, activeFileList, getTagsForFile);
        _ = Task.Run(() => TrimPageCache(pageIndex, totalPages));
    }

    public void LoadThumbnailsForItems(List<ImageViewItem> items)
    {
        var toLoad = items.Where(i => !i.IsLoaded).ToList();
        foreach (var item in toLoad)
            _ = LoadSingleThumbnailAsync(item);
    }

    public async Task LoadThumbnailsForItemsAsync(List<ImageViewItem> items)
    {
        var toLoad = items.Where(i => !i.IsLoaded).ToList();
        foreach (var item in toLoad)
            await LoadSingleThumbnailAsync(item);
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
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(300, token); }
                    catch { return; }
                    if (token.IsCancellationRequested) return;
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
        _zoomDebounceCts?.Dispose();
    }

    public void InvalidateCache()
    {
        _currentUiState = default;
        lock (_pageCacheLock) { _pageCache.Clear(); }
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

    private async Task LoadPageThumbnailsAsync(int pageIndex)
    {
        List<ImageViewItem> pageItems;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!)) return;
        }

        var unloaded = pageItems.Where(i => !i.IsLoaded).ToList();
        if (unloaded.Count == 0) return;

        int visibleCount = EstimateVisibleItemCount(_currentUiState);
        var priorityItems = unloaded.Take(visibleCount).ToList();
        var bgItems = unloaded.Skip(visibleCount).ToList();

        int i = 0;
        foreach (var item in priorityItems)
        {
            await LoadSingleThumbnailAsync(item);
            if (++i % 10 == 0)
                await Task.Yield();
        }

        i = 0;
        foreach (var item in bgItems)
        {
            await LoadSingleThumbnailAsync(item);
            if (++i % 10 == 0)
                await Task.Yield();
        }
    }

    private async Task LoadSingleThumbnailAsync(ImageViewItem item)
    {
        await _thumbnailLoadSemaphore.WaitAsync();
        try
        {
            var data = await _thumbCache.GetOrCreateThumbnailAsync(item.FilePath, _thumbnailDecodeWidth);
            if (data != null)
            {
                item.ThumbnailData = data;
                var (w, h) = ThumbnailGenerator.GetDimensions(item.FilePath);
                item.Width = w;
                item.Height = h;
                item.IsLoaded = true;
            }
        }
        catch { AppLogger.Warn($"Failed to load thumbnail: {item.FilePath}"); }
        finally { _thumbnailLoadSemaphore.Release(); }

        item.IsLoading = false;
        item.NotifyAll();
    }

    private void PreloadAdjacentPages(
        int currentPage, int totalPages,
        List<string> activeFileList,
        Func<string, List<string>> getTagsForFile)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);

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
            if (preloadPrev.HasValue)
                _ = LoadPageThumbnailsAsync(preloadPrev.Value);
            if (preloadNext.HasValue)
                _ = LoadPageThumbnailsAsync(preloadNext.Value);
        });
    }

    private void TrimPageCache(int currentPage, int totalPages)
    {
        lock (_pageCacheLock)
        {
            if (_pageCache.Count <= MaxCachedPages) return;

            var mustKeep = new HashSet<int> { currentPage };
            if (currentPage - 1 >= 0) mustKeep.Add(currentPage - 1);
            if (currentPage + 1 < totalPages) mustKeep.Add(currentPage + 1);

            foreach (var key in _pageCache.Keys.ToList())
            {
                if (mustKeep.Contains(key)) continue;
                if (_pageCache.TryGetValue(key, out var evicted))
                    foreach (var item in evicted) item.ThumbnailData = null;
                _pageCache.Remove(key);
            }
        }
    }
}
