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
    // ==================== Folder Commands ====================

    [RelayCommand]
    private async Task AddFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        await _folderRepo.AddAsync(folderPath);
        var info = await _folderRepo.GetByPathAsync(folderPath);
        if (info != null && FolderTree.All(f => !string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            var node = new FolderTreeNode
            {
                Path = info.Path, DisplayName = info.DisplayName, Alias = info.Alias, DbId = info.Id
            };
            await node.EnsureExpanderVisibleAsync();
            FolderTree.Add(node);
        }

        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolderNode == null) return;

        if (SelectedFolderNode.DbId > 0 || !string.IsNullOrEmpty(SelectedFolderNode.Path))
        {
            try
            {
                if (SelectedFolderNode.DbId > 0)
                {
                    var unlinked = await _metaRepo.UnlinkFolderIdAsync(SelectedFolderNode.DbId);
                    AppLogger.Info($"RemoveFolder unlink folderId={SelectedFolderNode.DbId} path={SelectedFolderNode.Path} count={unlinked}");
                }

                var metas = await _metaRepo.GetByFolderAsync(SelectedFolderNode.Path);
                foreach (var meta in metas)
                    _thumbCache.InvalidateThumbnail(meta.FilePath);
            }
            catch { }
        }

        await _folderRepo.RemoveAsync(SelectedFolderNode.Path);
        FolderTree.Remove(SelectedFolderNode);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task ArchiveFolderAsync()
    {
        var folder = SelectedFolderNode;
        if (folder == null || folder.DbId <= 0) return;

        await _folderRepo.SetArchivedAsync(folder.DbId, true);
        if (string.Equals(CurrentFolder, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            CurrentFolder = string.Empty;
            Images.Clear();
            PublishAllFiles(new());
            StatusText = "文件夹已归档";
        }

        FolderTree.Remove(folder);
        SelectedFolderNode = null;
        await SaveSettingsAsync();
    }

    public async Task RefreshFolderTreeAsync()
    {
        var folders = await _folderRepo.GetAllAsync();
        var nodes = folders.Where(f => !f.IsArchived).Select(f => new FolderTreeNode
        {
            Path = f.Path, DisplayName = f.DisplayName, Alias = f.Alias, DbId = f.Id
        }).ToList();
        await Task.WhenAll(nodes.Select(n => n.EnsureExpanderVisibleAsync()));
        FolderTree = new ObservableCollection<FolderTreeNode>(nodes);
    }

    [RelayCommand]
    private async Task SelectFolderSuggestion(FolderTreeNode folder)
    {
        FolderSearchText = string.Empty;
        IsFolderSearchPopupOpen = false;

        // 传递 syncSelection: true，让 ExpandAndHighlightFolderAsync 内部处理滚动
        await ExpandAndHighlightFolderAsync(folder.Path, syncSelection: true);

        if (!Directory.Exists(folder.Path))
        {
            StatusText = $"文件夹路径已变更: {folder.Path}";
            return;
        }

        _isProgrammaticFolderSelection = true;
        SelectedFolderNode = folder;
        AppSettings.LastFolder = folder.Path;
        await LoadFolderAsync(folder.Path);
        await SaveSettingsAsync();
        _isProgrammaticFolderSelection = false;

        // 删除显式滚动调用，避免双重滚动
        // TreeScrollToNodeRequested?.Invoke(folder);
    }

    internal bool _isProgrammaticFolderSelection;

    public async Task UpdateFolderAliasAsync(string folderPath, string? alias)
    {
        await _folderRepo.UpdateAliasAsync(folderPath, alias);
        var node = FindNodeByPath(folderPath);
        if (node != null)
        {
            node.Alias = alias;
            node.DisplayName = alias ?? System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        }
    }

    /// <summary>Relocate a folder whose path changed externally. Updates all paths in DB.</summary>
    public async Task RelocateFolderAsync(long folderId, string newFolderPath)
    {
        await _folderRepo.RelocateFolderAsync(folderId, newFolderPath);
        var node = FindNodeByPath(newFolderPath) ?? FindNodeByDbId(FolderTree, folderId);
        if (node != null)
            node.Path = newFolderPath;
    }

    private static FolderTreeNode? FindNodeByDbId(IEnumerable<FolderTreeNode> nodes, long id)
    {
        foreach (var n in nodes)
        {
            if (n.DbId == id) return n;
            var found = FindNodeByDbId(n.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Returns true if folder needs relocation (path doesn't exist on disk)</summary>
    public bool NeedsRelocation(FolderTreeNode folder) => folder.DbId > 0 && !Directory.Exists(folder.Path);

    public async Task SelectFolderAsync(FolderTreeNode? folder)
    {
        if (folder == null) return;

        if (!Directory.Exists(folder.Path))
        {
            StatusText = $"文件夹路径已变更: {folder.Path}";
            return;
        }

        SelectedFolderNode = folder;
        AppSettings.LastFolder = folder.Path;
        await LoadFolderAsync(folder.Path);
        await SaveSettingsAsync();
    }

    // ==================== Folder Loading ====================

    private int GetStartPageForLoadedFolder(string? preferredFilePath, int? lastPage)
    {
        if (!string.IsNullOrEmpty(preferredFilePath))
        {
            var preferredIndex = ActiveFileList.FindIndex(f =>
                string.Equals(f, preferredFilePath, StringComparison.OrdinalIgnoreCase));
            if (preferredIndex >= 0)
                return preferredIndex / _pageManager.PageSize;
        }

        return lastPage.HasValue && lastPage.Value < TotalPages ? lastPage.Value : 0;
    }

    public async Task LoadFolderAsync(string folder, string? preferredFilePath = null, Func<bool>? isCurrent = null)
    {
        var sw = Stopwatch.StartNew();
        PerfLogger.Log($"[LoadFolder] START folder={Path.GetFileName(folder)} showAll={ShowAllSubfolders}");
        var requestVersion = BeginFolderViewRequest();
        var folderWorkToken = BeginFolderWorkScope(folder);
        var showAllSubfolders = ShowAllSubfolders;

        if (!Directory.Exists(folder))
        {
            // Try to relocate if this was a previously imported folder
            var existingFolder = await Task.Run(() => _folderRepo.GetByPathAsync(folder));
            if (existingFolder != null)
            {
                StatusText = "文件夹路径已变更，请在侧边栏重新点击该文件夹以重定位";
            }
            else
            {
                StatusText = "文件夹不存在";
            }
            return;
        }

        ResetTagSuggestionContext();
        _tagSearch.ClearSearchResults();
        IsShowingSearchResult = false;
        Images.Clear();
        PublishAllFiles(new());
        _displayFilteredFiles = new();
        _filteredNavigationFiles = new();
        DisplayFilterSourceCount = 0;
        DisplayFilterUnknownCount = 0;
        NotifyDisplayFilterState();
        _pageManager.InvalidateCache();
        _phashCache.Clear();
        lock (_tagCacheLock) { _tagCacheByPath.Clear(); }
        _metaCache.Clear();
        BackgroundStatusText = "";
        CurrentPage = 0;
        TotalPages = 0;
        CurrentFolder = folder;
        _ = RefreshFolderItemsAsync(folderWorkToken);
        OnPropertyChanged(nameof(FolderBreadcrumbs));
        StartWatchingCurrentFolder();
        PerfLogger.Log($"[LoadFolder] 1-cleanup done {sw.ElapsedMilliseconds}ms");
        AppLogger.Memory($"LoadFolder.Cleanup {Path.GetFileName(folder)}");

        // 重置空闲定时器：用户停止切换 5 秒后执行维护任务
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, OnIdleTimerTick);
        _idleTimer.Start();

        await Task.Yield();
        if (folderWorkToken.IsCancellationRequested) return;
        PerfLogger.Log($"[LoadFolder] 2-yield done {sw.ElapsedMilliseconds}ms");

        // 线程池诊断
        ThreadPool.GetAvailableThreads(out var w, out var io);
        ThreadPool.GetMaxThreads(out var mw, out var mio);
        PerfLogger.Log($"[ThreadPool] workers={mw-w}/{mw} io-threads={mio-io}/{mio}");

        if (isCurrent?.Invoke() == false)
            return;

        if (showAllSubfolders)
        {
            await RebuildFileListAsync(requestVersion, folder, true, folderWorkToken);
            PerfLogger.Log($"[SortPerf] total_time_to_first_visible_ms={sw.ElapsedMilliseconds} count={_allFiles.Count}");
            PerfLogger.Log($"[LoadFolder] END (showAll) total={_allFiles.Count} elapsed={sw.ElapsedMilliseconds}ms");
            return;
        }

        // Check if this folder already has FolderId markers in DB
        PerfLogger.Log($"[LoadFolder] 3-db-query-folder start {sw.ElapsedMilliseconds}ms");
        var folderInfo = await Task.Run(() => _folderRepo.GetByPathAsync(folder), folderWorkToken);
        PerfLogger.Log($"[LoadFolder] 3-db-query-folder done {sw.ElapsedMilliseconds}ms");
        if (folderWorkToken.IsCancellationRequested || isCurrent?.Invoke() == false)
            return;
        long? folderId = folderInfo?.Id;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();

        // 磁盘枚举先行（<1ms），DB 查询在后台异步运行，不阻塞 LoadFolderAsync
        PerfLogger.Log($"[LoadFolder] disk-enum start {sw.ElapsedMilliseconds}ms");
        List<string> loadedFiles;
        var enumerationStopwatch = Stopwatch.StartNew();
        try
        {
            loadedFiles = await Task.Run(() =>
            {
                var files = new List<string>();
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (folderWorkToken.IsCancellationRequested)
                        return files;
                    if (exts.Contains(Path.GetExtension(file).ToLower()))
                        files.Add(file);
                }
                return files;
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Memory($"[FolderWork] load canceled folder={Path.GetFileName(folder)}");
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"读取文件夹失败: {ex.Message}";
            return;
        }
        if (folderWorkToken.IsCancellationRequested || isCurrent?.Invoke() == false ||
            !IsFolderViewRequestCurrent(requestVersion, folder, showAllSubfolders)) return;
        PerfLogger.Log($"[SortPerf] enumeration_ms={enumerationStopwatch.ElapsedMilliseconds} count={loadedFiles.Count} recursive=false");
        var loadedSnapshot = await SortFilesUsingCurrentSettingsAsync(loadedFiles, showAllSubfolders, folderWorkToken);
        if (folderWorkToken.IsCancellationRequested || isCurrent?.Invoke() == false ||
            !IsFolderViewRequestCurrent(requestVersion, folder, showAllSubfolders)) return;
        PublishAllFiles(loadedFiles, loadedSnapshot);
        PerfLogger.Log($"[LoadFolder] disk-enum done count={_allFiles.Count} {sw.ElapsedMilliseconds}ms");
        AppLogger.Memory($"LoadFolder.DiskEnum count={_allFiles.Count}");

        if (!await UpdateDisplayFilterAsync()) return;
        SetDisplayPaging();
        if (ActiveFileList.Count == 0)
        {
            LoadedInfoText = "";
            StatusText = HasDisplayFilter ? "没有符合当前筛选的文件" : "该文件夹内没有媒体文件";
            return;
        }
        StatusText = DisplayFilterCountText;

        // 立即显示第一页 — 无后台 Task.Run，和 ShowAll 路径一致
        PerfLogger.Log($"[LoadFolder] A-before-getstart {sw.ElapsedMilliseconds}ms tid={Environment.CurrentManagedThreadId}");
        int startPage = GetStartPageForLoadedFolder(preferredFilePath, null);
        PerfLogger.Log($"[LoadFolder] A-before-showpage page={startPage} {sw.ElapsedMilliseconds}ms");
        PerfLogger.Log($"[LoadFolder] show-page start page={startPage} {sw.ElapsedMilliseconds}ms");
        var firstPageStopwatch = Stopwatch.StartNew();
        await ShowPageAsync(startPage);
        PerfLogger.Log($"[SortPerf] first_page_ms={firstPageStopwatch.ElapsedMilliseconds} page={startPage}");
        PerfLogger.Log($"[SortPerf] total_time_to_first_visible_ms={sw.ElapsedMilliseconds} count={_allFiles.Count}");

        PerfLogger.Log($"[LoadFolder] END total={_allFiles.Count} elapsed={sw.ElapsedMilliseconds}ms");
        AppLogger.Memory($"LoadFolder.End total={_allFiles.Count}");
    }

    /// <summary>Public wrapper for code-behind: sync current folder and refresh UI, then compute missing hashes</summary>
    public async Task SyncCurrentFolderAsync()
    {
        var folderWorkToken = CurrentFolderWorkToken;
        if (string.IsNullOrEmpty(CurrentFolder)) return;
        var fi = await _folderRepo.GetByPathAsync(CurrentFolder);
        if (fi == null) return;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();
        await SyncFolderAsync(CurrentFolder, fi.Id, exts, folderWorkToken);

        RunBackgroundSafe("sync-current-folder-hash", folderWorkToken, async token =>
        {
            await Task.Delay(3000, token);
            await PrecomputeHashesAsync(token, fi.Id);
        });
    }



    public void ClearSearchHighlight()
    {
        foreach (var root in FolderTree)
            ClearHighlightRecursive(root);
    }

    private void CaptureSelectionForNextPageRefresh()
    {
        var selected = Images
            .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.FilePath))
            .Select(i => i.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in selected)
            _selectedFilePaths.Add(path);
        _pendingSelectionRestorePaths = selected.Count > 0 ? selected : null;
    }

    private void RestorePendingSelection(IEnumerable<ImageViewItem> items)
    {
        var selectedPaths = _pendingSelectionRestorePaths;
        _pendingSelectionRestorePaths = null;
        if (selectedPaths is not { Count: > 0 }) return;

        foreach (var item in items)
        {
            if (selectedPaths.Contains(item.FilePath))
            {
                _selectedFilePaths.Add(item.FilePath);
                item.IsSelected = true;
            }
        }
    }

    private static void ClearHighlightRecursive(FolderTreeNode node)
    {
        node.IsSearchHighlight = false;
        foreach (var child in node.Children)
            ClearHighlightRecursive(child);
    }

    private bool IsPathHighlighted(string normalizedPath)
    {
        foreach (var root in FolderTree)
        {
            if (IsPathHighlightedRecursive(root, normalizedPath))
                return true;
        }

        return false;
    }

    private static bool IsPathHighlightedRecursive(FolderTreeNode node, string normalizedPath)
    {
        if (node.IsSearchHighlight &&
            string.Equals(NormalizePath(node.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
        {
            if (IsPathHighlightedRecursive(child, normalizedPath))
                return true;
        }

        return false;
    }

    public async Task<FolderTreeNode?> ExpandAndHighlightFolderAsync(
        string targetPath,
        bool syncSelection = false,
        bool forceScroll = false,
        Func<bool>? isCurrent = null)
    {
        targetPath = NormalizePath(targetPath);
        if (isCurrent?.Invoke() == false)
            return null;

        ClearSearchHighlight();

        // 标准化输入路径
        targetPath = NormalizePath(targetPath);

        // 1. 构建路径层级列表
        var parts = new List<string>();
        var current = targetPath;
        while (!string.IsNullOrEmpty(current))
        {
            parts.Insert(0, current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(NormalizePath(parent), NormalizePath(current), StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        if (parts.Count == 0)
        {
            StatusText = "路径解析失败";
            return null;
        }

        // 2. 找到路径的根节点（FolderTree 中的顶层节点）
        FolderTreeNode? rootNode = null;
        int startIdx = -1;

        // 从开头向后查找，找到第一个存在于 FolderTree 中的节点
        for (int i = 0; i < parts.Count; i++)
        {
            rootNode = FolderTree.FirstOrDefault(n =>
                string.Equals(
                    NormalizePath(n.Path),
                    NormalizePath(parts[i]),
                    StringComparison.OrdinalIgnoreCase));
            if (rootNode != null)
            {
                startIdx = i;
                break;
            }
        }

        if (rootNode == null || startIdx < 0)
        {
            // 调试信息：显示尝试匹配的路径和可用的根节点
            var availableRoots = string.Join(", ", FolderTree.Select(n => $"[{NormalizePath(n.Path)}]"));
            var attemptedPaths = string.Join(", ", parts.Select(p => $"[{NormalizePath(p)}]"));
            StatusText = $"无法找到根节点。尝试: {attemptedPaths}；可用根节点: {availableRoots}";
            return null;
        }

        // 3. 从根节点开始，逐层向下展开
        var currentNode = rootNode;

        for (int i = startIdx + 1; i < parts.Count; i++)
        {
            var targetPathAtLevel = parts[i];

            // 3.1 确保当前节点已展开并加载子节点
            if (!currentNode.IsExpanded)
            {
                currentNode.IsExpanded = true;
                if (currentNode.LoadTask != null)
                    await currentNode.LoadTask;
                if (isCurrent?.Invoke() == false)
                    return null;
            }
            else
            {
                // 节点已展开，需要确保子节点真的加载完成
                if (!currentNode.ChildrenLoaded)
                {
                    // 子节点未加载，但LoadTask可能为null（节点初始就是展开状态）
                    if (currentNode.LoadTask == null)
                    {
                        // LoadTask为null说明OnIsExpandedChanged未触发，强制重新触发
                        currentNode.IsExpanded = false;
                        await Task.Delay(1); // 确保UI处理属性变化
                        currentNode.IsExpanded = true;
                    }

                    // 现在LoadTask应该存在了，等待它完成
                    if (currentNode.LoadTask != null)
                        await currentNode.LoadTask;
                }
                else if (currentNode.LoadTask != null && !currentNode.LoadTask.IsCompleted)
                {
                    // 已加载但可能有正在进行的任务
                    await currentNode.LoadTask;
                }

                if (isCurrent?.Invoke() == false)
                    return null;
            }

            // 3.2 在当前节点的直接子节点中查找下一级路径
            var nextNode = currentNode.Children.FirstOrDefault(child =>
                string.Equals(
                    NormalizePath(child.Path),
                    NormalizePath(targetPathAtLevel),
                    StringComparison.OrdinalIgnoreCase));

            if (nextNode == null)
            {
                // 找不到子节点，可能是路径不存在或未加载
                StatusText = $"无法展开到 {Path.GetFileName(targetPathAtLevel)}";
                return null;
            }

            currentNode = nextNode;
        }

        // 4. 到达目标节点，高亮并滚动
        if (currentNode != null)
        {
            currentNode.IsSearchHighlight = true;

            if (syncSelection)
            {
                _isProgrammaticFolderSelection = true;
                SelectedFolderNode = currentNode;
                _isProgrammaticFolderSelection = false;
            }

            // 滚动条件：
            // 1. forceScroll=true: 强制滚动（以图搜图切换场景）
            // 2. syncSelection=true: 需要选中节点，滚动确保可见
            if (forceScroll || syncSelection)
            {
                // The view owns any container-materialization retries. Waiting here
                // serialized folder navigation behind up to several seconds of UI
                // delays before media loading could start.
                await _dispatcher.InvokeAsync(() => TreeScrollToNodeRequested?.Invoke(currentNode));
            }
        }

        return currentNode;
    }

    public Task RebuildFileListAsync()
    {
        var requestVersion = BeginFolderViewRequest();
        return RebuildFileListAsync(requestVersion, CurrentFolder, ShowAllSubfolders, CurrentFolderWorkToken);
    }

    private async Task RebuildFileListAsync(
        int requestVersion,
        string folderPath,
        bool showAllSubfolders,
        CancellationToken folderWorkToken = default)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        if (folderWorkToken.IsCancellationRequested)
            return;
        var enumerationStopwatch = Stopwatch.StartNew();
        FileEnumerationResult rebuildEnumResult;
        if (showAllSubfolders)
        {
            rebuildEnumResult = await EnumerateMediaFilesAsync(folderPath, recursive: true, folderWorkToken);
        }
        else
        {
            rebuildEnumResult = await Task.Run(() =>
            {
                var files = new List<string>();
                foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (folderWorkToken.IsCancellationRequested)
                        return new FileEnumerationResult(files, true);
                    if (FileTypeConstants.IsMediaFile(file))
                        files.Add(file);
                }
                return new FileEnumerationResult(files, folderWorkToken.IsCancellationRequested);
            }, CancellationToken.None);
        }
        if (rebuildEnumResult.Canceled)
            return;
        var rebuiltFiles = rebuildEnumResult.Files;
        PerfLogger.Log($"[SortPerf] enumeration_ms={enumerationStopwatch.ElapsedMilliseconds} count={rebuiltFiles.Count} recursive={showAllSubfolders}");

        if (folderWorkToken.IsCancellationRequested ||
            !IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
            return;

        var rebuiltSnapshot = await SortFilesUsingCurrentSettingsAsync(rebuiltFiles, showAllSubfolders, folderWorkToken);
        if (folderWorkToken.IsCancellationRequested ||
            !IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
            return;

        PublishAllFiles(rebuiltFiles, rebuiltSnapshot);
        AppLogger.Memory($"RebuildList count={rebuiltFiles.Count}");
        CaptureSelectionForNextPageRefresh();
        var firstPageStopwatch = Stopwatch.StartNew();
        await RebuildDisplayFilterAsync(CurrentPage);
        PerfLogger.Log($"[SortPerf] first_page_ms={firstPageStopwatch.ElapsedMilliseconds} page={CurrentPage}");
        if (folderWorkToken.IsCancellationRequested ||
            !IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders)) return;

        // Load tags in background — page already visible, tags fill in asynchronously
        if (showAllSubfolders)
        {
            RunBackgroundSafe("show-all-tags-and-hash", folderWorkToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                var tagMap = await _metaRepo.GetTagMapByFolderAsync(folderPath);
                if (token.IsCancellationRequested ||
                    !IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                    return;
                int loadedCount = 0;
                    lock (_tagCacheLock)
                    {
                        foreach (var kv in tagMap)
                        {
                            if (kv.Value.Count > 0 && !_tagCacheByPath.ContainsKey(kv.Key))
                            {
                                _tagCacheByPath[kv.Key] = kv.Value;
                                loadedCount++;
                            }
                        }
                    }
                    if (loadedCount > 0)
                    {
                        // Refresh current page to show newly loaded tags
                        _ = _dispatcher.InvokeAsync(() =>
                        {
                            foreach (var item in Images)
                            {
                                if (tagMap.TryGetValue(item.FilePath, out var tags))
                                {
                                    item.Tags = new List<string>(tags);
                                    item.NotifyAll();
                                }
                            }
                            StatusText = $"总文件数: {_allFiles.Count} (含子文件夹) | 标签: {loadedCount}";
                        });
                    }

                    await Task.Delay(3000, token);
                    if (token.IsCancellationRequested ||
                        !IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                        return;
                    await PrecomputeHashesAsync(token, (await _folderRepo.GetByPathAsync(folderPath))?.Id);
            });
        }
    }

    private void InvalidateFileSortSnapshot()
    {
        _fileSortSnapshot = null;
    }

    private void PublishAllFiles(
        List<string> files,
        FileSortSnapshot? snapshot = null,
        bool contentChanged = true)
    {
        _allFiles = files;
        if (!contentChanged)
        {
            _fileSortSnapshot = snapshot?.MutationVersion == _fileListMutationVersion ? snapshot : null;
            return;
        }

        var mutationVersion = Interlocked.Increment(ref _fileListMutationVersion);
        _fileSortSnapshot = snapshot?.MutationVersion == mutationVersion ? snapshot : null;
    }

    private async Task<FileSortSnapshot> BuildFileSortSnapshotAsync(
        IReadOnlyList<string> files,
        bool showAllSubfolders,
        int mutationVersion,
        bool useFolderQuery,
        CancellationToken cancellationToken = default)
    {
        var cached = new Dictionary<string, ImageMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _metaCache)
            cached[pair.Key] = pair.Value;

        return await _fileSortSnapshotBuilder.BuildAsync(
            files,
            CurrentFolder,
            showAllSubfolders,
            mutationVersion,
            _metaRepo,
            useFolderQuery,
            cached,
            _fileSortSnapshot,
            cancellationToken).ConfigureAwait(false);
    }

    private int BeginFolderViewRequest()
    {
        CancelDisplayFilter();
        InvalidateFileSortSnapshot();
        InvalidateContinuousDisplayGeometry();
        Interlocked.Increment(ref _resultNavigationVersion);
        return Interlocked.Increment(ref _folderViewRequestVersion);
    }

    private int BeginSortRequest()
    {
        CancelDisplayFilter();
        InvalidateContinuousDisplayGeometry();
        Interlocked.Increment(ref _resultNavigationVersion);
        var nextCts = CancellationTokenSource.CreateLinkedTokenSource(CurrentFolderWorkToken);
        var previousCts = Interlocked.Exchange(ref _sortCts, nextCts);
        previousCts.Cancel();
        previousCts.Dispose();
        return Interlocked.Increment(ref _sortRequestVersion);
    }

    private CancellationToken BeginFolderWorkScope(string folderPath)
    {
        var oldCts = Interlocked.Exchange(ref _folderWorkCts, new CancellationTokenSource());
        try
        {
            oldCts.Cancel();
            AppLogger.Memory($"[FolderWork] cancel old folder={Path.GetFileName(CurrentFolder ?? "")}");
        }
        catch { }
        finally
        {
            oldCts.Dispose();
        }

        _pageManager.CancelCurrentLoads();
        _idleTimer?.Stop();
        return _folderWorkCts.Token;
    }

    private CancellationToken CurrentFolderWorkToken => Volatile.Read(ref _folderWorkCts).Token;

    private void RunBackgroundSafe(string name, CancellationToken ct, Func<CancellationToken, Task> work)
    {
        if (ct.IsCancellationRequested)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await work(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AppLogger.Memory($"[Background] canceled name={name}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Background] failed name={name}: {ex.Message}");
            }
        }, CancellationToken.None);
    }


}
