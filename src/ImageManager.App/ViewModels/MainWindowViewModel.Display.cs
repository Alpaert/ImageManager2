using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Controls;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    public async Task<bool> PrepareContinuousDisplayGeometryAsync(
        double containerWidth,
        CancellationToken cancellationToken = default)
    {
        return await _dispatcher.InvokeAsync(() =>
            PrepareContinuousDisplayGeometryCoreAsync(containerWidth, cancellationToken));
    }

    /// <summary>
    /// Reflows an already published continuous result for a new viewport width.
    /// This intentionally reuses its frozen paths, sizes, and item source: a resize
    /// must not clear visible thumbnails, query SQLite, or invoke video metadata.
    /// </summary>
    public async Task<bool> ReflowContinuousDisplayGeometryAsync(
        double containerWidth,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ContinuousDisplayGeometrySnapshot;
        var source = ContinuousImageItemSource;
        if (snapshot is null || source is null || !double.IsFinite(containerWidth) || containerWidth <= 0)
            return false;

        var options = snapshot.GeometryIndex.Options with
        {
            ContainerWidth = containerWidth,
            ThumbnailBaseWidth = ThumbnailBaseWidth,
            GridAspectRatio = AppSettings.ThumbnailAspectRatio
        };
        var geometry = await Task.Run(
            () => ImageDisplayGeometryIndex.Build(snapshot.Sizes, options), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return await _dispatcher.InvokeAsync(() =>
        {
            // Result, mode, or full geometry may have changed while the CPU-only
            // reflow ran. Do not publish stale geometry into a different source.
            if (!ReferenceEquals(snapshot, ContinuousDisplayGeometrySnapshot) ||
                !ReferenceEquals(source, ContinuousImageItemSource))
            {
                return false;
            }

            ContinuousDisplayGeometryIndex = geometry;
            return true;
        });
    }

    // All coordinator state starts on the UI dispatcher. The expensive metadata and
    // geometry work below deliberately resumes away from it until publication.
    private async Task<bool> PrepareContinuousDisplayGeometryCoreAsync(
        double containerWidth,
        CancellationToken cancellationToken)
    {
        InvalidateContinuousDisplayGeometry();
        if (ActiveFileList.Count == 0 || !double.IsFinite(containerWidth) || containerWidth <= 0)
            return false;

        var requestVersion = _continuousDisplayGeometryVersion;
        var displayFilterVersion = _displayFilterVersion;
        var sourcePaths = ActiveFileList.ToArray();
        var sortDimensions = _fileSortSnapshot?.GetKnownDimensions(sourcePaths)
            ?? new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        var options = new ImageDisplayGeometryOptions(
            ToImageDisplayLayoutMode(WaterfallMode),
            containerWidth,
            ThumbnailBaseWidth,
            AppSettings.ThumbnailAspectRatio);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _continuousDisplayGeometryCts = cts;
        IsContinuousGeometryBuilding = true;
        var cachedDimensionRepairs = new ConcurrentDictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        var unresolvedVideoPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var geometryStopwatch = Stopwatch.StartNew();
            var snapshot = await _continuousDisplayGeometryBuilder.BuildAsync(
                sourcePaths,
                options,
                async (paths, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var dimensions = await Task.Run(async () =>
                    {
                        token.ThrowIfCancellationRequested();
                        var result = await _metaRepo.GetDimensionsByPathsAsync(paths).ConfigureAwait(false);
                        foreach (var path in paths.Where(path => result.TryGetValue(path, out var size) && !HasUsableDimensions(size)).ToArray())
                            result.Remove(path);

                        // Dimensions discovered by the background repair remain usable
                        // even when the database had no ImageMeta row to update.
                        foreach (var path in paths)
                        {
                            if ((!result.TryGetValue(path, out var existing) || !HasUsableDimensions(existing))
                                && _continuousResolvedDimensions.TryGetValue(path, out var size)
                                && HasUsableDimensions(size))
                            result[path] = size;
                        }

                        var missingPaths = paths
                            .Where(path => !result.TryGetValue(path, out var size) || !HasUsableDimensions(size))
                            .ToArray();
                        if (missingPaths.Length > 0)
                        {
                            var cachedDimensions = await _thumbCache
                                .GetCachedDimensionsAsync(missingPaths, token)
                                .ConfigureAwait(false);
                            foreach (var (path, size) in cachedDimensions.Where(pair => HasUsableDimensions(pair.Value)))
                            {
                                result[path] = size;
                                cachedDimensionRepairs[path] = size;
                            }

                            // Publish the geometry from database/cache data now. FFmpeg
                            // probing is deliberately deferred so a large folder is not
                            // blank while every uncached video is inspected.
                            foreach (var path in missingPaths)
                            {
                                if (!FileTypeConstants.IsVideoFile(path))
                                    continue;
                                if (result.TryGetValue(path, out var size) && HasUsableDimensions(size))
                                    continue;
                                unresolvedVideoPaths.TryAdd(path, 0);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        return result;
                    }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return dimensions;
                },
                cts.Token,
                sortDimensions).ConfigureAwait(false);

            if (cts.IsCancellationRequested
                || requestVersion != _continuousDisplayGeometryVersion
                || displayFilterVersion != _displayFilterVersion)
            {
                return false;
            }

            var published = await _dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested
                    || requestVersion != _continuousDisplayGeometryVersion
                    || displayFilterVersion != _displayFilterVersion)
                {
                    return false;
                }

                var previousSource = ContinuousImageItemSource;
                ContinuousDisplayGeometrySnapshot = snapshot;
                ContinuousDisplayGeometryIndex = snapshot.GeometryIndex;
                ContinuousImageItemSource = new ContinuousImageItemSource(
                    snapshot.Paths,
                    snapshot.Sizes,
                    GetTagsForFile);
                previousSource?.ClearCache();
                return true;
            });

            if (!published)
                return false;

            PerfLogger.Log(
                $"[SortPerf] continuous_geometry_ms={geometryStopwatch.ElapsedMilliseconds} count={snapshot.Paths.Count} known_dimensions={sortDimensions.Count}");

            if (cachedDimensionRepairs.Count > 0)
                _ = PersistCachedDimensionsAsync(cachedDimensionRepairs);
            if (unresolvedVideoPaths.Count > 0)
            {
                var repairPaths = unresolvedVideoPaths.Keys.ToArray();
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested
                        && requestVersion == _continuousDisplayGeometryVersion
                        && displayFilterVersion == _displayFilterVersion
                        && DisplayMode == ImageDisplayMode.Continuous)
                    {
                        StartContinuousVideoDimensionRepair(repairPaths, requestVersion, displayFilterVersion);
                    }
                });
            }
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Continuous display geometry build failed: {ex}");
            return false;
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_continuousDisplayGeometryCts, cts)
                    && requestVersion == _continuousDisplayGeometryVersion)
                {
                    _continuousDisplayGeometryCts = null;
                    IsContinuousGeometryBuilding = false;
                }
            });

            // The request that created the CTS is the only code path that disposes it.
            cts.Dispose();
        }
    }

    /// <summary>
    /// Cancels outstanding geometry work and drops the lightweight snapshot. This
    /// deliberately does not start a replacement build: the future virtualizing
    /// view owns when a new container width is available.
    /// </summary>
    private void InvalidateContinuousDisplayGeometry()
    {
        ++_continuousDisplayGeometryVersion;
        _continuousDisplayGeometryCts?.Cancel();
        _continuousDisplayGeometryCts = null;
        _continuousDimensionRepairCts?.Cancel();
        _continuousDimensionRepairCts = null;
        CancelContinuousViewportThumbnailLoad();
        IsContinuousGeometryBuilding = false;

        ContinuousImageItemSource?.ClearCache();
        ContinuousImageItemSource = null;
        ContinuousDisplayGeometrySnapshot = null;
        ContinuousDisplayGeometryIndex = null;
    }

    private void CancelContinuousGeometryWorkPreservingSource()
    {
        ++_continuousDisplayGeometryVersion;
        _continuousDisplayGeometryCts?.Cancel();
        _continuousDisplayGeometryCts = null;
        _continuousDimensionRepairCts?.Cancel();
        _continuousDimensionRepairCts = null;
        IsContinuousGeometryBuilding = false;
    }

    private async Task<bool> ApplyContinuousDeletionAsync(
        ContinuousImageItemSource source,
        ContinuousDisplayGeometrySnapshot snapshot,
        HashSet<string> deletedPaths)
    {
        var remainingPaths = snapshot.Paths
            .Where(path => !deletedPaths.Contains(path))
            .ToArray();
        var remainingSizes = snapshot.Paths
            .Select((path, index) => (path, index))
            .Where(entry => !deletedPaths.Contains(entry.path))
            .Select(entry => snapshot.Sizes[entry.index])
            .ToArray();

        if (snapshot.Paths.Count != source.Count || remainingPaths.Length != remainingSizes.Length)
            return false;

        var requestVersion = _continuousDisplayGeometryVersion;
        var options = snapshot.GeometryIndex.Options;
        var geometry = await Task.Run(
            () => ImageDisplayGeometryIndex.Build(remainingSizes, options));

        if (requestVersion != _continuousDisplayGeometryVersion ||
            !ReferenceEquals(snapshot, ContinuousDisplayGeometrySnapshot) ||
            !ReferenceEquals(source, ContinuousImageItemSource) ||
            DisplayMode != ImageDisplayMode.Continuous)
            return false;

        source.RemovePaths(deletedPaths);
        if (source.Count != remainingPaths.Length)
            return false;

        // Paths after the deletion shift left. A numerically unchanged viewport
        // range can therefore contain a newly visible item that the previous
        // range had not loaded; force the next panel range publication to queue
        // only those missing thumbnails without clearing retained image bytes.
        CancelContinuousViewportThumbnailLoad();

        var dimensionHitCount = remainingSizes.Count(size => size.Width > 1 && size.Height > 1);
        ContinuousDisplayGeometrySnapshot = new ContinuousDisplayGeometrySnapshot(
            remainingPaths, remainingSizes, geometry, dimensionHitCount);
        ContinuousDisplayGeometryIndex = geometry;
        return true;
    }

    private async Task PersistCachedDimensionsAsync(
        IReadOnlyDictionary<string, (int Width, int Height)> dimensions)
    {
        try
        {
            await _metaRepo.UpdateDimensionsByPathsAsync(dimensions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Cached dimension persistence failed: {ex.Message}");
        }
    }

    private void StartContinuousVideoDimensionRepair(
        IReadOnlyCollection<string> paths,
        long requestVersion,
        long displayFilterVersion)
    {
        _continuousDimensionRepairCts?.Cancel();
        var cts = new CancellationTokenSource();
        _continuousDimensionRepairCts = cts;
        _ = RepairContinuousVideoDimensionsAsync(paths, requestVersion, displayFilterVersion, cts);
    }

    private async Task RepairContinuousVideoDimensionsAsync(
        IReadOnlyCollection<string> paths,
        long requestVersion,
        long displayFilterVersion,
        CancellationTokenSource cts)
    {
        if (_videoService == null)
            return;

        try
        {
            var videoService = _videoService;
            var resolved = new ConcurrentDictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(paths,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token },
                async (path, token) =>
                {
                    var size = await videoService.TryGetDisplayDimensionsAsync(path, token)
                        .ConfigureAwait(false);
                    if (size is { Width: > 1, Height: > 1 })
                        resolved[path] = size.Value;
                }).ConfigureAwait(false);

            if (resolved.Count == 0 || cts.IsCancellationRequested)
                return;

            foreach (var (path, size) in resolved)
                _continuousResolvedDimensions[path] = size;
            await _metaRepo.UpdateDimensionsByPathsAsync(resolved).ConfigureAwait(false);
            if (cts.IsCancellationRequested ||
                requestVersion != _continuousDisplayGeometryVersion ||
                displayFilterVersion != _displayFilterVersion ||
                DisplayMode != ImageDisplayMode.Continuous)
            {
                return;
            }

            await _dispatcher.InvokeAsync(async () =>
            {
                if (cts.IsCancellationRequested ||
                    requestVersion != _continuousDisplayGeometryVersion ||
                    displayFilterVersion != _displayFilterVersion ||
                    DisplayMode != ImageDisplayMode.Continuous)
                {
                    return;
                }

                if (ContinuousDisplayRefreshRequested is { } refresh)
                    await refresh(Math.Max(0, _continuousVisibleRange.StartIndex));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Continuous video dimension repair failed: {ex}");
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_continuousDimensionRepairCts, cts))
                    _continuousDimensionRepairCts = null;
            });
            cts.Dispose();
        }
    }

    private static bool HasUsableDimensions((int Width, int Height) size) =>
        size.Width > 1 && size.Height > 1;

    /// <summary>
    /// Coordinates state retained by the continuous panel. The panel has already
    /// materialized its bounded range before reporting it, so visible thumbnail
    /// work can only observe cached items and can never create a full result set.
    /// This method is called on the UI thread by the window hosting that panel.
    /// </summary>
    public void UpdateContinuousDisplayViewport(
        ImageDisplayRange visibleRange,
        ImageDisplayRange retainedRange,
        ContinuousScrollDirection scrollDirection = ContinuousScrollDirection.None)
    {
        var source = ContinuousImageItemSource;
        if (source is null)
            return;

        var previousSource = _continuousViewportSource;
        var previousVisibleRange = _continuousVisibleRange;
        var prefetchRange = GetContinuousPrefetchRange(
            ReferenceEquals(previousSource, source) ? previousVisibleRange : default,
            visibleRange,
            scrollDirection,
            source.Count);
        source.UpdateRetainedRange(CombineRanges(retainedRange, prefetchRange, source.Count));

        if (ReferenceEquals(previousSource, source)
            && previousVisibleRange == visibleRange
            && _continuousPrefetchRange == prefetchRange)
        {
            return;
        }

        CancelContinuousViewportThumbnailLoad();
        _continuousViewportSource = source;
        _continuousVisibleRange = visibleRange;
        _continuousPrefetchRange = prefetchRange;
        if (visibleRange.IsEmpty)
            return;

        var cts = new CancellationTokenSource();
        _continuousViewportThumbnailCts = cts;
        var visibleItems = source.GetCachedItems(visibleRange);
        var prefetchItems = source.GetOrCreateItems(prefetchRange);
        _pageManager.LoadViewportThumbnailsForItems(visibleItems, cts.Token);
        _pageManager.LoadPrefetchThumbnailsForItems(prefetchItems, cts.Token);
    }

    private bool RefreshContinuousViewportThumbnailsAfterZoom()
    {
        if (DisplayMode != ImageDisplayMode.Continuous || ContinuousImageItemSource is not { } source)
            return false;

        // The virtualizing panel owns which items exist. Keep the old bytes on its
        // current visible and prefetch items until each replacement has succeeded.
        var visibleRange = _continuousVisibleRange;
        var prefetchRange = _continuousPrefetchRange;
        var visibleItems = source.GetCachedItems(visibleRange);
        var visiblePaths = new HashSet<string>(
            visibleItems.Select(item => item.FilePath),
            StringComparer.OrdinalIgnoreCase);
        var prefetchItems = source.GetOrCreateItems(prefetchRange)
            .Where(item => !visiblePaths.Contains(item.FilePath))
            .ToArray();

        // Retained items are already materialized but may cross into the viewport
        // before being evicted. Mark them now, then schedule only active ranges.
        foreach (var item in source.GetAllCachedItems())
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

        CancelContinuousViewportThumbnailLoad();
        _continuousViewportSource = source;
        _continuousVisibleRange = visibleRange;
        _continuousPrefetchRange = prefetchRange;
        if (visibleRange.IsEmpty)
            return true;

        var cts = new CancellationTokenSource();
        _continuousViewportThumbnailCts = cts;
        _pageManager.LoadViewportThumbnailsForItems(visibleItems, cts.Token);
        _pageManager.LoadPrefetchThumbnailsForItems(prefetchItems, cts.Token);
        return true;
    }

    /// <summary>
    /// Returns existing item projections only. It never materializes additional
    /// continuous items merely to service a context-menu command.
    /// </summary>
    public IReadOnlyList<ImageViewItem> GetMaterializedThumbnailItems(IEnumerable<string> filePaths)
    {
        var paths = new HashSet<string>(
            filePaths.Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return Array.Empty<ImageViewItem>();

        return Images
            .Concat(ContinuousImageItemSource?.GetAllCachedItems() ?? Array.Empty<ImageViewItem>())
            .Where(item => paths.Contains(item.FilePath))
            .DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CancelContinuousViewportThumbnailLoad()
    {
        _continuousViewportThumbnailCts?.Cancel();
        _continuousViewportThumbnailCts?.Dispose();
        _continuousViewportThumbnailCts = null;
        _continuousViewportSource = null;
        _continuousVisibleRange = default;
        _continuousPrefetchRange = default;
    }

    private static ImageDisplayRange GetContinuousPrefetchRange(
        ImageDisplayRange previousVisibleRange,
        ImageDisplayRange visibleRange,
        ContinuousScrollDirection scrollDirection,
        int totalCount)
    {
        if (visibleRange.IsEmpty || previousVisibleRange.IsEmpty || totalCount <= 0)
            return default;

        var count = Math.Max(1, (int)Math.Ceiling(visibleRange.Count * (2d / 3d)));
        if (scrollDirection == ContinuousScrollDirection.Down)
        {
            var start = Math.Clamp(visibleRange.EndExclusive, 0, totalCount);
            return new ImageDisplayRange(start, Math.Min(count, totalCount - start));
        }

        if (scrollDirection == ContinuousScrollDirection.Up)
        {
            var end = Math.Clamp(visibleRange.StartIndex, 0, totalCount);
            var start = Math.Max(0, end - count);
            return new ImageDisplayRange(start, end - start);
        }

        return default;
    }

    private static ImageDisplayRange CombineRanges(
        ImageDisplayRange first,
        ImageDisplayRange second,
        int totalCount)
    {
        if (second.IsEmpty)
            return first;
        if (first.IsEmpty)
            return second;

        var start = Math.Max(0, Math.Min(first.StartIndex, second.StartIndex));
        var end = Math.Min(totalCount, Math.Max(first.EndExclusive, second.EndExclusive));
        return new ImageDisplayRange(start, Math.Max(0, end - start));
    }

    private static ImageDisplayLayoutMode ToImageDisplayLayoutMode(string? waterfallMode) =>
        waterfallMode switch
        {
            "Vertical" => ImageDisplayLayoutMode.Vertical,
            "Horizontal" => ImageDisplayLayoutMode.Horizontal,
            _ => ImageDisplayLayoutMode.None
        };

    public double PreSearchScrollOffset { get; set; }
    public event Action? ScrollRestoreRequested;



    // ==================== Paging ====================

    public async Task ApplyThumbnailSettingsAsync()
    {
        var oldPageSize = _pageManager.PageSize;
        var pageSize = ImagePaging.Clamp(AppSettings.ImagesPerPage);
        AppSettings.ImagesPerPage = pageSize;
        _pageManager.PageSize = pageSize;
        SyncUISettingsFromAppData();

        if (DisplayMode == ImageDisplayMode.Paged)
        {
            var anchorIndex = SelectedFilePaths
                .Select(path => ActiveFileList.FindIndex(file =>
                    string.Equals(file, path, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(index => index >= 0);

            if (anchorIndex < 0)
                anchorIndex = CurrentPage * oldPageSize;

            if (ActiveFileList.Count == 0)
            {
                _isNavigating = true;
                CurrentPage = 0;
                _isNavigating = false;
                Images = new();
                LoadedInfoText = string.Empty;
            }
            else
            {
                anchorIndex = Math.Clamp(anchorIndex, 0, ActiveFileList.Count - 1);
                SetDisplayPaging();
                await ShowPageAsync(anchorIndex / pageSize);
            }
        }

        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync() { if (CurrentPage > 0) await ShowPageAsync(CurrentPage - 1); }

    [RelayCommand]
    private async Task NextPageAsync() { if (CurrentPage < TotalPages - 1) await ShowPageAsync(CurrentPage + 1); }

    /// <summary>The owning view rebuilds continuous layout using its viewport width.</summary>
    public event Func<int, Task>? ContinuousDisplayRefreshRequested;

    /// <summary>Refreshes either display mode at a global result index.</summary>
    public async Task RefreshImageDisplayAsync(int preferredIndex)
    {
        if (DisplayMode == ImageDisplayMode.Continuous)
        {
            // The view supplies the actual viewport width, but every result-list
            // refresh must request a rebuild, not just a display-mode change.
            if (ContinuousDisplayRefreshRequested is { } refresh)
                await refresh(Math.Clamp(preferredIndex, 0, Math.Max(0, ActiveFileList.Count - 1)));
            return;
        }

        if (ActiveFileList.Count == 0)
            return;

        preferredIndex = Math.Clamp(preferredIndex, 0, ActiveFileList.Count - 1);
        switch (DisplayMode)
        {
            case ImageDisplayMode.Paged:
                await ShowPagedDisplayAsync(preferredIndex / _pageManager.PageSize);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(DisplayMode), DisplayMode, null);
        }
    }

    /// <summary>
    /// Compatibility entry point for existing page navigation and UI event
    /// handlers. All callers now pass through the display-mode dispatcher.
    /// </summary>
    public Task ShowPageAsync(int pageIndex)
    {
        var range = ImageDisplayRange.ForPage(pageIndex, _pageManager.PageSize, ActiveFileList.Count);
        return RefreshImageDisplayAsync(range.StartIndex);
    }

    public async Task<bool> TrySetDisplayModeAsync(ImageDisplayMode mode, int? preferredIndex = null)
    {
        if (mode == DisplayMode)
            return true;

        if (mode == ImageDisplayMode.Continuous && !IsContinuousDisplayAvailable)
        {
            StatusText = "连续显示模式正在准备，当前继续使用分页显示";
            return false;
        }

        _pageManager.CancelCurrentLoads();
        DisplayMode = mode;
        OnPropertyChanged(nameof(IsPagedDisplay));

        await RefreshImageDisplayAsync(preferredIndex ?? CurrentPage * _pageManager.PageSize);

        return true;
    }

    private async Task ShowPagedDisplayAsync(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= TotalPages) return;
        var sw = Stopwatch.StartNew();
        PerfLogger.Log($"[ShowPage] START page={pageIndex}");
        DeferHashPrecomputeForPaging();
        _isNavigating = true;
        CurrentPage = pageIndex;
        _isNavigating = false;
        // Preload tag cache for the current page's files (subfolder files especially)
        var pageFiles = ActiveFileList.Skip(pageIndex * _pageManager.PageSize).Take(_pageManager.PageSize).ToList();
        if (pageFiles.Count > 0) _ = PreloadTagsForFilesAsync(pageFiles);
        await _pageManager.ShowPageAsync(pageIndex, TotalPages,
            ActiveFileList, GetTagsForFile, IsShowingSearchResult, CurrentFolder);
        PerfLogger.Log($"[ShowPage] END elapsed={sw.ElapsedMilliseconds}ms");
    }

    private void DeferHashPrecomputeForPaging()
    {
        _deferMaintenanceUntilUtc = DateTime.UtcNow.AddSeconds(15);
        var cts = Volatile.Read(ref _hashCts);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
                AppLogger.Memory("HashPrecompute deferred by paging");
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(CurrentFolder))
        {
            _idleTimer?.Stop();
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, OnIdleTimerTick);
            _idleTimer.Start();
        }
    }

    // ==================== Thumbnail Zoom ====================

    private bool _isNavigating;

    // Respond to combobox page selection
    partial void OnCurrentPageChanged(int value)
    {
        if (!_isNavigating && value >= 0 && value < TotalPages)
            _ = ShowPageAsync(value);
    }

    partial void OnWaterfallModeChanged(string value)
    {
        InvalidateContinuousDisplayGeometry();
        OnPropertyChanged(nameof(GridThumbnailHeight));
        _pageManager.UpdateUiState(new PageUiState(
            ThumbnailBaseWidth, WaterfallMode, AppSettings.ThumbnailAspectRatio));
        _ = _pageManager.RefreshDecodeWidthForCurrentModeAsync();
    }
    partial void OnThumbnailBaseWidthChanged(double value)
    {
        if (DisplayMode != ImageDisplayMode.Continuous)
            InvalidateContinuousDisplayGeometry();
        OnPropertyChanged(nameof(GridThumbnailHeight));
        _ = RefreshFolderCardThumbnailsForCurrentScaleAsync();
    }
    partial void OnShowFileNameChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
    partial void OnShowTagsChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
    partial void OnShowOrientationChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
partial void OnKeepPaddingChanged(bool value)
{
    OnPropertyChanged(nameof(ThumbnailPadding));
    OnPropertyChanged(nameof(ThumbnailBorderThickness));
    OnPropertyChanged(nameof(ThumbnailInnerCornerRadius));
    OnPropertyChanged(nameof(ThumbnailImageStretch));
}
partial void OnCornerRadiusDipChanged(double value)
{
    OnPropertyChanged(nameof(ThumbnailCornerRadius));
    OnPropertyChanged(nameof(ThumbnailInnerCornerRadius));
    OnPropertyChanged(nameof(ThumbnailSelectionCornerRadius));
}

    partial void OnZoomTickChanged(double value)
    {
        SaveZoomForMode(WaterfallMode);
        _pageManager.UpdateUiState(new PageUiState(
            ThumbnailBaseWidth, WaterfallMode, AppSettings.ThumbnailAspectRatio));
        var (baseWidth, _) = _pageManager.OnZoomTickChanged(value, CurrentPage, TotalPages,
            ActiveFileList, GetTagsForFile);
        ThumbnailBaseWidth = baseWidth;
    }

    partial void OnShowAllSubfoldersChanged(bool value)
    {
        if (value) SearchScope = 1; // auto-switch to recursive
        OnPropertyChanged(nameof(ShowFolderSection));
        OnPropertyChanged(nameof(IsSortScopeEnabled));
        if (string.IsNullOrEmpty(CurrentFolder)) return;
        var requestVersion = BeginFolderViewRequest();
        var folderWorkToken = BeginFolderWorkScope(CurrentFolder);
        var folder = CurrentFolder;
        _ = RefreshFolderItemsAsync(folderWorkToken);
        _ = RebuildFileListAsync(requestVersion, folder, value, folderWorkToken);
    }

    partial void OnIsShowingSearchResultChanged(bool value)
    {
        NotifyFolderSectionSearchStateChanged();
    }

    partial void OnFolderSearchTextChanged(string value)
    {
        FolderSearchSuggestions.Clear();
        var keyword = value?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            IsFolderSearchPopupOpen = false;
            return;
        }
        foreach (var root in FolderTree)
            CollectLoadedNodes(root, FolderSearchSuggestions, keyword);
        IsFolderSearchPopupOpen = FolderSearchSuggestions.Count > 0;
    }

    private static void CollectLoadedNodes(FolderTreeNode node,
        ObservableCollection<FolderTreeNode> results, string keyword)
    {
        if (node.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            results.Add(node);
        foreach (var child in node.Children)
        {
            if (child.DisplayName != "...") // skip placeholder
                CollectLoadedNodes(child, results, keyword);
        }
    }


}
