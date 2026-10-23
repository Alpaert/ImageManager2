using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Models;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Messages;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    // ==================== Duplicate Detection & Background Operations ====================

    [RelayCommand]
    public async Task DetectDuplicatesAsync(string targetDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return;
        CancellationToken token;
        lock (_backgroundTasksLock)
        {
            _duplicateDetectCts?.Cancel();
            _duplicateDetectCts?.Dispose();
            _duplicateDetectCts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            token = _duplicateDetectCts.Token;
        }

        StatusText = "全局查重中...";
        try
        {
            var (exact, fuzzy) = await _duplicateService.DetectAndMoveDuplicatesAsync(
                _allFiles, targetDir, token);

            if (token.IsCancellationRequested)
            {
                StatusText = "查重已取消";
                return;
            }

            StatusText = $"查重完成: 精确 {exact} 张, 模糊 {fuzzy} 张";
            if (!string.IsNullOrEmpty(CurrentFolder))
                await LoadFolderAsync(CurrentFolder);
        }
        catch (OperationCanceledException)
        {
            StatusText = "查重已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"查重失败: {ex.Message}";
            Common.Helpers.AppLogger.Error($"Duplicate detection failed: {ex}");
        }
        finally
        {
            lock (_backgroundTasksLock)
            {
                _duplicateDetectCts?.Dispose();
                _duplicateDetectCts = null;
            }
        }
    }

    public void CancelActiveBackgroundTasks()
    {
        lock (_backgroundTasksLock)
        {
            try { _characterMatchCts?.Cancel(); } catch { }
            try { _mediaMoveCts?.Cancel(); } catch { }
            try { _mediaCopyCts?.Cancel(); } catch { }
            try { _mediaDeleteCts?.Cancel(); } catch { }
            try { _duplicateDetectCts?.Cancel(); } catch { }
        }
    }

    public async Task RunCharacterMatchAsync(string folderPath, CancellationToken ct = default)
    {
        var currentOpId = Interlocked.Increment(ref _characterMatchOpId);
        CancellationToken token;
        lock (_backgroundTasksLock)
        {
            _characterMatchCts?.Cancel();
            _characterMatchCts?.Dispose();
            _characterMatchCts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            token = _characterMatchCts.Token;
        }

        var matchService = _characterMatchingService;
        if (matchService == null)
        {
            StatusText = "人物匹配服务不可用";
            return;
        }

        StatusText = "正在匹配人物 Embedding...";
        int lastWritten = 0;
        var progressWrapper = new Progress<CharacterMatchProgress>(p =>
        {
            var progress = p.OperationId == 0 ? p with { OperationId = currentOpId } : p;
            if (progress.OperationId != currentOpId) return;
            if (Volatile.Read(ref _characterMatchOpId) != currentOpId) return;

            BackgroundStatusText = $"人物 Embedding 匹配 {progress.ProcessedCount}/{progress.TotalCount} | 命中 {progress.MatchedCount} / 缺失 {progress.MissingVectorCount}";
            if (progress.WrittenTagsCount > lastWritten)
            {
                lastWritten = progress.WrittenTagsCount;
                _ = RefreshTagsAfterExternalWriteAsync(Array.Empty<string>(), refreshCounts: false);
            }
        });

        try
        {
            var request = new CharacterMatchRequest(
                FolderPath: folderPath,
                Recursive: true,
                MatchThreshold: AppSettings.CharacterMatchThreshold,
                MaxMatchesPerImage: Math.Clamp(AppSettings.CharacterMaxMatchesPerImage, 1, 5));

            var result = await matchService.MatchCharactersAsync(request, progressWrapper, token);

            if (Volatile.Read(ref _characterMatchOpId) != currentOpId) return;

            BackgroundStatusText = "";
            if (result.Cancelled)
            {
                StatusText = "人物 Embedding 匹配已取消";
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                StatusText = $"人物 Embedding 匹配失败: {result.ErrorMessage}";
            }
            else
            {
                StatusText = $"人物 Embedding 匹配完成: 图片 {result.TotalImages}, 有向量 {result.WithEmbeddings}, 缺失 {result.MissingEmbeddings}, 命中 {result.MatchedCount}, 写入 {result.WrittenTagsCount}";
                await RefreshTagsAfterExternalWriteAsync(Array.Empty<string>());
            }
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _characterMatchOpId) == currentOpId)
            {
                BackgroundStatusText = "";
                StatusText = "人物 Embedding 匹配已取消";
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _characterMatchOpId) == currentOpId)
            {
                BackgroundStatusText = "";
                StatusText = $"人物 Embedding 匹配失败: {ex.Message}";
                Common.Helpers.AppLogger.Error($"Character embedding match failed: {ex}");
            }
        }
        finally
        {
            if (Volatile.Read(ref _characterMatchOpId) == currentOpId)
            {
                lock (_backgroundTasksLock)
                {
                    _characterMatchCts?.Dispose();
                    _characterMatchCts = null;
                }
            }
        }
    }

    public async Task MoveFilesAsync(IReadOnlyList<string> filePaths, string targetDir, CancellationToken ct = default)
    {
        if (filePaths == null || filePaths.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return;
        var moveService = _mediaMoveService;
        if (moveService == null)
        {
            StatusText = "文件移动服务不可用";
            return;
        }

        CancellationToken token;
        CancellationTokenSource cts;
        lock (_backgroundTasksLock)
        {
            _mediaMoveCts?.Cancel();
            _mediaMoveCts?.Dispose();
            cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            _mediaMoveCts = cts;
            token = cts.Token;
        }

        StatusText = "正在移动...";
        var progress = new Progress<MediaMoveProgress>(p =>
        {
            StatusText = $"正在移动文件 ({p.Processed}/{p.Total})...";
        });
        try
        {
            var result = await moveService.MoveFilesAsync(new MediaMoveRequest(filePaths, targetDir), progress, cancellationToken: token);

            if (token.IsCancellationRequested)
            {
                StatusText = "文件移动已取消";
                return;
            }

            bool currentFolderAffected = !string.IsNullOrWhiteSpace(CurrentFolder) &&
                (string.Equals(Path.GetFullPath(targetDir).TrimEnd('\\', '/'), Path.GetFullPath(CurrentFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                 filePaths.Any(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f))?.TrimEnd('\\', '/'), Path.GetFullPath(CurrentFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)));

            if (currentFolderAffected)
            {
                await LoadFolderAsync(CurrentFolder);
            }

            StatusText = FormatMoveBatchResult(result, targetDir);
        }
        catch (OperationCanceledException)
        {
            StatusText = "文件移动已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"移动文件失败: {ex.Message}";
            Common.Helpers.AppLogger.Error($"Move files failed: {ex}");
        }
        finally
        {
            lock (_backgroundTasksLock)
            {
                if (_mediaMoveCts == cts)
                {
                    _mediaMoveCts?.Dispose();
                    _mediaMoveCts = null;
                }
            }
        }
    }

    public static string FormatMoveBatchResult(MediaMoveBatchResult result, string targetDir)
    {
        if (result.CompensationFailedCount > 0)
        {
            var firstComp = result.Items.FirstOrDefault(i => i.Status == MediaMoveItemStatus.CompensationFailed);
            var detail = firstComp != null ? $" (例如 {Path.GetFileName(firstComp.SourcePath)}: {firstComp.Error})" : "";
            return $"移动遇到异常需人工核查: 成功 {result.SuccessCount}, 失败 {result.FailedCount}, 待处理 {result.CompensationFailedCount}{detail}";
        }

        if (result.FailedCount > 0)
        {
            var firstFail = result.Items.FirstOrDefault(i => i.Status == MediaMoveItemStatus.Failed);
            var detail = firstFail != null ? $" (例如 {Path.GetFileName(firstFail.SourcePath)}: {firstFail.Error})" : "";
            return $"已移动 {result.SuccessCount} 个文件，失败 {result.FailedCount} 个到: {targetDir}{detail}";
        }

        if (result.CancelledCount > 0)
        {
            return $"移动已取消: 已移动 {result.SuccessCount} 个文件，未执行 {result.CancelledCount + result.SkippedCount} 个";
        }

        return $"已移动 {result.SuccessCount} 个文件到: {targetDir}";
    }

    public async Task<MediaMoveItemResult> MoveDirectoryAsync(string sourceDir, string targetParentDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetParentDir))
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Failed, "路径无效");

        var moveService = _mediaMoveService;
        if (moveService == null)
        {
            StatusText = "文件夹移动服务不可用";
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Failed, "文件夹移动服务不可用");
        }

        CancellationToken token;
        CancellationTokenSource cts;
        lock (_backgroundTasksLock)
        {
            _mediaMoveCts?.Cancel();
            _mediaMoveCts?.Dispose();
            cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            _mediaMoveCts = cts;
            token = cts.Token;
        }

        var dirName = Path.GetFileName(sourceDir.TrimEnd('\\', '/'));
        StatusText = $"正在移动文件夹：{dirName}…";

        var progress = new Progress<MediaMoveProgress>(p =>
        {
            StatusText = $"{p.CurrentStage}：{dirName}";
        });

        try
        {
            SuppressDeletedEvent();
            var result = await moveService.MoveDirectoryAsync(
                new DirectoryMoveRequest(sourceDir, targetParentDir),
                progress,
                token);

            if (result.Status == MediaMoveItemStatus.Success)
            {
                await RefreshFolderTreeAsync();
                await LoadFolderAsync(CurrentFolder);
                StatusText = $"已移动文件夹到：{result.TargetPath ?? targetParentDir}";
            }
            else if (result.Status == MediaMoveItemStatus.Cancelled)
            {
                StatusText = $"移动文件夹已取消：{dirName}";
            }
            else if (result.Status == MediaMoveItemStatus.CompensationFailed)
            {
                StatusText = $"文件夹移动异常需人工核查：{result.Error ?? "目录状态不确定，请检查磁盘"}";
            }
            else
            {
                StatusText = $"移动文件夹失败：{result.Error}";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = $"移动文件夹已取消：{dirName}";
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = $"移动文件夹失败：{ex.Message}";
            Common.Helpers.AppLogger.Error($"Move directory failed: {ex}");
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Failed, ex.Message);
        }
        finally
        {
            RestoreDeletedEvent();
            lock (_backgroundTasksLock)
            {
                if (_mediaMoveCts == cts)
                {
                    _mediaMoveCts?.Dispose();
                    _mediaMoveCts = null;
                }
            }
        }
    }

    public async Task CopyFilesAsync(IReadOnlyList<string> filePaths, string targetDir, CancellationToken ct = default)
    {
        if (filePaths == null || filePaths.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return;

        CancellationToken token;
        CancellationTokenSource cts;
        lock (_backgroundTasksLock)
        {
            _mediaCopyCts?.Cancel();
            _mediaCopyCts?.Dispose();
            cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            _mediaCopyCts = cts;
            token = cts.Token;
        }

        StatusText = "正在复制...";
        int success = 0;
        int failed = 0;

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(targetDir);
                foreach (var filePath in filePaths)
                {
                    if (token.IsCancellationRequested) break;
                    if (!File.Exists(filePath)) continue;
                    try
                    {
                        var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                            Path.Combine(targetDir, Path.GetFileName(filePath)));
                        File.Copy(filePath, destPath);
                        Interlocked.Increment(ref success);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Common.Helpers.AppLogger.Warn($"复制文件失败: {filePath}; {ex.Message}");
                    }
                }
            }, token);

            if (token.IsCancellationRequested)
            {
                StatusText = "文件复制已取消";
                return;
            }

            if (!string.IsNullOrWhiteSpace(CurrentFolder) &&
                string.Equals(Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
                    Path.GetFullPath(CurrentFolder).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                await LoadFolderAsync(CurrentFolder);
            }

            StatusText = failed == 0
                ? $"已复制 {success} 个文件到: {targetDir}"
                : $"已复制 {success} 个文件，失败 {failed} 个到: {targetDir}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "文件复制已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"复制文件失败: {ex.Message}";
            Common.Helpers.AppLogger.Error($"Copy files failed: {ex}");
        }
        finally
        {
            lock (_backgroundTasksLock)
            {
                if (_mediaCopyCts == cts)
                {
                    _mediaCopyCts?.Dispose();
                    _mediaCopyCts = null;
                }
            }
        }
    }

    public async Task DeleteFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        if (filePaths == null) return;
        var targets = filePaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0) return;

        CancellationToken token;
        CancellationTokenSource cts;
        lock (_backgroundTasksLock)
        {
            _mediaDeleteCts?.Cancel();
            _mediaDeleteCts?.Dispose();
            cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            _mediaDeleteCts = cts;
            token = cts.Token;
        }

        StatusText = "正在删除文件...";
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int successCount = 0;
        int failedCount = 0;

        _pageManager.CancelCurrentLoads();
        SuppressDeletedEvent();
        try
        {
            await Task.Run(() =>
            {
                foreach (var filePath in targets)
                {
                    if (token.IsCancellationRequested) break;
                    if (!File.Exists(filePath)) continue;
                    try
                    {
                        bool deleted = false;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                File.Delete(filePath);
                                deleted = true;
                                break;
                            }
                            catch (IOException) when (attempt < 4)
                            {
                                Thread.Sleep(30);
                            }
                        }
                        if (!deleted)
                        {
                            File.Delete(filePath);
                        }

                        _thumbCache.InvalidateThumbnail(filePath);
                        lock (deletedPaths)
                        {
                            deletedPaths.Add(filePath);
                        }
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedCount);
                        Common.Helpers.AppLogger.Warn($"删除文件失败: {filePath}; {ex.Message}");
                    }
                }
            }, token);

            if (token.IsCancellationRequested)
            {
                StatusText = "删除文件已取消";
            }
            else
            {
                StatusText = failedCount == 0
                    ? $"已删除 {successCount} 个文件"
                    : $"已删除 {successCount} 个文件，失败 {failedCount} 个";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "删除文件已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"删除文件失败: {ex.Message}";
            Common.Helpers.AppLogger.Error($"Delete files failed: {ex}");
        }
        finally
        {
            RestoreDeletedEvent();
            lock (_backgroundTasksLock)
            {
                if (_mediaDeleteCts == cts)
                {
                    _mediaDeleteCts?.Dispose();
                    _mediaDeleteCts = null;
                }
            }
        }

        if (deletedPaths.Count > 0)
        {
            await RemoveFilesFromViewAsync(deletedPaths);
        }
    }

    public async Task<int> ClearFilesTagsAndStatusAsync(List<string> files)
    {
        if (files.Count == 0) return 0;
        await Task.Run(() => _metaRepo.ClearTagsAndStatusBatchAsync(files));
        InvalidatePageCache();
        await RefreshTagsAfterExternalWriteAsync(files);
        return files.Count;
    }

    public async Task<MediaThumbnailPrewarmResult?> PrewarmMediaThumbnailsAsync(
        IReadOnlyList<string> paths,
        IProgress<MediaThumbnailPrewarmProgress>? progress,
        CancellationToken token)
    {
        var service = _mediaThumbnailPrewarmService;
        if (service == null) return null;
        return await service.PrewarmAsync(paths, _pageManager.ThumbnailDecodeWidth, progress, token);
    }

    public async Task PrewarmVideoOriginalFramesAsync(
        List<string> videos,
        CancellationToken token)
    {
        if (videos.Count == 0)
        {
            StatusText = "文件夹及子文件夹无视频";
            BackgroundStatusText = "";
            return;
        }

        var originalFrames = _videoOriginalFrameCacheService;
        if (originalFrames == null) return;

        var existing = 0;
        var missingVideos = new List<string>();

        StatusText = $"正在检查 {videos.Count} 个视频的原始帧缓存...";
        BackgroundStatusText = $"视频原始帧检查 0/{videos.Count}";

        for (var i = 0; i < videos.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            if (originalFrames.Exists(videos[i]))
                existing++;
            else
                missingVideos.Add(videos[i]);

            if ((i + 1) % 100 == 0 || i + 1 == videos.Count)
            {
                var checkedCount = i + 1;
                var missingCount = missingVideos.Count;
                BackgroundStatusText =
                    $"视频原始帧检查 {checkedCount}/{videos.Count} | 已有 {existing} / 缺失 {missingCount}";
            }
        }

        var missing = missingVideos.Count;
        StatusText = $"视频原始帧: 总数 {videos.Count}, 已有 {existing}, 缺失 {missing}";
        BackgroundStatusText = missing == 0
            ? ""
            : $"视频原始帧生成 0/{missing} | 已有 {existing} / 新增 0 / 失败 0";

        if (missing == 0)
            return;

        var generated = 0;
        var failed = 0;

        for (var i = 0; i < missingVideos.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = missingVideos[i];
            var index = i + 1;

            VideoOriginalFrameResult result;
            try
            {
                result = await originalFrames.EnsureAsync(
                    path,
                    token,
                    cancelExtractionOnCancellation: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new VideoOriginalFrameResult(false, false, null, ex.Message);
            }

            if (result.Success)
            {
                if (!result.AlreadyExisted)
                    generated++;
            }
            else
            {
                failed++;
                AppLogger.Warn($"VideoOriginalFrames batch failed file={path} error={result.Error}");
            }

            BackgroundStatusText =
                $"视频原始帧生成 {index}/{missing} | 已有 {existing} / 新增 {generated} / 失败 {failed}";
        }

        StatusText = $"视频原始帧预生成完成: 总数 {videos.Count}, 已有 {existing}, 新增 {generated}, 失败 {failed}";
    }

    public async Task RunAutoTagAsync(
        long? folderDbId,
        string? folderPath,
        List<string>? filePaths,
        bool recursive)
    {
        var controller = _autoTagOrchestrator;
        if (controller == null) return;

        var messenger = _messenger;
        var msgToken = new object();
        var terminalMessage = 0;
        messenger?.Register<AutoTagProgressMessage>(msgToken, (r, m) =>
        {
            if (m.Phase is "Done" or "Stopped" or "Error")
                Interlocked.Exchange(ref terminalMessage, 1);
            App.UI.Post(() =>
            {
                StatusText = m.StatusText;
            });
        });

        try
        {
            var settings = AppSettings;
            controller.Configure(
                (Core.Services.TagMode)settings.TagMode,
                settings.SingleModelMinConfidence, 75,
                settings.EnsemblePixaiMinConfidence,
                settings.ArtistMatchThreshold,
                settings.EnableCharacterRecognition,
                settings.CharacterMatchThreshold,
                settings.CharacterMaxMatchesPerImage,
                settings.DeepSeekApiKey);

            StatusText = "正在准备自动打标...";
            if (folderDbId.HasValue && !string.IsNullOrEmpty(folderPath))
            {
                await controller.RunFolderAsync(folderDbId.Value, folderPath, recursive);
            }
            else
            {
                await controller.RunSelectedImagesAsync(filePaths ?? new List<string>());
            }

            var processed = controller.LastProcessedPaths?.ToList() ?? new List<string>();
            if (processed.Count > 0)
            {
                await RefreshTagsAfterExternalWriteAsync(processed);
                AppLogger.Info($"AutoTag tag counts refreshed processed={processed.Count}");
            }

            if (Volatile.Read(ref terminalMessage) == 0)
            {
                var total = filePaths?.Count ?? 0;
                StatusText = controller.LastRunCancelled
                    ? "自动打标已停止"
                    : processed.Count > 0
                        ? $"打标完成 ({processed.Count}/{(total > 0 ? total : processed.Count)} 张)"
                        : controller.LastPreparationFailed > 0
                            ? $"打标完成（准备失败 {controller.LastPreparationFailed} 张，跳过 {controller.LastSkippedCount} 张）"
                            : $"打标完成（跳过 {controller.LastSkippedCount} 张）";
            }
        }
        finally
        {
            if (messenger != null)
            {
                try { messenger.Unregister<AutoTagProgressMessage>(msgToken); }
                catch (Exception ex) { AppLogger.Warn($"AutoTag unregister failed: {ex.Message}"); }
            }
        }
    }
}
