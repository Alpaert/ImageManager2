using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    // ==================== Tag Search ====================

    [RelayCommand]
    private async Task SearchByTag()
    {
        var raw = TagSearchText.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            CurrentTagFilter = string.Empty;
            return;
        }

        DismissTagSearchSuggestions();
        CurrentTagFilter = raw;

        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(CurrentPage);

        try
        {
            await _tagSearch.SearchByTagAsync(raw, await GetSearchScopeFilesAsync(), IsShowingSearchResult,
                list => { foreach (var t in list) TagSearchSuggestions.Add(t); });
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task SelectTagSuggestion(TagCount tag)
    {
        var wasCoTagMode = _tagSearch.IsSuggestionCoTagMode;
        _tagSearch.SelectSuggestion(tag, TagSearchText,
            name =>
            {
                if (!wasCoTagMode)
                {
                    TagSearchText = name;
                    return;
                }

                _suppressTagSearchTextChange = true;
                try
                {
                    TagSearchText = name;
                }
                finally
                {
                    _suppressTagSearchTextChange = false;
                }
            },
            open => IsTagSearchPopupOpen = open,
            () => SearchByTagCommand.ExecuteAsync(null));
        return Task.CompletedTask;
    }

    partial void OnTagSearchTextChanged(string value)
    {
        if (_suppressTagSearchTextChange)
            return;

        if (string.IsNullOrWhiteSpace(value) && _isTagSearchInputFocused)
        {
            PublishFocusTagSuggestions();
            return;
        }

        InvalidateTagSuggestionRequest();
        _tagSuggestionMode = TagSuggestionMode.TypedSuggestions;
        SetCoTagFilterTextWithoutRefresh(string.Empty);
        _coTagSuggestionBase.Clear();
        _coTagSuggestionBaseLoaded = false;
        _coTagSuggestionFiles.Clear();
        OnPropertyChanged(nameof(IsSuggestionCoTagMode));
        _tagSearch.OnTextChanged(value, TagSearchText,
            () => OnPropertyChanged(nameof(SearchBoxBorderColor)),
            keyword => UpdateTagSuggestions(keyword));
    }

    private void UpdateTagSuggestions(string keyword)
    {
        _tagSuggestionMode = TagSuggestionMode.TypedSuggestions;
        PublishTagSuggestions(_tagSearch.GetTypedSuggestions(keyword));
        OnPropertyChanged(nameof(IsSuggestionCoTagMode));
    }

    partial void OnCoTagFilterTextChanged(string value)
    {
        if (!_suppressCoTagFilterChange && IsSuggestionCoTagMode)
            FilterCoTagSuggestions(value);
    }

    public Task OnTagSearchGotFocusAsync(bool preserveCurrentMode = false)
    {
        _isTagSearchInputFocused = true;
        if (preserveCurrentMode && IsTagSearchPopupOpen)
            return Task.CompletedTask;

        PublishFocusTagSuggestions();
        return Task.CompletedTask;
    }

    public void DismissTagSearchSuggestions()
    {
        _isTagSearchInputFocused = false;
        InvalidateTagSuggestionRequest();
        IsTagSearchPopupOpen = false;
    }

    private void ResetTagSuggestionContext()
    {
        DismissTagSearchSuggestions();
        SetCoTagFilterTextWithoutRefresh(string.Empty);
        _tagSuggestionMode = TagSuggestionMode.None;
        _coTagSuggestionBase.Clear();
        _coTagSuggestionBaseLoaded = false;
        _coTagSuggestionFiles.Clear();
        TagSearchSuggestions = new ObservableCollection<TagCount>();
        OnPropertyChanged(nameof(IsSuggestionCoTagMode));
    }

    private void InvalidateCoTagSuggestionCache()
    {
        CancelCoTagFilterRequest();
        _coTagSuggestionBase.Clear();
        _coTagSuggestionBaseLoaded = false;
        _coTagSuggestionFiles.Clear();
        if (_tagSuggestionMode == TagSuggestionMode.SearchResultCoTags && _isTagSearchInputFocused)
            PublishTagSuggestions(GetImmediateTagSuggestions(TagSearchText), keepOpen: true);
    }

    private void PublishFocusTagSuggestions()
    {
        InvalidateTagSuggestionRequest();
        var hasSearchResultContext = _tagSearch.IsSuggestionCoTagMode
            && _coTagSuggestionFiles.Count > 0;
        _tagSuggestionMode = hasSearchResultContext
            ? TagSuggestionMode.SearchResultCoTags
            : TagSuggestionMode.FocusCoTags;
        if (hasSearchResultContext)
            _searchResultSuggestionVersion = _tagSuggestionVersion;
        SetCoTagFilterTextWithoutRefresh(string.Empty);
        var suggestions = _coTagSuggestionBaseLoaded
            ? _coTagSuggestionBase
            : GetImmediateTagSuggestions(TagSearchText);
        PublishTagSuggestions(suggestions, keepOpen: true);
        OnPropertyChanged(nameof(IsSuggestionCoTagMode));

        if (hasSearchResultContext && !_coTagSuggestionBaseLoaded)
            _ = _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
    }

    private void FilterCoTagSuggestions(string value)
    {
        if (!IsSuggestionCoTagMode)
            return;

        IReadOnlyList<TagCount> filtered;
        if (_tagSuggestionMode == TagSuggestionMode.FocusCoTags
            && !string.IsNullOrWhiteSpace(value))
        {
            filtered = _tagSearch.GetTypedSuggestions(value);
        }
        else
        {
            var baseSuggestions = _coTagSuggestionBaseLoaded
                ? _coTagSuggestionBase
                : GetImmediateTagSuggestions(TagSearchText);
            filtered = string.IsNullOrWhiteSpace(value)
                ? baseSuggestions
                : baseSuggestions
                    .Where(tag => tag.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
        PublishTagSuggestions(filtered, keepOpen: true);

        CancelCoTagFilterRequest();
        if (string.IsNullOrWhiteSpace(value)
            || _tagSuggestionMode != TagSuggestionMode.SearchResultCoTags)
            return;

        var requestVersion = ++_coTagFilterVersion;
        var requestCts = new CancellationTokenSource();
        lock (_coTagFilterCtsSync)
            _coTagFilterCts = requestCts;
        _ = RefreshFilteredCoTagsAsync(value, requestVersion, requestCts);
    }

    private async Task RefreshFilteredCoTagsAsync(
        string filter,
        int requestVersion,
        CancellationTokenSource requestCts)
    {
        try
        {
            await Task.Delay(200, requestCts.Token).ConfigureAwait(false);
            var suggestions = await _tagSearch.QueryCurrentCoTagSuggestionsAsync(
                filter,
                requestCts.Token).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                if (requestCts.IsCancellationRequested
                    || requestVersion != _coTagFilterVersion
                    || !_isTagSearchInputFocused
                    || _tagSuggestionMode != TagSuggestionMode.SearchResultCoTags
                    || !string.Equals(CoTagFilterText, filter, StringComparison.Ordinal))
                    return;

                PublishTagSuggestions(suggestions, keepOpen: true);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"共现 Tag 联想查询失败: {ex.Message}");
        }
        finally
        {
            lock (_coTagFilterCtsSync)
            {
                if (ReferenceEquals(_coTagFilterCts, requestCts))
                    _coTagFilterCts = null;
                requestCts.Dispose();
            }
        }
    }

    private void CancelCoTagFilterRequest()
    {
        ++_coTagFilterVersion;
        lock (_coTagFilterCtsSync)
        {
            var previous = _coTagFilterCts;
            _coTagFilterCts = null;
            previous?.Cancel();
        }
    }

    private IReadOnlyList<TagCount> GetImmediateTagSuggestions(string keyword)
    {
        if (!string.IsNullOrWhiteSpace(keyword))
            return _tagSearch.GetTypedSuggestions(keyword);

        return _tagSearch.AllTagCounts
            .OrderByDescending(tag => tag.Count)
            .Take(300)
            .ToList();
    }

    private void InvalidateTagSuggestionRequest()
    {
        CancelCoTagFilterRequest();
        ++_tagSuggestionVersion;
        _tagSuggestionCts?.Cancel();
        _tagSuggestionCts?.Dispose();
        _tagSuggestionCts = null;
    }

    private void PublishTagSuggestions(IEnumerable<TagCount> suggestions, bool keepOpen = false)
    {
        var snapshot = suggestions.ToList();
        TagSearchSuggestions = new ObservableCollection<TagCount>(snapshot);
        IsTagSearchPopupOpen = keepOpen || snapshot.Count > 0;
    }

    private void SetCoTagFilterTextWithoutRefresh(string value)
    {
        _suppressCoTagFilterChange = true;
        try
        {
            CoTagFilterText = value;
        }
        finally
        {
            _suppressCoTagFilterChange = false;
        }
    }

    /// <summary>Remove deleted files incrementally, keeping existing thumbnails intact</summary>
    public async Task RemoveFilesFromViewAsync(HashSet<string> deletedPaths)
    {
        // Guard against OnCurrentPageChanged firing ShowPageAsync during paging updates
        _isNavigating = true;
        try
        {

        var filterWasPending = IsDisplayFilterBusy;
        var wasContinuous = DisplayMode == ImageDisplayMode.Continuous;
        var continuousAnchor = _continuousVisibleRange.StartIndex;
        var continuousSource = wasContinuous ? ContinuousImageItemSource : null;
        var continuousSnapshot = wasContinuous ? ContinuousDisplayGeometrySnapshot : null;
        if (wasContinuous && continuousAnchor > 0)
        {
            var deletedBeforeAnchor = ActiveFileList
                .Take(Math.Min(continuousAnchor, ActiveFileList.Count))
                .Count(deletedPaths.Contains);
            continuousAnchor = Math.Max(0, continuousAnchor - deletedBeforeAnchor);
        }
        CancelDisplayFilter();
        IsDisplayFilterBusy = false;
        // Remove from master lists and both published snapshots.
        if (!ReferenceEquals(_displayFilteredFiles, _allFiles))
            _displayFilteredFiles.RemoveAll(p => deletedPaths.Contains(p));
        _filteredNavigationFiles.RemoveAll(p => deletedPaths.Contains(p));
        PublishAllFiles(_allFiles.Where(path => !deletedPaths.Contains(path)).ToList());
        _selectedFilePaths.RemoveWhere(deletedPaths.Contains);
        if (!wasContinuous || HasDisplayFilter || filterWasPending)
            InvalidateContinuousDisplayGeometry();
        if (_tagSearch.SearchResultFiles.Count > 0
            && _tagSearch.SearchResultFiles.RemoveAll(p => deletedPaths.Contains(p)) > 0)
            _tagSearch.NotifySearchResultFilesChanged();
        if (_tagSearch.IsSuggestionCoTagMode)
        {
            InvalidateCoTagSuggestionCache();
            _ = _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
        }

        if (HasDisplayFilter || filterWasPending)
        {
            CaptureSelectionForNextPageRefresh();
            await RebuildDisplayFilterAsync(CurrentPage);
            return;
        }

        DisplayFilterSourceCount = (IsShowingSearchResult ? _tagSearch.SearchResultFiles : _allFiles).Count;
        NotifyDisplayFilterState();
        // Recalculate paging
        var files = ActiveFileList;
        TotalPages = files.Count == 0 ? 0 : (files.Count + _pageManager.PageSize - 1) / _pageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        // Clamp current page
        if (CurrentPage >= TotalPages && TotalPages > 0)
            CurrentPage = TotalPages - 1;

        if (wasContinuous && continuousSource is not null && continuousSnapshot is not null)
        {
            CancelContinuousGeometryWorkPreservingSource();
            if (await ApplyContinuousDeletionAsync(continuousSource, continuousSnapshot, deletedPaths))
            {
                _pageManager.InvalidateCache();
                return;
            }

            // The source may have been replaced by another result update while the
            // CPU-only geometry rebuild was running. Fall back to the normal full
            // continuous refresh in that case rather than publishing stale indexes.
            InvalidateContinuousDisplayGeometry();
        }

        if (TotalPages == 0)
        {
            Images = new ObservableCollection<ImageViewItem>();
            _pageManager.InvalidateCache();
            return;
        }

        if (wasContinuous)
        {
            // Continuous mode owns its virtualized source; the page Items collection
            // is not the visible surface. Rebuild the source after invalidation so a
            // single deletion does not leave the whole continuous view blank.
            _pageManager.InvalidateCache();
            if (ContinuousDisplayRefreshRequested is { } refresh)
                await refresh(Math.Clamp(continuousAnchor, 0, ActiveFileList.Count - 1));
            return;
        }

        // Find deleted positions in Images (sorted ascending)
        var removedIndices = new List<int>();
        for (int i = 0; i < Images.Count; i++)
        {
            if (deletedPaths.Contains(Images[i].FilePath))
                removedIndices.Add(i);
        }

        // Build gap-fill file paths from ActiveFileList that aren't already in Images
        int pageStart = CurrentPage * _pageManager.PageSize;
        var existingPaths = new HashSet<string>(Images.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var gapPaths = new List<string>();
        for (int i = 0; i < _pageManager.PageSize && gapPaths.Count < removedIndices.Count; i++)
        {
            int idx = pageStart + i;
            if (idx >= files.Count) break;
            var path = files[idx];
            if (existingPaths.Contains(path)) continue;
            gapPaths.Add(path);
            existingPaths.Add(path);
        }

        // Reuse existing ImageViewItem objects — mutate in-place to avoid CollectionChanged
        int reuseCount = Math.Min(removedIndices.Count, gapPaths.Count);
        var reloadItems = new List<ImageViewItem>();
        for (int i = 0; i < reuseCount; i++)
        {
            var item = Images[removedIndices[i]];
            item.FilePath = gapPaths[i];
            item.FileName = System.IO.Path.GetFileName(gapPaths[i]);
            item.Tags = GetTagsForFile(gapPaths[i]);
            item.IsLoading = true;
            item.IsLoaded = false;
            item.IsSelected = false;
            item.ThumbnailData = null;
            item.Width = 1;
            item.Height = 1;
            reloadItems.Add(item);
        }

        // If more deleted than gap-fill, remove extras
        for (int i = removedIndices.Count - 1; i >= reuseCount; i--)
            Images.RemoveAt(removedIndices[i]);

        // If more gap-fill than deleted, add new items at the end
        for (int i = reuseCount; i < gapPaths.Count; i++)
        {
            var path = gapPaths[i];
            var item = new ImageViewItem
            {
                FilePath = path,
                FileName = System.IO.Path.GetFileName(path),
                Tags = GetTagsForFile(path),
                IsLoading = true
            };
            Images.Add(item);
            reloadItems.Add(item);
        }

        // Load thumbnails for gap-fill items
        if (reloadItems.Count > 0)
            await _pageManager.LoadThumbnailsForItemsAsync(reloadItems);

        // Clear all cached pages except current (stale after deletion), then store current
        _pageManager.InvalidateCacheExceptPage(CurrentPage, Images.ToList());
        }
        finally
        {
            _isNavigating = false;
        }
    }


    // ==================== Sort ====================

    public string SortMenuHeader => $"排序：{GetSortFieldLabel(CurrentSortOrder)} {(IsSortAscending ? "↑" : "↓")}";
    public bool IsSortScopeEnabled => ShowAllSubfolders;
    public bool IsAllFilesSortScope => CurrentSortScope == ImageSortScope.AllFiles;
    public bool IsFolderGroupedSortScope => CurrentSortScope == ImageSortScope.FolderGrouped;
    public bool IsSortByFileName => CurrentSortOrder is ImageSortOrder.FileNameAsc or ImageSortOrder.FileNameDesc;
    public bool IsSortByModified => CurrentSortOrder is ImageSortOrder.ModifiedAsc or ImageSortOrder.ModifiedDesc;
    public bool IsSortByFileSize => CurrentSortOrder is ImageSortOrder.FileSizeAsc or ImageSortOrder.FileSizeDesc;
    public bool IsSortByResolution => CurrentSortOrder is ImageSortOrder.ResolutionAsc or ImageSortOrder.ResolutionDesc;
    public bool IsSortAscending => CurrentSortOrder is ImageSortOrder.FileNameAsc or ImageSortOrder.ModifiedAsc or
        ImageSortOrder.FileSizeAsc or ImageSortOrder.ResolutionAsc;
    public bool IsSortDescending => !IsSortAscending;

    partial void OnCurrentSortOrderChanged(ImageSortOrder value) => NotifySortMenuState();
    partial void OnCurrentSortScopeChanged(ImageSortScope value) => NotifySortMenuState();

    private void NotifySortMenuState()
    {
        OnPropertyChanged(nameof(SortMenuHeader));
        OnPropertyChanged(nameof(IsAllFilesSortScope));
        OnPropertyChanged(nameof(IsFolderGroupedSortScope));
        OnPropertyChanged(nameof(IsSortByFileName));
        OnPropertyChanged(nameof(IsSortByModified));
        OnPropertyChanged(nameof(IsSortByFileSize));
        OnPropertyChanged(nameof(IsSortByResolution));
        OnPropertyChanged(nameof(IsSortAscending));
        OnPropertyChanged(nameof(IsSortDescending));
    }

    [RelayCommand]
    private async Task SetSortScope(string? scope)
    {
        if (!ShowAllSubfolders || !Enum.TryParse<ImageSortScope>(scope, true, out var parsed) ||
            parsed == CurrentSortScope)
            return;

        CurrentSortScope = parsed;
        await SortImagesAsync(CurrentSortOrder);
    }

    [RelayCommand]
    private async Task SetSortField(string? field)
    {
        var ascending = IsSortAscending;
        var order = field switch
        {
            "FileName" => ascending ? ImageSortOrder.FileNameAsc : ImageSortOrder.FileNameDesc,
            "Modified" => ascending ? ImageSortOrder.ModifiedAsc : ImageSortOrder.ModifiedDesc,
            "FileSize" => ascending ? ImageSortOrder.FileSizeAsc : ImageSortOrder.FileSizeDesc,
            "Resolution" => ascending ? ImageSortOrder.ResolutionAsc : ImageSortOrder.ResolutionDesc,
            _ => CurrentSortOrder
        };
        if (order != CurrentSortOrder)
            await SortImagesAsync(order);
    }

    [RelayCommand]
    private async Task SetSortDirection(string? direction)
    {
        var ascending = string.Equals(direction, "Ascending", StringComparison.OrdinalIgnoreCase);
        var descending = string.Equals(direction, "Descending", StringComparison.OrdinalIgnoreCase);
        if ((!ascending && !descending) || ascending == IsSortAscending)
            return;

        var order = CurrentSortOrder switch
        {
            ImageSortOrder.FileNameAsc or ImageSortOrder.FileNameDesc =>
                ascending ? ImageSortOrder.FileNameAsc : ImageSortOrder.FileNameDesc,
            ImageSortOrder.ModifiedAsc or ImageSortOrder.ModifiedDesc =>
                ascending ? ImageSortOrder.ModifiedAsc : ImageSortOrder.ModifiedDesc,
            ImageSortOrder.FileSizeAsc or ImageSortOrder.FileSizeDesc =>
                ascending ? ImageSortOrder.FileSizeAsc : ImageSortOrder.FileSizeDesc,
            _ => ascending ? ImageSortOrder.ResolutionAsc : ImageSortOrder.ResolutionDesc
        };
        await SortImagesAsync(order);
    }

    public async Task SortImagesAsync(ImageSortOrder order)
    {
        CurrentSortOrder = order;
        AppSettings.ImageSortOrder = order.ToString();
        AppSettings.ImageSortScope = CurrentSortScope.ToString();
        var sortVersion = BeginSortRequest();
        var sortToken = _sortCts.Token;
        await SaveSettingsAsync();
        if (sortToken.IsCancellationRequested)
            return;

        var folder = CurrentFolder;
        var showAllSubfolders = ShowAllSubfolders;
        var sortScope = CurrentSortScope;
        var source = _allFiles;
        var mutationVersion = _fileListMutationVersion;
        var sorted = source.ToList();
        FileSortSnapshot sortSnapshot;
        List<string> searchSource;
        List<string>? sortedSearch;
        try
        {
            sortSnapshot = await BuildFileSortSnapshotAsync(
                sorted,
                showAllSubfolders,
                mutationVersion,
                useFolderQuery: true,
                sortToken).ConfigureAwait(false);
            searchSource = _tagSearch.SearchResultFiles;
            sortedSearch = _similarityScores.Count == 0 ? searchSource.ToList() : null;
            var sortMainTask = SortFilesAsync(
                sorted, order, showAllSubfolders, sortScope, sortSnapshot, sortToken);
            Task sortSearchTask = Task.CompletedTask;
            if (sortedSearch is not null)
            {
                var searchSnapshot = sortSnapshot.Covers(
                    sortedSearch, folder, showAllSubfolders, mutationVersion)
                    ? sortSnapshot
                    : await BuildFileSortSnapshotAsync(
                        sortedSearch,
                        showAllSubfolders,
                        mutationVersion,
                        useFolderQuery: false,
                        sortToken).ConfigureAwait(false);
                sortSearchTask = SortFilesAsync(
                    sortedSearch, order, showAllSubfolders, sortScope, searchSnapshot, sortToken);
            }
            await Task.WhenAll(sortMainTask, sortSearchTask);
        }
        catch (OperationCanceledException) when (sortToken.IsCancellationRequested)
        {
            return;
        }
        if (!ReferenceEquals(source, _allFiles) || mutationVersion != _fileListMutationVersion ||
            sortVersion != Volatile.Read(ref _sortRequestVersion) ||
            !string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase) ||
            CurrentSortOrder != order || CurrentSortScope != sortScope || ShowAllSubfolders != showAllSubfolders) return;
        PublishAllFiles(sorted, sortSnapshot, contentChanged: false);
        // Similarity result order is its relevance rank; only ordinary Tag results use file sorting.
        if (sortedSearch is not null && ReferenceEquals(searchSource, _tagSearch.SearchResultFiles))
            _tagSearch.SearchResultFiles = sortedSearch;
        await RebuildDisplayFilterAsync(CurrentPage);

        var scopeLabel = showAllSubfolders
            ? (sortScope == ImageSortScope.AllFiles ? "全部文件混排" : "按文件夹分组") + " · "
            : string.Empty;
        StatusText = $"排序：{scopeLabel}{GetSortFieldLabel(order)} {(IsSortAscending ? "↑" : "↓")}";
    }

    private static string GetSortFieldLabel(ImageSortOrder order) => order switch
    {
        ImageSortOrder.FileNameAsc or ImageSortOrder.FileNameDesc => "文件名",
        ImageSortOrder.ModifiedAsc or ImageSortOrder.ModifiedDesc => "修改时间",
        ImageSortOrder.FileSizeAsc or ImageSortOrder.FileSizeDesc => "文件大小",
        _ => "分辨率"
    };

    [RelayCommand]
    private async Task ClearFilter()
    {
        ResetTagSuggestionContext();
        _tagSearch.ClearSearchResults();
        TagSearchText = string.Empty;
        CurrentTagFilter = string.Empty;
        DisplayFilter = new();
        IsShowingSearchResult = false;
        _similarityScores.Clear();
        _similarityMatchKinds.Clear();
        NotifyFolderSectionSearchStateChanged();
        await RebuildDisplayFilterAsync();
    }

    // ==================== Similar Image Search ====================

    [RelayCommand]
    private async Task SearchSimilarAsync(string filePath)
    {
        var mode = SimilaritySearchModeValues[
            Math.Clamp(SelectedSimilarityMode, 0, SimilaritySearchModeValues.Length - 1)];
        var (requestVersion, requestCts) = BeginSearchRequest();
        StatusText = "正在搜索相似图片...";

        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(CurrentPage);

        try
        {
            var candidates = (await GetSearchScopeFilesAsync()).ToList();
            requestCts.Token.ThrowIfCancellationRequested();
            AppLogger.Info($"SimilarSearch scope={SearchScope} candidates={candidates.Count} mode={mode} file={filePath}");
            var resultLimit = Math.Clamp(AppSettings.SimilaritySearchResultLimit, 1, 500);
            var results = await _similarService.SearchByImageAsync(
                filePath, candidates, mode, resultLimit, requestCts.Token);
            await ApplySimilarityResultsAsync(
                results,
                mode,
                () => IsCurrentSearchRequest(requestVersion, requestCts));
            if (mode == SimilaritySearchMode.Perceptual)
            {
                var searchPaths = candidates.ToArray();
                RunBackgroundSafe("similar-search-hash-repair", CurrentFolderWorkToken,
                    async token =>
                    {
                        await AutoTagRuntimeState.WaitForIdleAsync(token);
                        token.ThrowIfCancellationRequested();
                        await PrecomputeHashesAsync(token, paths: searchPaths);
                    });
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSearchRequest(requestVersion, requestCts))
                StatusText = "搜索已取消";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(requestVersion, requestCts))
                StatusText = $"相似搜索失败: {ex.Message}";
        }
        finally
        {
            CompleteSearchRequest(requestCts);
        }
    }

    public void SelectSimilarityMode(SimilaritySearchMode mode)
    {
        var index = Array.IndexOf(SimilaritySearchModeValues, mode);
        if (index >= 0)
            SelectedSimilarityMode = index;
    }

    [RelayCommand]
    private async Task SearchNaturalLanguageAsync()
    {
        var query = NaturalLanguageQuery.Trim();
        if (query.Length == 0)
        {
            StatusText = "请输入自然语言描述";
            return;
        }

        var (requestVersion, requestCts) = BeginSearchRequest();
        StatusText = "正在进行自然语言搜索...";
        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(CurrentPage);

        try
        {
            var candidates = (await GetSearchScopeFilesAsync()).ToList();
            requestCts.Token.ThrowIfCancellationRequested();
            var resultLimit = Math.Clamp(AppSettings.SimilaritySearchResultLimit, 1, 500);
            var results = await _similarService.SearchByTextAsync(
                query, candidates, resultLimit, requestCts.Token);
            await ApplySimilarityResultsAsync(
                results,
                SimilaritySearchMode.Semantic,
                () => IsCurrentSearchRequest(requestVersion, requestCts));
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSearchRequest(requestVersion, requestCts))
                StatusText = "搜索已取消";
        }
        catch (Exception ex)
        {
            if (IsCurrentSearchRequest(requestVersion, requestCts))
                StatusText = $"自然语言搜索失败: {ex.Message}";
        }
        finally
        {
            CompleteSearchRequest(requestCts);
        }
    }

    [RelayCommand]
    private void StopSearch()
    {
        Volatile.Read(ref _searchCts)?.Cancel();
    }

    private (int Version, CancellationTokenSource Cancellation) BeginSearchRequest()
    {
        var requestVersion = Interlocked.Increment(ref _searchRequestVersion);
        var requestCts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, requestCts);
        previous?.Cancel();
        previous?.Dispose();
        return (requestVersion, requestCts);
    }

    private bool IsCurrentSearchRequest(int requestVersion, CancellationTokenSource requestCts)
    {
        return requestVersion == Volatile.Read(ref _searchRequestVersion) &&
               ReferenceEquals(Volatile.Read(ref _searchCts), requestCts);
    }

    private void CompleteSearchRequest(CancellationTokenSource requestCts)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, requestCts), requestCts))
            requestCts.Dispose();
    }

    private async Task ApplySimilarityResultsAsync(
        IReadOnlyList<SimilaritySearchResult> results,
        SimilaritySearchMode searchMode,
        Func<bool> isCurrentSearch)
    {
        if (!isCurrentSearch())
            return;

        var useRankedResults = searchMode != SimilaritySearchMode.Perceptual ||
                               AppSettings.PerceptualSearchResultMode == PerceptualSearchResultMode.Ranked;
        var displayedResults = searchMode == SimilaritySearchMode.Perceptual && !useRankedResults
            ? results.Where(result => result.MatchKind == SimilarityMatchKind.PerceptualStrict).ToList()
            : results.ToList();

        _similarityScores.Clear();
        _similarityMatchKinds.Clear();
        foreach (var result in displayedResults)
        {
            _similarityScores[result.FilePath] = result.Score;
            _similarityMatchKinds[result.FilePath] = result.MatchKind;
        }
        NotifyFolderSectionSearchStateChanged();
        _tagSearch.SearchResultFiles = displayedResults.Select(result => result.FilePath).ToList();
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultInfo));

        if (_tagSearch.SearchResultFiles.Count == 0)
        {
            if (useRankedResults)
            {
                IsShowingSearchResult = true;
                await RebuildDisplayFilterAsync();
                if (!isCurrentSearch())
                    return;
                ScrollSearchResultsToTopRequested?.Invoke();
            }
            else if (IsShowingSearchResult)
            {
                IsShowingSearchResult = false;
                await RebuildDisplayFilterAsync();
                if (!isCurrentSearch())
                    return;
            }

            StatusText = searchMode switch
            {
                SimilaritySearchMode.Perceptual when !useRankedResults => "除原图外未找到严格相似图片",
                SimilaritySearchMode.Perceptual => "未找到达到基础相似度的图片",
                _ => "未找到结果；请先在设置中建立对应向量索引"
            };
            return;
        }

        StatusText = FormatSimilarityStatus(displayedResults, searchMode);
        _currentResultIndex = 0;
        if (useRankedResults)
            await ShowRankedSimilarityResultsAsync(isCurrentSearch);
        else
            await ShowJumpSimilarityResultsAsync(isCurrentSearch);
    }

    private async Task ShowRankedSimilarityResultsAsync(Func<bool> isCurrentSearch)
    {
        IsShowingSearchResult = true;
        Interlocked.Increment(ref _resultNavigationVersion);
        await RebuildDisplayFilterAsync();
        if (!isCurrentSearch())
            return;

        _currentResultIndex = 0;
        if (Images.Count > 0)
        {
            ReplaceSelectedFiles(new[] { Images[0].FilePath });
        }

        OnPropertyChanged(nameof(SearchResultInfo));
        ScrollSearchResultsToTopRequested?.Invoke();
    }

    private async Task ShowJumpSimilarityResultsAsync(Func<bool> isCurrentSearch)
    {
        if (IsShowingSearchResult)
        {
            IsShowingSearchResult = false;
            await RebuildDisplayFilterAsync(renderPage: false);
            if (!isCurrentSearch())
                return;
        }
        else
        {
            // Publish the filtered snapshot without rendering the old page.
            // NavigateToResultAsync will render the target page exactly once.
            await RebuildDisplayFilterAsync(renderPage: false);
            if (!isCurrentSearch()) return;
        }

        await NavigateToResultAsync(forcePageReload: true);
    }

    private List<string> ResultNavigationFiles =>
        IsShowingSearchResult ? ActiveFileList : _filteredNavigationFiles;

    private string FormatSearchResultInfo()
    {
        var files = ResultNavigationFiles;
        if (files.Count == 0) return "当前筛选下没有相似结果";
        var scoreText = _currentResultIndex >= 0 && _currentResultIndex < files.Count &&
                        _similarityScores.TryGetValue(files[_currentResultIndex], out var score)
            ? $"  相似度 {score:0.000}"
            : string.Empty;
        var matchKindText = _currentResultIndex >= 0 && _currentResultIndex < files.Count &&
                            _similarityMatchKinds.TryGetValue(files[_currentResultIndex], out var matchKind)
            ? matchKind switch
            {
                SimilarityMatchKind.PerceptualStrict => "  严格近重复",
                SimilarityMatchKind.PerceptualFallback => "  宽松候选",
                _ => string.Empty
            }
            : string.Empty;
        return $"找到 {files.Count} 张相似图片  第 {_currentResultIndex + 1}/{files.Count}{matchKindText}{scoreText}";
    }

    private static string FormatSimilarityStatus(
        IReadOnlyCollection<SimilaritySearchResult> results,
        SimilaritySearchMode searchMode)
    {
        if (searchMode != SimilaritySearchMode.Perceptual)
            return $"找到 {results.Count} 张相似图片";
        var strictCount = results.Count(result => result.MatchKind == SimilarityMatchKind.PerceptualStrict);
        var fallbackCount = results.Count - strictCount;
        return fallbackCount > 0
            ? $"找到 {results.Count} 张图片（严格 {strictCount}，宽松候选 {fallbackCount}）"
            : $"找到 {strictCount} 张严格相似图片";
    }

    [RelayCommand]
    private async Task PrevResult()
    {
        var total = ResultNavigationFiles.Count;
        if (total == 0) return;
        _currentResultIndex = (_currentResultIndex - 1 + total) % total;
        await NavigateToResultAsync();
    }

    [RelayCommand]
    private async Task NextResult()
    {
        var total = ResultNavigationFiles.Count;
        if (total == 0) return;
        _currentResultIndex = (_currentResultIndex + 1) % total;
        await NavigateToResultAsync();
    }

    private async Task NavigateToResultAsync(bool forcePageReload = false)
    {
        // === 阶段0：版本控制 ===
        var navigationVersion = Interlocked.Increment(ref _resultNavigationVersion);
        bool IsCurrentNavigation() => navigationVersion == Volatile.Read(ref _resultNavigationVersion);

        var files = ResultNavigationFiles;
        if (files.Count == 0) return;

        var targetPath = files[_currentResultIndex];
        var targetDir = Path.GetDirectoryName(targetPath) ?? "";

        // === 阶段1：展开并高亮目标文件夹 ===
        bool needSwitchFolder = !IsShowingSearchResult && !ShowAllSubfolders &&
                                !string.Equals(CurrentFolder, targetDir, StringComparison.OrdinalIgnoreCase);
        bool shouldScroll = ShowAllSubfolders || needSwitchFolder;

        var targetNode = await ExpandAndHighlightFolderAsync(
            targetDir,
            syncSelection: false,
            forceScroll: shouldScroll,
            IsCurrentNavigation);

        if (!IsCurrentNavigation()) return;
        if (targetNode == null)
        {
            StatusText = "无法展开到目标文件夹";
            return;
        }

        // === 阶段2：切换文件夹（轻量级 — 不调用 LoadFolderAsync，避免销毁缓存和搜索状态）===
        if (needSwitchFolder)
        {
            if (!Directory.Exists(targetDir))
            {
                StatusText = "目标文件夹不存在";
                return;
            }

            // 轻量级文件枚举：仅加载目标文件夹的文件列表，不清除缓存和搜索状态
            var exts = FileTypeConstants.AllMediaExtensions;
            var folderFiles = await Task.Run(() =>
            {
                try
                {
                    return Directory.EnumerateFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => exts.Contains(Path.GetExtension(f)))
                        .ToList();
                }
                catch { return new List<string>(); }
            });

            if (!IsCurrentNavigation()) return;

            if (folderFiles.Count == 0)
            {
                StatusText = "该文件夹内没有图片文件";
                return;
            }

            // 更新文件列表并跳到目标文件所在页
            PublishAllFiles(folderFiles);
            CurrentFolder = targetDir;
            // Refilter the new folder; preserve the selected result after filter publication resets navigation.
            if (!await UpdateDisplayFilterAsync(preserveNavigation: true) || !IsCurrentNavigation()) return;
            _currentResultIndex = Math.Max(0, ResultNavigationFiles.FindIndex(f =>
                string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase)));
            SetDisplayPaging();
            _pageManager.InvalidateCache();
            forcePageReload = true;

            // 更新文件夹树选中状态（抑制 SelectionChanged 重入）
            _isProgrammaticFolderSelection = true;
            SelectedFolderNode = targetNode;
            AppSettings.LastFolder = targetDir;
            _isProgrammaticFolderSelection = false;
        }

        // === 阶段3：定位并选中目标图片 ===
        var indexInList = ActiveFileList.FindIndex(f =>
            string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));

        if (indexInList < 0)
        {
            StatusText = $"目标图片不在当前列表中";
            return;
        }

        // 切换到目标页
        int targetPage = indexInList / _pageManager.PageSize;
        if (forcePageReload || targetPage != CurrentPage)
        {
            await ShowPageAsync(targetPage);
            if (!IsCurrentNavigation()) return;
        }

        // 选中并滚动到图片
        await SelectAndScrollToImageAsync(targetPath, IsCurrentNavigation);
        if (!IsCurrentNavigation()) return;

        OnPropertyChanged(nameof(SearchResultInfo));
    }

    /// <summary>选中指定图片并滚动到可见区域</summary>
    private void SelectAndScrollToImage(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        ReplaceSelectedFiles(new[] { targetPath });
        _dispatcher.InvokeAsync(
            () => ScrollToSelectedRequested?.Invoke());
    }

    /// <summary>
    /// 等待树节点的UI容器生成完成
    /// </summary>
    private async Task<bool> WaitForTreeContainerAsync(FolderTreeNode node, Func<bool>? isCurrent = null)
    {
        const int maxAttempts = 20;  // 增加到20次，1秒总等待
        const int delayMs = 50;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (isCurrent?.Invoke() == false) return false;

            var ready = await _dispatcher.InvokeAsync(() =>
            {
                // 改进检测：节点已展开且子节点已加载
                return TreeScrollToNodeRequested?.GetInvocationList().Length > 0
                       && node.IsExpanded
                       && node.ChildrenLoaded;
            });

            if (ready)
            {
                await Task.Delay(100);  // 更长的稳定时间
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;  // 超时返回false
    }

    /// <summary>
    /// 等待 Images 集合更新完成。
    /// 如果 _imagesUpdatedTcs 已在外部提前创建（如在 LoadFolderAsync 之前），则复用已存在的 TCS，
    /// 避免 PageChanged 事件在 LoadFolderAsync 内触发时 TCS 尚未创建的竞态问题。
    /// </summary>
    private async Task WaitForImagesUpdatedAsync(string targetPath, Func<bool>? isCurrent = null)
    {
        // 复用已存在的 TCS（由 NavigateToResultAsync 在 LoadFolderAsync 之前创建），否则创建新的
        if (_imagesUpdatedTcs == null)
            _imagesUpdatedTcs = new TaskCompletionSource<bool>();

        using var cts = new CancellationTokenSource(5000); // 5秒超时
        using var registration = cts.Token.Register(() =>
            _imagesUpdatedTcs.TrySetCanceled());

        try
        {
            // 等待 PageChanged 事件触发（可能已经在 LoadFolderAsync 内触发过了）
            await _imagesUpdatedTcs.Task;

            // 额外延迟确保UI绑定完成
            await Task.Delay(50);

            // 再次检查导航版本
            if (isCurrent?.Invoke() == false) return;
        }
        catch (OperationCanceledException)
        {
            // 超时或取消
            StatusText = "图片加载超时";
        }
        finally
        {
            _imagesUpdatedTcs = null;
        }
    }

    /// <summary>
    /// 异步选中并滚动到指定图片
    /// </summary>
    private async Task SelectAndScrollToImageAsync(string targetPath, Func<bool>? isCurrent = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        // Continuous display does not materialize an ImageViewItem for every
        // result. Keep navigation path-based so both display modes can locate
        // the target before its container exists.
        ReplaceSelectedFiles(new[] { targetPath });

        if (DisplayMode == ImageDisplayMode.Continuous)
        {
            // ApplyDisplayModeAsync has already published the geometry and
            // positioned the virtualized panel at the target index. Dispatch
            // the selection notification immediately; fixed sleeps only delay
            // continuous navigation and do not improve panel readiness.
            await _dispatcher.InvokeAsync(
                () => { ScrollToSelectedRequested?.Invoke(); });
            return;
        }

        // 跨文件夹首次跳转时 Images 集合刚被 PageChanged 替换为新实例，
        // Avalonia 的 ItemsControl 需要至少一次 layout pass 才能创建 child 容器。
        // 等待一段时间，再由 View 端的 OnScrollToSelected 自带重试循环兜底。
        await Task.Delay(80);
        if (isCurrent?.Invoke() == false) return;

        await _dispatcher.InvokeAsync(
            () => { ScrollToSelectedRequested?.Invoke(); });

        await Task.Delay(100);
    }

    [RelayCommand]
    private async Task BackFromSearch()
    {
        if (!IsShowingSearchResult) return;
        ResetTagSuggestionContext();
        _tagSearch.ClearSearchResults();
        IsShowingSearchResult = false;
        _similarityScores.Clear();
        _similarityMatchKinds.Clear();
        NotifyFolderSectionSearchStateChanged();
        OnPropertyChanged(nameof(HasSearchResults));

        var page = _pageManager.TryRestorePreSearchState(out var savedPage) ? savedPage : 0;
        await RebuildDisplayFilterAsync(page);
        ScrollRestoreRequested?.Invoke();
    }


}
