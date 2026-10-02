using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Imaging;
using System.Diagnostics;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private DisplayFilterOptions _displayFilter = new();
    [ObservableProperty] private bool _isDisplayFilterBusy;
    [ObservableProperty] private int _displayFilterSourceCount;
    [ObservableProperty] private int _displayFilterUnknownCount;
    private List<string> _displayFilteredFiles = new();
    private List<string> _filteredNavigationFiles = new();
    private CancellationTokenSource? _displayFilterCts;
    private int _displayFilterVersion;

    public bool HasDisplayFilter => DisplayFilter.IsActive;
    public bool HasTypeFilter => DisplayFilter.TypeId != null;
    public bool HasOrientationFilter => DisplayFilter.Orientation != MediaOrientation.All;
    public bool HasContentRatingFilter => !DisplayFilter.IncludesAllContentRatings;
    public string DisplayFilterButtonText => HasDisplayFilter
        ? $"显示筛选 ({(HasTypeFilter ? 1 : 0) + (HasOrientationFilter ? 1 : 0) + (HasContentRatingFilter ? 1 : 0)}) ▾" : "显示筛选 ▾";
    public string TypeFilterText => "类型：" + (FileTypeConstants.SupportedTypes
        .FirstOrDefault(t => t.Id == DisplayFilter.TypeId)?.DisplayName ?? "全部") + " ×";
    public string OrientationFilterText => "方向：" + (DisplayFilter.Orientation switch
    {
        MediaOrientation.Landscape => "横向", MediaOrientation.Portrait => "竖向",
        MediaOrientation.Square => "正方形", _ => "不限"
    }) + (DisplayFilter.IncludeUnknownDimensions ? "（含尺寸未知）" : "") + " ×";
    public string ContentRatingFilterText => "年龄分级：" + string.Join("、", new[]
    {
        (ContentRatingFilter.General, "全年龄"),
        (ContentRatingFilter.Sensitive, "敏感"),
        (ContentRatingFilter.Questionable, "大尺度"),
        (ContentRatingFilter.Explicit, "R-18")
    }.Where(entry => DisplayFilter.ContentRatings.HasFlag(entry.Item1)).Select(entry => entry.Item2)) + " ×";
    public string DisplayFilterCountText => IsDisplayFilterBusy ? "正在筛选…"
        : $"显示 {ActiveFileList.Count} / {DisplayFilterSourceCount} 个文件";
    public string DisplayFilterUnknownText => HasOrientationFilter
        ? $"尺寸未知：{DisplayFilterUnknownCount} 个文件" : "";
    public bool IsDisplayFilterEmpty => HasDisplayFilter && !IsDisplayFilterBusy && ActiveFileList.Count == 0;

    private void NotifyDisplayFilterState()
    {
        OnPropertyChanged(nameof(HasDisplayFilter));
        OnPropertyChanged(nameof(HasTypeFilter));
        OnPropertyChanged(nameof(HasOrientationFilter));
        OnPropertyChanged(nameof(HasContentRatingFilter));
        OnPropertyChanged(nameof(DisplayFilterButtonText));
        OnPropertyChanged(nameof(TypeFilterText));
        OnPropertyChanged(nameof(OrientationFilterText));
        OnPropertyChanged(nameof(ContentRatingFilterText));
        OnPropertyChanged(nameof(DisplayFilterCountText));
        OnPropertyChanged(nameof(DisplayFilterUnknownText));
        OnPropertyChanged(nameof(IsDisplayFilterEmpty));
        OnPropertyChanged(nameof(ActiveFileList));
        OnPropertyChanged(nameof(SearchResultInfo));
        OnPropertyChanged(nameof(HasSearchResults));
    }

    public async Task ApplyDisplayFilterAsync(DisplayFilterOptions options)
    {
        if (options.TypeId != null && !FileTypeConstants.SupportedTypes.Any(t => t.Id == options.TypeId))
            options = options with { TypeId = null };
        var type = FileTypeConstants.SupportedTypes.FirstOrDefault(t => t.Id == options.TypeId);
        if (type is { SupportsDimensions: false }) options = options with { Orientation = MediaOrientation.All };
        DisplayFilter = options;
        await RebuildDisplayFilterAsync();
    }

    [RelayCommand]
    public Task ResetDisplayFilterAsync() => ApplyDisplayFilterAsync(new());
    [RelayCommand]
    public Task RemoveTypeFilterAsync() => ApplyDisplayFilterAsync(DisplayFilter with { TypeId = null });
    [RelayCommand]
    public Task RemoveOrientationFilterAsync() => ApplyDisplayFilterAsync(DisplayFilter with
        { Orientation = MediaOrientation.All, IncludeUnknownDimensions = true });
    [RelayCommand]
    public Task RemoveContentRatingFilterAsync() => ApplyDisplayFilterAsync(DisplayFilter with
        { ContentRatings = ContentRatingFilter.All });

    private void CancelDisplayFilter()
    {
        ++_displayFilterVersion;
        _displayFilterCts?.Cancel();
        IsDisplayFilterBusy = false;
        // The owning operation disposes its CTS after its worker has stopped.
    }

    /// <summary>Snapshots input before background work, then atomically publishes the latest result.</summary>
    private async Task<bool> UpdateDisplayFilterAsync(bool preserveNavigation = false)
    {
        CancelDisplayFilter();
        if (!preserveNavigation) Interlocked.Increment(ref _resultNavigationVersion);
        var version = _displayFilterVersion;
        var cts = new CancellationTokenSource();
        _displayFilterCts = cts;
        var token = cts.Token;
        var options = DisplayFilter;
        var showingSearch = IsShowingSearchResult;
        var sourceList = showingSearch ? _tagSearch.SearchResultFiles : _allFiles;
        IReadOnlyList<string> source = options.IsActive ? sourceList.ToArray() : sourceList;
        var navigationList = !showingSearch && _similarityScores.Count > 0
            ? _tagSearch.SearchResultFiles : null;
        IReadOnlyList<string>? navigationSource = options.IsActive
            ? navigationList?.ToArray() ?? Array.Empty<string>()
            : navigationList;
        IsDisplayFilterBusy = true;
        NotifyDisplayFilterState();
        try
        {
            var filterStopwatch = Stopwatch.StartNew();
            if (!options.IsActive)
            {
                if (version != _displayFilterVersion || token.IsCancellationRequested) return false;
                _displayFilteredFiles = sourceList;
                _filteredNavigationFiles = navigationList ?? new();
                InvalidateContinuousDisplayGeometry();
                DisplayFilterSourceCount = sourceList.Count;
                DisplayFilterUnknownCount = 0;
                PerfLogger.Log($"[SortPerf] filter_publish_ms={filterStopwatch.ElapsedMilliseconds} count={sourceList.Count} active=false");
                if (!preserveNavigation) _currentResultIndex = 0;
                return true;
            }

            async Task<DisplayFilterResult> Filter(string[] paths) => await Task.Run(() =>
                DisplayFileFilter.ApplyAsync(paths, options,
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
                    },
                    ThumbnailGenerator.GetDimensionsOrUnknown, token), token);

            var result = await Filter(source.ToArray());
            var navigation = navigationSource is null || navigationSource.Count == 0
                ? new DisplayFilterResult(new(), 0)
                : await Filter(navigationSource.ToArray());
            if (version != _displayFilterVersion || token.IsCancellationRequested) return false;
            _displayFilteredFiles = result.Files;
            _filteredNavigationFiles = navigation.Files;
            InvalidateContinuousDisplayGeometry();
            DisplayFilterSourceCount = source.Count;
            DisplayFilterUnknownCount = result.UnknownDimensionsCount;
            PerfLogger.Log($"[SortPerf] filter_publish_ms={filterStopwatch.ElapsedMilliseconds} count={result.Files.Count} active={options.IsActive}");
            if (!preserveNavigation) _currentResultIndex = 0;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (version == _displayFilterVersion)
            {
                _displayFilteredFiles = new();
                _filteredNavigationFiles = new();
                InvalidateContinuousDisplayGeometry();
                DisplayFilterSourceCount = source.Count;
                DisplayFilterUnknownCount = 0;
                _pageManager.CancelCurrentLoads();
                _pageManager.InvalidateCache();
                SetDisplayPaging();
                Images = new();
                StatusText = $"筛选失败：{ex.Message}";
                AppLogger.Warn($"Display filter failed: {ex}");
            }
            return false;
        }
        finally
        {
            if (version == _displayFilterVersion)
            {
                IsDisplayFilterBusy = false;
                NotifyDisplayFilterState();
            }
            if (ReferenceEquals(_displayFilterCts, cts)) _displayFilterCts = null;
            cts.Dispose();
        }
    }

    private void SetDisplayPaging()
    {
        TotalPages = (ActiveFileList.Count + _pageManager.PageSize - 1) / _pageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
    }

    private async Task RebuildDisplayFilterAsync(int? preferredPage = null, bool renderPage = true)
    {
        if (!await UpdateDisplayFilterAsync()) return;
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
            StatusText = HasDisplayFilter ? "没有符合当前筛选的文件" : "没有可显示的文件";
        }
        else if (renderPage)
        {
            await ShowPageAsync(Math.Clamp(preferredPage ?? 0, 0, TotalPages - 1));
            StatusText = DisplayFilterCountText;
        }
        else
        {
            // Search jump navigation publishes the new file snapshot first,
            // then renders the target page once. Rendering the old page here
            // causes a visible refresh before the actual jump.
            StatusText = DisplayFilterCountText;
        }
    }

    private async Task ApplyTagSearchDisplayAsync()
    {
        _similarityScores.Clear();
        _similarityMatchKinds.Clear();
        IsShowingSearchResult = true;
        var source = _tagSearch.SearchResultFiles;
        var order = CurrentSortOrder;
        var scope = CurrentSortScope;
        var showAllSubfolders = ShowAllSubfolders;
        var sortVersion = Volatile.Read(ref _sortRequestVersion);
        var sorted = source.ToList();
        await SortFilesAsync(sorted, order, showAllSubfolders, scope);
        if (!ReferenceEquals(source, _tagSearch.SearchResultFiles) ||
            sortVersion != Volatile.Read(ref _sortRequestVersion) ||
            order != CurrentSortOrder || scope != CurrentSortScope ||
            showAllSubfolders != ShowAllSubfolders)
            return;
        _tagSearch.SearchResultFiles = sorted;
        await RebuildDisplayFilterAsync();
    }
}
