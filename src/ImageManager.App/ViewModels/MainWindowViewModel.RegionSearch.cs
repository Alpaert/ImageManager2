using CommunityToolkit.Mvvm.ComponentModel;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.App.Services;
using System.Diagnostics;
using ImageManager.Common.Helpers;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isRegionSearchActive;
    [ObservableProperty] private bool _isRegionSearchBusy;
    [ObservableProperty] private bool _hasRegionSearchResults;
    [ObservableProperty] private string _regionSearchStatus = "选择图片，拖动框选区域以搜索";
    [ObservableProperty] private string _regionSearchInputName = "尚未选择图片";
    [ObservableProperty] private string _regionSearchConditionText = "整图";
    [ObservableProperty] private int _regionSearchResultCount;
    [ObservableProperty] private bool _regionSearchRecursive;
    public string RegionSearchResultLimitLabel => $"最多 {Math.Clamp(AppSettings.SimilaritySearchResultLimit, 1, 500)} 张";
    public double RegionSearchRestoreOffset { get; private set; }
    private (long Region, int Folder, int Search)? _regionScrollRestoreIntent;
    public bool CanRestoreRegionSearchScroll => !IsRegionSearchActive &&
        _regionScrollRestoreIntent is { } intent &&
        intent.Region == _regionRequestVersion && intent.Folder == _folderViewRequestVersion &&
        intent.Search == _searchRequestVersion;

    private readonly SemaphoreSlim _regionInferenceGate = new(1, 1);
    private CancellationTokenSource? _regionSessionRequest;
    private IDisposable? _regionModelLease;
    private long _regionRequestVersion;
    private RegionSessionSnapshot? _regionSnapshot;
    private (string Path, ImageRegion? Region, RegionSearchMode Mode)? _regionInput;
    private sealed record RegionSessionSnapshot(int Page, bool ShowingSearch, string[] Results,
        Dictionary<string, float> Scores, Dictionary<string, SimilarityMatchKind> Kinds,
        string[] Selection, string[] Navigation, int ResultIndex, string Status, DisplayFilterOptions Filter);

    public void BeginRegionSearchSession(double scrollOffset)
    {
        if (IsRegionSearchActive) return;
        _regionModelLease = _regionSearchService?.BeginSession();
        _regionScrollRestoreIntent = null;
        Interlocked.Increment(ref _regionRequestVersion);
        CancelDisplayFilter();
        Interlocked.Increment(ref _searchRequestVersion);
        Volatile.Read(ref _searchCts)?.Cancel();
        Interlocked.Increment(ref _resultNavigationVersion);
        _regionSnapshot = new(CurrentPage, IsShowingSearchResult, _tagSearch.SearchResultFiles.ToArray(),
            new(_similarityScores, StringComparer.OrdinalIgnoreCase),
            new(_similarityMatchKinds, StringComparer.OrdinalIgnoreCase),
            SelectedFilePaths.ToArray(), ResultNavigationFiles.ToArray(), _currentResultIndex, StatusText, DisplayFilter);
        RegionSearchRestoreOffset = scrollOffset;
        RegionSearchRecursive = SearchScope != 0;
        _regionInput = null;
        HasRegionSearchResults = false;
        RegionSearchResultCount = 0;
        RegionSearchInputName = "尚未选择图片";
        RegionSearchConditionText = "整图";
        RegionSearchStatus = "选择图片，拖动框选区域以搜索";
        IsRegionSearchActive = true;
    }

    partial void OnRegionSearchRecursiveChanged(bool value)
    {
        if (IsRegionSearchActive && _regionInput is { } input)
            _ = QueueRegionSearchAsync(input.Path, input.Region, input.Mode);
    }

    public void SuspendRegionSearch()
    {
        Interlocked.Increment(ref _regionRequestVersion);
        Interlocked.Exchange(ref _regionSessionRequest, null)?.Cancel();
        CancelDisplayFilter();
        _pageManager.CancelCurrentLoads();
        Interlocked.Increment(ref _resultNavigationVersion);
        IsRegionSearchBusy = false;
        if (IsRegionSearchActive)
            RegionSearchStatus = HasRegionSearchResults ? "正在调整输入，保留上次结果" : "等待输入图片或选区";
    }

    public void StopRegionSearch()
    {
        SuspendRegionSearch();
        RegionSearchStatus = HasRegionSearchResults ? "搜索已停止，保留上次结果" : "搜索已停止";
    }

    public void InvalidateRegionSearchInput()
    {
        _regionInput = null;
        SuspendRegionSearch();
    }

    public void UpdateRegionSearchInputState(string? path, ImageRegion? region, RegionSearchMode mode, bool awaitingSelection)
    {
        if (!IsRegionSearchActive) return;
        RegionSearchInputName = path is null ? "尚未选择图片" : Path.GetFileName(path);
        RegionSearchConditionText = (mode == RegionSearchMode.FindOriginal ? "找原图 · " : "相似内容 · ") +
            (awaitingSelection ? "等待框选" : region is null ? "整图" : "框选区域");
        if (path is null || awaitingSelection)
        {
            InvalidateRegionSearchInput();
            RegionSearchStatus = path is null ? "选择图片，拖动框选区域以搜索"
                : HasRegionSearchResults ? "等待框选，保留上次结果" : "请框选搜索区域，或点击搜索整图";
        }
    }

    public Task RefreshRegionSearchSettingsAsync()
    {
        OnPropertyChanged(nameof(RegionSearchResultLimitLabel));
        return IsRegionSearchActive && _regionInput is { } input
            ? QueueRegionSearchAsync(input.Path, input.Region, input.Mode, immediate: true)
            : Task.CompletedTask;
    }

    public async Task QueueRegionSearchAsync(string path, ImageRegion? region, RegionSearchMode mode, bool immediate = false)
    {
        if (!IsRegionSearchActive) return;
        SuspendRegionSearch();
        _regionInput = (path, region, mode);
        RegionSearchInputName = Path.GetFileName(path);
        RegionSearchConditionText = (mode == RegionSearchMode.FindOriginal ? "找原图 · " : "相似内容 · ") +
                                    (region is null ? "整图" : "框选区域");
        var version = _regionRequestVersion;
        var cts = new CancellationTokenSource();
        _regionSessionRequest = cts;
        var token = cts.Token;
        var requestWatch = Stopwatch.StartNew();
        bool IsCurrent() => IsRegionSearchActive && version == _regionRequestVersion && !token.IsCancellationRequested;
        var root = CurrentFolder;
        var recursive = RegionSearchRecursive;
        var files = _allFiles.ToArray();
        var limit = Math.Clamp(AppSettings.SimilaritySearchResultLimit, 1, 500);
        IsRegionSearchBusy = true;
        RegionSearchStatus = immediate ? "正在搜索…" : "等待框选稳定…";
        Task<RegionSearchResponse>? worker = null;
        try
        {
            if (!immediate) await Task.Delay(300, token);
            if (!IsCurrent()) return;
            RegionSearchStatus = mode == RegionSearchMode.SimilarContent
                ? "正在找相似内容…首次搜索需要加载模型" : "正在找原图…";
            // Native inference owns this gate even after its UI wait is cancelled.
            worker = Task.Run(async () =>
            {
                await _regionInferenceGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var enumerationWatch = Stopwatch.StartNew();
                    AppLogger.Info($"RegionSearch.WorkerReady request={version} mode={mode} waitMs={requestWatch.ElapsedMilliseconds}");
                    var candidates = !string.IsNullOrEmpty(root)
                        ? (await EnumerateMediaFilesAsync(root, recursive, token).ConfigureAwait(false)).Files
                        : files.ToList();
                    AppLogger.Info($"RegionSearch.ScopeReady request={version} candidates={candidates.Count} enumerateMs={enumerationWatch.ElapsedMilliseconds}");
                    token.ThrowIfCancellationRequested();
                    var service = _regionSearchService ?? throw new InvalidOperationException("局部搜图服务未配置。");
                    return await service.SearchAsync(
                        path, region, candidates.Where(FileTypeConstants.IsImageFile).ToArray(), mode, limit, token)
                        .ConfigureAwait(false);
                }
                finally { _regionInferenceGate.Release(); }
            }, token);
            var result = await worker.WaitAsync(token);
            if (!IsCurrent()) return;
            var publishWatch = Stopwatch.StartNew();
            await _dispatcher.InvokeAsync(async () =>
            {
                if (!IsCurrent()) return;
                await ApplySimilarityResultsAsync(result.Results, SimilaritySearchMode.Semantic, IsCurrent);
                if (!IsCurrent()) return;
                HasRegionSearchResults = true;
                RegionSearchResultCount = result.Results.Count;
                RegionSearchStatus = $"找到 {result.Results.Count} 张图片，向量覆盖 {result.IndexedImages}/{result.CandidateImages} 张";
                if (result.IndexedImages == 0) RegionSearchStatus += "；请在设置中建立对应向量索引";
                StatusText = RegionSearchStatus;
            });
            if (IsCurrent())
                AppLogger.Info($"RegionSearch.Published request={version} mode={mode} results={result.Results.Count} publishMs={publishWatch.ElapsedMilliseconds} totalMs={requestWatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (IsCurrent()) RegionSearchStatus = $"搜索失败：{ex.Message}";
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { if (IsCurrent()) IsRegionSearchBusy = false; });
            Interlocked.CompareExchange(ref _regionSessionRequest, null, cts);
            if (worker is { IsCompleted: false }) _ = DisposeRegionWorkerAsync(worker, cts);
            else { _ = worker?.Exception; cts.Dispose(); }
        }
    }

    private static async Task DisposeRegionWorkerAsync(Task worker, CancellationTokenSource cts)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { /* Observe failures from an invalidated request. */ }
        finally { cts.Dispose(); }
    }

    public async Task ExitRegionSearchSessionAsync()
    {
        if (!IsRegionSearchActive) return;
        SuspendRegionSearch();
        ReleaseRegionModelLease();
        var exitVersion = _regionRequestVersion;
        var folderVersion = _folderViewRequestVersion;
        var searchVersion = _searchRequestVersion;
        bool IsCurrentExit() => exitVersion == _regionRequestVersion && !IsRegionSearchActive &&
            folderVersion == _folderViewRequestVersion && searchVersion == _searchRequestVersion;
        var snapshot = _regionSnapshot;
        _regionSnapshot = null;
        _regionInput = null;
        IsRegionSearchActive = false;
        HasRegionSearchResults = false;
        if (snapshot is null) return;
        _tagSearch.SearchResultFiles = snapshot.Results.ToList();
        _similarityScores.Clear();
        foreach (var pair in snapshot.Scores) _similarityScores[pair.Key] = pair.Value;
        _similarityMatchKinds.Clear();
        foreach (var pair in snapshot.Kinds) _similarityMatchKinds[pair.Key] = pair.Value;
        IsShowingSearchResult = snapshot.ShowingSearch;
        DisplayFilter = snapshot.Filter;
        NotifyFolderSectionSearchStateChanged();
        await RebuildSearchDisplayAsync(IsCurrentExit, preferredPage: snapshot.Page);
        if (!IsCurrentExit()) return;
        ReplaceSelectedFiles(snapshot.Selection);
        _currentResultIndex = snapshot.ResultIndex;
        StatusText = snapshot.Status;
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultInfo));
        _regionScrollRestoreIntent = (exitVersion, folderVersion, searchVersion);
    }

    private void ReleaseRegionModelLease() => Interlocked.Exchange(ref _regionModelLease, null)?.Dispose();

    private async Task RebuildSearchDisplayAsync(Func<bool> isCurrent, bool renderPage = true, int preferredPage = 0)
    {
        if (!isCurrent() || !await UpdateDisplayFilterAsync() || !isCurrent()) return;
        _pageManager.CancelCurrentLoads();
        _pageManager.InvalidateCache();
        SetDisplayPaging();
        if (TotalPages == 0)
        {
            _isNavigating = true;
            CurrentPage = 0;
            _isNavigating = false;
            Images = new();
            LoadedInfoText = "";
        }
        else if (renderPage) await ShowPageAsync(Math.Clamp(preferredPage, 0, TotalPages - 1));
    }

    private void RemoveFilesFromRegionSnapshot(HashSet<string> removedPaths)
    {
        // Deletion also invalidates an exit currently awaiting page/filter work.
        if (IsRegionSearchActive) SuspendRegionSearch();
        else Interlocked.Increment(ref _regionRequestVersion);
        if (_regionSnapshot is not { } snapshot) return;
        var removed = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
        var previousResult = snapshot.ResultIndex >= 0 && snapshot.ResultIndex < snapshot.Navigation.Length
            ? snapshot.Navigation[snapshot.ResultIndex] : null;
        var results = snapshot.Results.Where(path => !removed.Contains(path)).ToArray();
        var navigation = snapshot.Navigation.Where(path => !removed.Contains(path)).ToArray();
        var index = previousResult is null ? -1 : Array.FindIndex(navigation,
            path => string.Equals(path, previousResult, StringComparison.OrdinalIgnoreCase));
        _regionSnapshot = snapshot with
        {
            Results = results,
            Scores = snapshot.Scores.Where(pair => !removed.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            Kinds = snapshot.Kinds.Where(pair => !removed.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            Selection = snapshot.Selection.Where(path => !removed.Contains(path)).ToArray(),
            Navigation = navigation,
            ResultIndex = index >= 0 ? index : Math.Clamp(snapshot.ResultIndex, 0, Math.Max(0, navigation.Length - 1))
        };
    }

    private async Task PublishRegionResultsAsync(IReadOnlyList<SimilaritySearchResult> results, Func<bool> isCurrent)
    {
        var paths = results.Select(result => result.FilePath).ToArray();
        var options = DisplayFilter;
        // Prepare every fallible read before changing the currently displayed result.
        var filtered = await Task.Run(() => DisplayFileFilter.ApplyAsync(paths, options,
            async (candidates, ct) =>
            {
                var dimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
                foreach (var batch in candidates.Chunk(500))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var entry in await _metaRepo.GetDimensionsByPathsAsync(batch.ToList()))
                        dimensions[entry.Key] = entry.Value;
                }
                return dimensions;
            },
            async (candidates, ct) =>
            {
                var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var batch in candidates.Chunk(500))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var entry in await _metaRepo.GetSystemRatingsByPathsAsync(batch.ToList()))
                        ratings[entry.Key] = entry.Value;
                }
                return ratings;
            }, _thumbCache.GetDimensionsOrUnknown, CancellationToken.None));
        if (!isCurrent()) return;
        CancelDisplayFilter();
        _pageManager.CancelCurrentLoads();
        _pageManager.InvalidateCache();
        _tagSearch.SearchResultFiles = paths.ToList();
        _similarityScores.Clear();
        _similarityMatchKinds.Clear();
        foreach (var result in results)
        {
            _similarityScores[result.FilePath] = result.Score;
            _similarityMatchKinds[result.FilePath] = result.MatchKind;
        }
        IsShowingSearchResult = true;
        _displayFilteredFiles = filtered.Files;
        _filteredNavigationFiles = new();
        _displayFilterUnknownPaths = filtered.UnknownDimensionsPaths ?? new(StringComparer.OrdinalIgnoreCase);
        DisplayFilterSourceCount = paths.Length;
        DisplayFilterUnknownCount = filtered.UnknownDimensionsCount;
        _publishedDisplayFilterVersion = _displayFilterVersion;
        InvalidateContinuousDisplayGeometry();
        ReconcileSelectionWithPublishedResults();
        _currentResultIndex = 0;
        SetDisplayPaging();
        NotifyDisplayFilterState();
        NotifyFolderSectionSearchStateChanged();
        if (TotalPages > 0) await ShowPageAsync(0);
        else
        {
            _isNavigating = true;
            CurrentPage = 0;
            _isNavigating = false;
            Images = new();
            LoadedInfoText = "";
        }
        if (!isCurrent()) return;
        if (ActiveFileList.Count > 0) ReplaceSelectedFiles(new[] { ActiveFileList[0] });
        ScrollSearchResultsToTopRequested?.Invoke();
    }
}
