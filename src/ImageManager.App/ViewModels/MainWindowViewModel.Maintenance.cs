using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    public async Task RecomputeFailedHashesForFolderAsync(FolderTreeNode folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
        {
            StatusText = "未选择文件夹";
            return;
        }

        if (!Directory.Exists(folder.Path))
        {
            StatusText = "文件夹不存在";
            return;
        }

        List<string> retryPaths;
        try
        {
            retryPaths = await _metaRepo.ResetFailedHashStatusByFolderAsync(folder.Path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Reset failed hash status failed: {folder.Path} | {ex.Message}");
            StatusText = $"重置失败项失败: {ex.Message}";
            return;
        }

        if (retryPaths.Count == 0)
        {
            StatusText = "没有需要重新计算的失败项";
            return;
        }

        var folderWorkToken = CurrentFolderWorkToken;
        try
        {
            StatusText = $"正在重新计算 {retryPaths.Count} 个失败项...";
            var files = retryPaths
                .Where(path => File.Exists(path) && FileTypeConstants.IsImageFile(path))
                .ToList();
            if (files.Count == 0)
            {
                StatusText = "失败项文件不存在或不是图片文件";
                return;
            }

            await PrecomputeHashesAsync(folderWorkToken, folder.DbId > 0 ? folder.DbId : null, files);
            StatusText = $"已重新计算失败项: {retryPaths.Count}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "重新计算失败项已取消";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Recompute failed hashes failed: {folder.Path} | {ex.Message}");
            StatusText = $"重新计算失败项失败: {ex.Message}";
        }
    }

    /// <summary>Compute missing or failed image hashes for a folder and all descendants.</summary>
    public async Task ComputeHashesIncrementallyForFolderAsync(FolderTreeNode folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
        {
            StatusText = "文件夹不存在";
            return;
        }

        List<string> files;
        try
        {
            files = await Task.Run(() => Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(FileTypeConstants.IsImageFile)
                .ToList());
        }
        catch (Exception ex)
        {
            StatusText = $"读取文件夹失败: {ex.Message}";
            return;
        }

        if (files.Count == 0)
        {
            StatusText = "文件夹中没有图片";
            return;
        }

        var folderWorkToken = CurrentFolderWorkToken;
        try
        {
            var folderInfo = await _folderRepo.GetByPathAsync(folder.Path);
            StatusText = $"正在计算 {files.Count} 张图片的指纹...";
            await PrecomputeHashesAsync(folderWorkToken, folderInfo?.Id ?? (folder.DbId > 0 ? folder.DbId : null), files);
            StatusText = $"增量计算完成: {files.Count} 张图片";
        }
        catch (OperationCanceledException)
        {
            StatusText = "指纹计算已取消";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Incremental hash failed: {folder.Path} | {ex.Message}");
            StatusText = $"指纹计算失败: {ex.Message}";
        }
    }

    private void StartWatchingCurrentFolder()
    {
        StopWatchingCurrentFolder();
        if (string.IsNullOrEmpty(CurrentFolder) || !Directory.Exists(CurrentFolder)) return;

        _folderWatcher = new FileSystemWatcher(CurrentFolder)
        {
            IncludeSubdirectories = ShowAllSubfolders,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = false
        };
        _folderWatcher.Created += OnFolderFileCreated;
        _folderWatcher.Deleted += OnFolderFileDeleted;
        _folderWatcher.Renamed += OnFolderFileRenamed;
        _folderWatcher.EnableRaisingEvents = true;
    }

    private void StopWatchingCurrentFolder()
    {
        _folderWatchDebounceCts?.Cancel();
        _folderWatchDebounceCts?.Dispose();
        _folderWatchDebounceCts = null;
        if (_folderWatcher != null)
        {
            _folderWatcher.EnableRaisingEvents = false;
            _folderWatcher.Created -= OnFolderFileCreated;
            _folderWatcher.Deleted -= OnFolderFileDeleted;
            _folderWatcher.Renamed -= OnFolderFileRenamed;
            _folderWatcher.Dispose();
            _folderWatcher = null;
        }
    }

    public void SuppressDeletedEvent()
    {
        if (_folderWatcher != null)
            _folderWatcher.Deleted -= OnFolderFileDeleted;
    }

    public void RestoreDeletedEvent()
    {
        if (_folderWatcher != null)
            _folderWatcher.Deleted += OnFolderFileDeleted;
    }

    private void OnFolderFileCreated(object sender, FileSystemEventArgs e)
    {
        QueueFolderRefreshForWatcherEvent(e, force: Directory.Exists(e.FullPath));
    }

    private void OnFolderFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (FileTypeConstants.IsMediaFile(e.FullPath))
            _thumbCache.InvalidateThumbnail(e.FullPath);
        QueueFolderRefreshForWatcherEvent(e, force: Directory.Exists(e.FullPath) || !FileTypeConstants.IsMediaFile(e.FullPath));
    }

    private void OnFolderFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!FileTypeConstants.IsMediaFile(e.Name ?? string.Empty) && !FileTypeConstants.IsMediaFile(e.OldName ?? string.Empty))
            return;

        if (FileTypeConstants.IsMediaFile(e.OldFullPath) && FileTypeConstants.IsMediaFile(e.FullPath))
            _thumbCache.MoveDiskCache(e.OldFullPath, e.FullPath);
        else if (FileTypeConstants.IsMediaFile(e.OldFullPath))
            _thumbCache.InvalidateThumbnail(e.OldFullPath);

        QueueFolderRefreshForWatcherEvent(e, force: true);
    }

    private void QueueFolderRefreshForWatcherEvent(FileSystemEventArgs e, bool force = false)
    {
        if (!force && !FileTypeConstants.IsMediaFile(e.FullPath))
            return;

        _folderWatchDebounceCts?.Cancel();
        _folderWatchDebounceCts?.Dispose();
        _folderWatchDebounceCts = new CancellationTokenSource();
        var ct = _folderWatchDebounceCts.Token;

        RunBackgroundSafe("folder-watcher-refresh", ct, async token =>
        {
            await Task.Delay(2000, token);
            await _dispatcher.InvokeAsync(async () =>
            {
                await RefreshCurrentFolderFromDiskAsync("watcher");
                if (!ShowAllSubfolders)
                    await RefreshFolderItemsAsync(token);
            });
        });
    }

    public async Task RefreshCurrentFolderFromDiskAsync(string reason = "refresh")
    {
        var folderWorkToken = CurrentFolderWorkToken;
        if (string.IsNullOrEmpty(CurrentFolder) || !Directory.Exists(CurrentFolder)) return;

        try
        {
            if (folderWorkToken.IsCancellationRequested)
                return;
            FileEnumerationResult refreshEnumResult;
            if (ShowAllSubfolders)
            {
                refreshEnumResult = await EnumerateMediaFilesAsync(CurrentFolder, recursive: true, folderWorkToken);
            }
            else
            {
                refreshEnumResult = await Task.Run(() =>
                {
                    var files = new List<string>();
                    foreach (var file in Directory.EnumerateFiles(CurrentFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (folderWorkToken.IsCancellationRequested)
                            return new FileEnumerationResult(files, true);
                        if (FileTypeConstants.IsMediaFile(file))
                            files.Add(file);
                    }
                    return new FileEnumerationResult(files, folderWorkToken.IsCancellationRequested);
                }, CancellationToken.None);
            }
            if (refreshEnumResult.Canceled)
                return;
            var diskFiles = refreshEnumResult.Files;

            var diskSnapshot = await SortFilesUsingCurrentSettingsAsync(diskFiles, ShowAllSubfolders, folderWorkToken);

            if (folderWorkToken.IsCancellationRequested) return;
            var oldSet = new HashSet<string>(_allFiles, StringComparer.OrdinalIgnoreCase);
            var newSet = new HashSet<string>(diskFiles, StringComparer.OrdinalIgnoreCase);
            var deleted = oldSet.Where(path => !newSet.Contains(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = diskFiles.Where(path => !oldSet.Contains(path)).ToList();
            if (deleted.Count == 0 && added.Count == 0) return;

            AppLogger.Memory($"FolderRefresh reason={reason} added={added.Count} deleted={deleted.Count} showAll={ShowAllSubfolders}");
            PublishAllFiles(diskFiles, diskSnapshot);
            if (_tagSearch.SearchResultFiles.Count > 0
                && _tagSearch.SearchResultFiles.RemoveAll(path => deleted.Contains(path)) > 0)
                _tagSearch.NotifySearchResultFilesChanged();
            if (_tagSearch.IsSuggestionCoTagMode)
            {
                InvalidateCoTagSuggestionCache();
                _ = _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
            }

            CaptureSelectionForNextPageRefresh();
            await RebuildDisplayFilterAsync(CurrentPage);
            if (folderWorkToken.IsCancellationRequested) return;

            var fi = await _folderRepo.GetByPathAsync(CurrentFolder);
            if (fi != null)
            {
                var exts = FileTypeConstants.AllMediaExtensions.ToArray();
                var currentFolder = CurrentFolder;
                var showAll = ShowAllSubfolders;
                RunBackgroundSafe("folder-refresh-sync", folderWorkToken, token =>
                    SyncFolderAsync(currentFolder, fi.Id, exts, token, recursive: showAll));
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Memory($"FolderRefresh canceled reason={reason} folder={Path.GetFileName(CurrentFolder)}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FolderRefresh failed reason={reason}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sync folder: detect new/deleted files, compute hashes for new files, refresh UI if changed.
    /// Returns true if any files were added or removed.
    /// </summary>
    private async Task<bool> SyncFolderAsync(
        string folder,
        long folderId,
        string[] exts,
        CancellationToken ct = default,
        bool recursive = false,
        Func<bool>? isCurrent = null)
    {
        try
        {
            // Disk enum + DB query on background thread to avoid blocking UI
            var diskFiles = await Task.Run(() =>
            {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", searchOption))
                {
                    if (ct.IsCancellationRequested)
                        return files;
                    if (FileTypeConstants.IsMediaFile(file))
                        files.Add(file);
                }
                return files;
            }, CancellationToken.None);
            if (ct.IsCancellationRequested)
                return false;

            var dbFiles = await Task.Run(() => _metaRepo.GetByFolderIdAsync(folderId), ct);
            var dbSet = new HashSet<string>(dbFiles.Select(m => m.FilePath), StringComparer.OrdinalIgnoreCase);

            // MD5 + DB on background thread to avoid blocking UI
            (List<string> newFiles, bool deleted) = await Task.Run(async () =>
            {
                var newFiles = new List<string>();
                foreach (var file in diskFiles)
                {
                    await AutoTagRuntimeState.WaitForIdleAsync(ct);
                    if (!dbSet.Contains(file))
                    {
                        if (FileTypeConstants.IsVideoFile(file))
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                await _metaRepo.UpsertAsync(new ImageMeta
                                {
                                    FilePath = file,
                                    FolderId = folderId,
                                    FileSize = fi.Length,
                                    LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                                    HashStatus = -1
                                });
                            }
                            catch { }

                            newFiles.Add(file);
                            continue;
                        }

                        string? md5 = null;
                        try
                        {
                            await AutoTagRuntimeState.WaitForIdleAsync(ct);
                            using var fs = File.OpenRead(file);
                            md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs)).ToLowerInvariant();
                            await AutoTagRuntimeState.WaitForIdleAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(md5))
                        {
                            var match = await _metaRepo.GetByFileHashAsync(md5);
                            if (match != null)
                            {
                                var tagNames = match.Tags.Select(t => t.Name).ToList();
                                if (!File.Exists(match.FilePath))
                                {
                                    await _metaRepo.UpdateFilePathAsync(match.Id, file, folderId);
                                }
                                else if (string.Equals(match.FilePath, file, StringComparison.OrdinalIgnoreCase))
                                {
                                    await _metaRepo.SetFolderIdAsync(file, folderId);
                                }
                                else
                                {
                                    var fi = new FileInfo(file);
                                    var newMeta = new ImageMeta
                                    {
                                        FilePath = file, FileHash = md5, FolderId = folderId,
                                        FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks
                                    };
                                    var newId = await _metaRepo.UpsertAsync(newMeta);
                                    if (tagNames.Count > 0)
                                        await _metaRepo.SetTagsAsync(newId, tagNames);
                                }
                                lock (_tagCacheLock)
                                {
                                    _tagCacheByPath[file] = tagNames;
                                }
                                newFiles.Add(file);
                                continue;
                            }
                        }
                        var newFileInfo = new FileInfo(file);
                        await _metaRepo.UpsertAsync(new ImageMeta
                        {
                            FilePath = file,
                            FileHash = md5,
                            FolderId = folderId,
                            FileSize = newFileInfo.Length,
                            LastWriteTicks = newFileInfo.LastWriteTimeUtc.Ticks,
                            HashStatus = 0
                        });
                        newFiles.Add(file);
                    }
                }

                bool deleted = false;
                foreach (var meta in dbFiles)
                {
                    await AutoTagRuntimeState.WaitForIdleAsync(ct);
                    if (!diskFiles.Contains(meta.FilePath) && !File.Exists(meta.FilePath))
                    {
                        await _metaRepo.SetFolderIdAsync(meta.FilePath, 0L);
                        _thumbCache.InvalidateThumbnail(meta.FilePath);
                        deleted = true;
                    }
                }
                return (newFiles, deleted);
            }, ct);

            if (newFiles.Count > 0 || deleted)
            {
                if (string.Equals(CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    if (isCurrent?.Invoke() == false)
                        return false;

                    // Reuse RebuildFileListAsync to correctly handle ShowAllSubfolders,
                    // sorting, and page state — instead of overwriting _allFiles with
                    // top-level-only diskFiles which destroys sub-folder view.
                    await _dispatcher.InvokeAsync(async () =>
                    {
                        await RebuildFileListAsync();
                    });
                }
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Memory($"[SyncFolder] canceled folder={Path.GetFileName(folder)}");
        }
        catch { }
        return false;
    }

    private async Task PrecomputeHashesAsync(CancellationToken ct, long? folderId = null, IReadOnlyCollection<string>? paths = null)
    {
        if (AutoTagRuntimeState.IsRunning)
        {
            AppLogger.Info("HashPrecompute skipped while auto-tagging is running");
            return;
        }

        // Unified cancellation: cancel any previous run, link with caller's token
        var oldCts = Interlocked.Exchange(ref _hashCts, CancellationTokenSource.CreateLinkedTokenSource(ct));
        oldCts?.Cancel();
        oldCts?.Dispose();
        var token = _hashCts!.Token;

        if (!await _hashGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            try { await _hashGate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        try
        {
            if (token.IsCancellationRequested) return;
            await PrecomputeHashesCoreAsync(token, folderId, paths);
        }
        finally { _hashGate.Release(); }
    }

    private async Task PrecomputeHashesCoreAsync(CancellationToken ct, long? folderId, IReadOnlyCollection<string>? paths = null)
    {
        if (AutoTagRuntimeState.IsRunning) return;

        var files = (paths ?? _allFiles).ToArray();
        if (files.Length == 0) return;

        HashSet<string> existingSet = await GetHashedPathsWithRetryAsync(files, ct);

        var needsHashing = files
            .Where(f => !existingSet.Contains(f))
            .Where(f => FileTypeConstants.IsImageFile(f))  // Skip video files
            .ToList();
        AppLogger.Memory($"HashPrecompute.Start totalFiles={files.Length} hashed={existingSet.Count} needsHashing={needsHashing.Count}");
        if (needsHashing.Count == 0)
        {
            AppLogger.Memory($"HashPrecompute.End processed=0 totalFiles={files.Length} hashed={existingSet.Count} needsHashing=0");
            return;
        }

        // Pre-query existing FileHash to avoid recomputing MD5 for files already
        // indexed by SyncFolderAsync (which stores FileHash with HashStatus=0)
        var fileHashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dbHashes = await _metaRepo.GetFileHashesByPathsAsync(needsHashing);
            foreach (var kv in dbHashes)
                fileHashCache[kv.Key] = kv.Value;
        }
        catch (Exception ex) { AppLogger.Warn($"预加载 FileHash 失败: {ex.Message}"); }

        // Fetch stable record metadata (Id, FilePath, LastWriteTicks, FileSize) to prevent resurrecting moved/deleted files
        var existingMetas = await _metaRepo.GetHashMetadataByPathsAsync(needsHashing);
        var metaByPath = existingMetas.ToDictionary(m => m.FilePath, StringComparer.OrdinalIgnoreCase);

        var unindexed = new List<ImageMeta>();
        foreach (var path in needsHashing)
        {
            if (!metaByPath.ContainsKey(path) && File.Exists(path))
            {
                try
                {
                    var fi = new FileInfo(path);
                    unindexed.Add(new ImageMeta
                    {
                        FilePath = path,
                        FolderId = folderId,
                        FileSize = fi.Length,
                        LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                        HashStatus = 0
                    });
                }
                catch { }
            }
        }

        if (unindexed.Count > 0)
        {
            await _metaRepo.RegisterNewFilesBatchAsync(unindexed);
            var registered = await _metaRepo.GetHashMetadataByPathsAsync(unindexed.Select(u => u.FilePath).ToList());
            foreach (var m in registered)
                metaByPath[m.FilePath] = m;
        }

        // === Producer: I/O-bound file reading ===
        var channel = Channel.CreateBounded<(long Id, string Path, byte[] Data, long FileSize, long LastWriteTicks, string FileHash)>(
            new BoundedChannelOptions(50)
            {
                SingleWriter = false, SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        int ioConcurrency = 2;

        // === Consumer: CPU-bound hash computation ===
        int cpuConcurrency = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
        using var cpuSlots = new SemaphoreSlim(cpuConcurrency);
        var upsertBatch = new ConcurrentQueue<ImageHashUpdateItem>();
        var failedBatch = new ConcurrentQueue<ImageHashUpdateItem>();
        int processed = 0;
        int failed = 0;
        int failedLogCount = 0;
        int totalNeed = needsHashing.Count;

        _ = _dispatcher.InvokeAsync(() =>
            BackgroundStatusText = $"正在计算图片指纹... 0/{totalNeed}");

        var consumeTasks = Enumerable.Range(0, cpuConcurrency).Select(async workerIndex =>
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var item))
                    {
                        await AutoTagRuntimeState.WaitForIdleAsync(ct);
                        await cpuSlots.WaitAsync(ct);
                        try
                        {
                            var phash = _hashService?.ComputePerceptualHash(item.Data);
                            var hashSucceeded = !string.IsNullOrEmpty(phash);
                            int? w = null, h = null;
                            try
                            {
                                var (dimW, dimH) = _thumbCache.GetDimensions(item.Path);
                                w = dimW; h = dimH;
                            }
                            catch { }

                            if (hashSucceeded)
                                _phashCache[item.Path] = phash!;

                            var updateItem = new ImageHashUpdateItem(
                                item.Id,
                                item.Path,
                                item.LastWriteTicks,
                                item.FileSize,
                                item.FileHash,
                                phash,
                                w, h,
                                hashSucceeded ? 1 : -1);

                            upsertBatch.Enqueue(updateItem);

                            if (upsertBatch.Count >= 100)
                            {
                                var batch = DrainBatch();
                                if (batch.Count > 0)
                                {
                                    await AutoTagRuntimeState.WaitForIdleAsync(ct);
                                    var result = await _metaRepo.UpdateHashBatchAsync(batch);
                                    Interlocked.Add(ref processed, result.UpdatedCount);
                                    if (result.StaleCount > 0)
                                        AppLogger.Info($"HashPrecompute dropped {result.StaleCount} stale items.");
                                }
                                int snap = Interlocked.CompareExchange(ref processed, 0, 0);
                                _ = _dispatcher.InvokeAsync(() =>
                                    BackgroundStatusText = $"正在计算图片指纹... {snap}/{totalNeed}");
                                if (MemoryPressureMonitor.Current >= MemoryPressureMonitor.PressureLevel.High)
                                {
                                    MemoryPressureMonitor.CompactLoh();
                                    AppLogger.Memory("HashPrecompute.PostGC");
                                }
                            }
                        }
                        finally { cpuSlots.Release(); }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Error($"Hash consumer 异常: {ex.Message}"); }
        });

        // Start consumers first, then produce with bounded parallelism
        var consumerTaskList = consumeTasks.ToList();

        try
        {
            await Parallel.ForEachAsync(
                needsHashing,
                new ParallelOptions { MaxDegreeOfParallelism = ioConcurrency, CancellationToken = ct },
                async (path, token) =>
                {
                    try
                    {
                        await AutoTagRuntimeState.WaitForIdleAsync(token);

                        if (!metaByPath.TryGetValue(path, out var metaRecord))
                            return; // Not found or deleted

                        var fi = new FileInfo(path);
                        string fileHash;
                        if (fileHashCache.TryGetValue(path, out var cachedHash))
                        {
                            fileHash = cachedHash;
                        }
                        else
                        {
                            token.ThrowIfCancellationRequested();
                            using var fs = File.OpenRead(path);
                            fileHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs)).ToLowerInvariant();
                            token.ThrowIfCancellationRequested();
                        }
                        var hashInput = _thumbCache.DecodeForHashInput(path, 256);
                        if (hashInput == null)
                        {
                            failedBatch.Enqueue(new ImageHashUpdateItem(
                                metaRecord.Id,
                                path,
                                fi.LastWriteTimeUtc.Ticks,
                                fi.Length,
                                fileHash,
                                null, null, null,
                                -1));
                            Interlocked.Increment(ref failed);
                            if (Interlocked.Increment(ref failedLogCount) <= 5)
                                AppLogger.Warn($"HashPrecompute decode skipped: {path}");
                            return;
                        }
                        await channel.Writer.WriteAsync(
                            (metaRecord.Id, path, hashInput, fi.Length, fi.LastWriteTimeUtc.Ticks, fileHash), token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) when (HandleHashProducerFailure(path, ex))
                    {
                    }
                    catch (Exception ex) { AppLogger.Warn($"Hash producer 文件失败: {ex.Message}"); }
                });
        }
        finally
        {
            channel.Writer.TryComplete();
            await Task.WhenAll(consumerTaskList);
        }
        ct.ThrowIfCancellationRequested();
        await AutoTagRuntimeState.WaitForIdleAsync(ct);

        var final = DrainBatch();
        foreach (var batch in final.Chunk(100))
        {
            await AutoTagRuntimeState.WaitForIdleAsync(ct);
            var result = await _metaRepo.UpdateHashBatchAsync(batch.ToList());
            Interlocked.Add(ref processed, result.UpdatedCount);
            if (result.StaleCount > 0)
                AppLogger.Info($"HashPrecompute final dropped {result.StaleCount} stale items.");
        }

        var failedFinal = DrainFailedBatch();
        foreach (var batch in failedFinal.Chunk(100))
        {
            await AutoTagRuntimeState.WaitForIdleAsync(ct);
            await _metaRepo.UpdateHashBatchAsync(batch.ToList());
        }

        _ = _dispatcher.InvokeAsync(() =>
            BackgroundStatusText = "");
        AppLogger.Memory($"HashPrecompute.End processed={processed} failed={failed}");
        return;

        List<ImageHashUpdateItem> DrainBatch()
        {
            var list = new List<ImageHashUpdateItem>();
            while (upsertBatch.TryDequeue(out var m))
                list.Add(m);
            return list;
        }

        List<ImageHashUpdateItem> DrainFailedBatch()
        {
            var list = new List<ImageHashUpdateItem>();
            while (failedBatch.TryDequeue(out var m))
                list.Add(m);
            return list;
        }

        bool HandleHashProducerFailure(string path, Exception ex)
        {
            try
            {
                if (metaByPath.TryGetValue(path, out var failedMeta))
                {
                    var fi = new FileInfo(path);
                    failedBatch.Enqueue(new ImageHashUpdateItem(
                        failedMeta.Id,
                        path,
                        fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0,
                        fi.Exists ? fi.Length : 0,
                        null, null, null, null,
                        -1));
                    Interlocked.Increment(ref failed);
                }
            }
            catch { }

            if (Interlocked.Increment(ref failedLogCount) <= 5)
                AppLogger.Warn($"Hash producer 文件失败: {path} | {ex.Message}");
            return true;
        }
    }

    /// <summary>Query usable hashed paths with retry. On total failure, return an empty set
    /// so files are not falsely treated as completed; the write path will report the error.</summary>
    private async Task<HashSet<string>> GetHashedPathsWithRetryAsync(string[] files, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _metaRepo.GetHashedPathsAsync(files.ToList());
                // Warm the in-memory phash cache for similarity search
                RunBackgroundSafe("phash-cache-warmup", ct, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    var hashDict = await _metaRepo.GetPerceptualHashesByPathsAsync(files.ToList());
                    foreach (var kv in hashDict)
                        if (!string.IsNullOrEmpty(kv.Value))
                            _phashCache[kv.Key] = kv.Value;
                });
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"GetHashedPathsAsync 失败 ({attempt}/{maxRetries}): {ex.Message}");
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
            }
        }
        AppLogger.Error("GetHashedPathsAsync 全部重试失败，跳过本轮哈希预计算");
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>用户停止切换文件夹 5 秒后执行维护任务（CleanMeta、PrecomputeHashes、SyncFolder）</summary>
    private static ImageMeta CreateFailedHashMeta(string path, string? fileHash, FileInfo fileInfo, long? folderId)
        => new()
        {
            FilePath = path,
            FileHash = fileHash,
            FileSize = fileInfo.Exists ? fileInfo.Length : 0,
            LastWriteTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
            FolderId = folderId,
            HashStatus = -1
        };

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        // A stopped timer can already have a queued dispatcher callback. Do not let it
        // stop or replace a newer maintenance schedule.
        if (sender is DispatcherTimer timer && !ReferenceEquals(timer, _idleTimer))
        {
            timer.Stop();
            return;
        }

        _idleTimer?.Stop();

        // LOH compaction can block the UI thread long enough to visibly interrupt a
        // scroll animation. Keep all idle maintenance deferred until scrolling ends.
        if (_pageManager.IsScrollActive)
        {
            AppLogger.Memory("IdleMaintenance deferred by scroll activity");
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnIdleTimerTick);
            _idleTimer.Start();
            return;
        }

        var remaining = _deferMaintenanceUntilUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            _idleTimer = new DispatcherTimer(remaining, DispatcherPriority.Background, OnIdleTimerTick);
            _idleTimer.Start();
            return;
        }

        var heapMB = GC.GetTotalMemory(false) / 1048576.0;
        if (MemoryPressureMonitor.Current >= MemoryPressureMonitor.PressureLevel.Medium || heapMB >= 512)
        {
            AppLogger.Memory($"HashPrecompute deferred by memory pressure heapMB={heapMB:F1} level={MemoryPressureMonitor.Current}");
            MemoryPressureMonitor.CompactLoh();
            _deferMaintenanceUntilUtc = DateTime.UtcNow.AddSeconds(15);
            _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, OnIdleTimerTick);
            _idleTimer.Start();
            return;
        }

        var folder = CurrentFolder;
        var showAll = ShowAllSubfolders; // capture before Task.Run
        if (string.IsNullOrEmpty(folder)) return;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();
        var folderWorkToken = CurrentFolderWorkToken;

        RunBackgroundSafe("folder-maintenance", folderWorkToken, async token =>
        {
            var gateEntered = false;
            // 非阻塞获取：如果 AutoTag 正在运行则跳过本次维护
            if (!await _maintenanceGate.WaitAsync(0, token)) return;
            gateEntered = true;
            try
            {
                token.ThrowIfCancellationRequested();
                var folderInfo = await _folderRepo.GetByPathAsync(folder);
                var folderId = folderInfo?.Id;
                var diskFiles = new HashSet<string>(_allFiles, StringComparer.OrdinalIgnoreCase);

                await CleanMetaForFolderAsync(folder, diskFiles, showAll);

                if (folderId.HasValue)
                {
                    await PrecomputeHashesAsync(token, folderId);
                    await SyncFolderAsync(folder, folderId.Value, exts, token, recursive: showAll);
                }
            }
            finally { if (gateEntered) _maintenanceGate.Release(); }
        });
    }

    private async Task CleanMetaForFolderAsync(string folderPath, HashSet<string> existingFiles, bool showAll)
    {
        try
        {
            var metas = await _metaRepo.GetByFolderAsync(folderPath);
            var normalizedPath = PathHelper.NormalizeFolderPath(folderPath);

            foreach (var meta in metas)
            {
                if (!showAll)
                {
                    // Non-showAll: only clean records for files directly in this folder,
                    // not subfolder files (which are not in _allFiles / existingFiles).
                    var metaDir = Path.GetDirectoryName(meta.FilePath);
                    if (metaDir == null ||
                        !string.Equals(
                            PathHelper.NormalizeFolderPath(metaDir),
                            normalizedPath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!existingFiles.Contains(meta.FilePath) && !File.Exists(meta.FilePath))
                {
                    try { await _metaRepo.SetFolderIdAsync(meta.FilePath, 0L); }
                    catch (Exception ex) { AppLogger.Warn($"CleanMeta unlink failed: {meta.FilePath} — {ex.Message}"); }
                }
            }
        }
        catch { }
    }


}
