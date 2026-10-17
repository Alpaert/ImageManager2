using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 画师/角色嵌入库构建服务：采用“版本目录＋当前版本指针＋内存快照原子发布”方案。
/// 1. 在独立版本目录构建完整文件集（向量、名称映射、manifest）。
/// 2. 校验文件完整性、维度与数量，预加载到候选内存快照。
/// 3. 通过 current.version 指针文件原子替换发布。
/// 4. 原子切换单例 store 的内存快照及名称映射，查询全程持有独立快照，无锁不降级。
/// 5. 异常或取消时完整保留旧库与旧版本；同一图库同一时刻只允许一个构建发布者。
/// </summary>
public class LibraryBuilderService : ILibraryBuilderService
{
    private static readonly SemaphoreSlim _artistBuildGate = new(1, 1);
    private static readonly SemaphoreSlim _characterBuildGate = new(1, 1);

    private readonly IImageEmbeddingExtractor _embeddingExtractor;
    private readonly IArtistEmbeddingStore _artistStore;
    private readonly ICharacterEmbeddingStore _characterStore;
    private readonly IChineseTagLibrary _chineseLib;
    private readonly IThumbnailCacheService _thumbCache;

    public LibraryBuilderService(
        IImageEmbeddingExtractor embeddingExtractor,
        IArtistEmbeddingStore artistStore,
        ICharacterEmbeddingStore characterStore,
        IChineseTagLibrary chineseLib,
        IThumbnailCacheService thumbCache)
    {
        _embeddingExtractor = embeddingExtractor ?? throw new ArgumentNullException(nameof(embeddingExtractor));
        _artistStore = artistStore ?? throw new ArgumentNullException(nameof(artistStore));
        _characterStore = characterStore ?? throw new ArgumentNullException(nameof(characterStore));
        _chineseLib = chineseLib ?? throw new ArgumentNullException(nameof(chineseLib));
        _thumbCache = thumbCache ?? throw new ArgumentNullException(nameof(thumbCache));
    }

    public Task<LibraryBuildResult> BuildLibraryAsync(
        LibraryBuildRequest request,
        IProgress<LibraryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            bool isArtist = request.Target == LibraryBuildTarget.Artist;
            var gate = isArtist ? _artistBuildGate : _characterBuildGate;

            // 同一图库同一时间只允许一个发布者
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await BuildLibraryInternalAsync(request, isArtist, progress, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken);
    }

    private async Task<LibraryBuildResult> BuildLibraryInternalAsync(
        LibraryBuildRequest request,
        bool isArtist,
        IProgress<LibraryBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 1. 检查打标模型是否已加载；若未加载，尝试自动从模型缓存目录加载
        if (!_embeddingExtractor.IsModelLoaded)
        {
            var pixaiModelsDir = Path.Combine(_thumbCache.CacheDirectory, "models", "pixai");
            try
            {
                progress?.Report(new LibraryBuildProgress(
                    CurrentIndex: 0,
                    TotalCount: 0,
                    SkippedCount: 0,
                    BuiltCount: 0,
                    FailedCount: 0,
                    CurrentItemName: string.Empty,
                    Stage: "正在自动加载打标模型..."));

                await _embeddingExtractor.LoadModelAsync(pixaiModelsDir, cancellationToken);
            }
            catch (Exception loadEx)
            {
                AppLogger.Warn($"LibraryBuilderService: 自动加载打标模型失败: {loadEx.Message}");
            }
        }

        if (!_embeddingExtractor.IsModelLoaded)
        {
            return new LibraryBuildResult(
                TotalItems: 0,
                SkippedCount: 0,
                BuiltCount: 0,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: false,
                Cancelled: false,
                ErrorMessage: "打标模型未加载");
        }

        // 2. 检查参考目录
        if (string.IsNullOrWhiteSpace(request.ReferenceDirectory) || !Directory.Exists(request.ReferenceDirectory))
        {
            return new LibraryBuildResult(
                TotalItems: 0,
                SkippedCount: 0,
                BuiltCount: 0,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: false,
                Cancelled: false,
                ErrorMessage: "参考目录不存在");
        }

        // 3. 扫描子文件夹
        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(request.ReferenceDirectory);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"LibraryBuilderService: 扫描参考目录失败: {ex.Message}");
            return new LibraryBuildResult(
                TotalItems: 0,
                SkippedCount: 0,
                BuiltCount: 0,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: false,
                Cancelled: false,
                ErrorMessage: ex.Message);
        }

        if (subDirs.Length == 0)
        {
            return new LibraryBuildResult(
                TotalItems: 0,
                SkippedCount: 0,
                BuiltCount: 0,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: true,
                Cancelled: false,
                ErrorMessage: "未找到子文件夹");
        }

        // 4. 版本目录结构规划
        var modelsDir = Path.Combine(_thumbCache.CacheDirectory, "models");
        var storeBaseDir = Path.Combine(modelsDir, isArtist ? "artist_store" : "character_store");
        Directory.CreateDirectory(storeBaseDir);

        var versionId = $"v_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        var versionDir = Path.Combine(storeBaseDir, versionId);
        Directory.CreateDirectory(versionDir);

        var targetFile = isArtist
            ? Path.Combine(versionDir, "artist_embeddings.bin")
            : Path.Combine(versionDir, "character_embeddings.bin");

        var targetNamesFile = isArtist ? Path.Combine(versionDir, "artist_names.txt") : null;
        var manifestFile = Path.Combine(versionDir, "manifest.json");

        void CleanupVersionDir()
        {
            try
            {
                if (Directory.Exists(versionDir))
                    Directory.Delete(versionDir, recursive: true);
            }
            catch { }
        }

        // 基础数据字典（增量继承已有库）
        var tempArtists = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var tempCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tempArtistNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isArtist)
        {
            var snap = _artistStore.CurrentSnapshot;
            foreach (var (k, v) in snap.Artists)
            {
                var copy = new float[v.Length];
                Array.Copy(v, copy, v.Length);
                tempArtists[k] = copy;
            }
            foreach (var (k, v) in snap.ImageCounts)
            {
                tempCounts[k] = v;
                tempArtistNames[k] = _chineseLib.Lookup(k) ?? k;
            }
        }
        else
        {
            var snap = _characterStore.CurrentSnapshot;
            foreach (var (k, v) in snap.Characters)
            {
                var copy = new float[v.Length];
                Array.Copy(v, copy, v.Length);
                tempArtists[k] = copy;
            }
            foreach (var (k, v) in snap.ImageCounts)
            {
                tempCounts[k] = v;
            }
        }

        // 5. 比对图片数，若现有数量相同且>0则计入 skipped
        var toBuild = new List<(string Name, List<string> Images, int ImageCount)>();
        int skipped = 0;

        foreach (var dir in subDirs)
        {
            var name = Path.GetFileName(dir);
            List<string> images;
            try
            {
                images = Directory.GetFiles(dir)
                    .Where(FileTypeConstants.IsImageFile)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"LibraryBuilderService: 读取目录 {dir} 失败: {ex.Message}");
                continue;
            }

            int currentCount = images.Count;
            int storedCount = tempCounts.GetValueOrDefault(name, -1);

            if (storedCount == currentCount && storedCount > 0)
            {
                skipped++;
                continue;
            }

            toBuild.Add((name, images, currentCount));
        }

        if (toBuild.Count == 0)
        {
            CleanupVersionDir();
            progress?.Report(new LibraryBuildProgress(
                CurrentIndex: subDirs.Length,
                TotalCount: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: 0,
                FailedCount: 0,
                CurrentItemName: string.Empty,
                Stage: $"全部 {skipped} 个{(isArtist ? "画师" : "角色")}无需更新"));

            return new LibraryBuildResult(
                TotalItems: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: 0,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: true,
                Cancelled: false);
        }

        int built = 0;
        int failed = 0;
        int processedDirs = skipped;

        try
        {
            foreach (var (name, images, currentCount) in toBuild)
            {
                cancellationToken.ThrowIfCancellationRequested();

                processedDirs++;
                var isRebuild = tempCounts.GetValueOrDefault(name, -1) > 0;
                var actionLabel = isRebuild ? $"重建 {name}" : $"新增 {name}";
                var hint = $"跳过 {skipped} / 处理 {built + failed + 1}/{toBuild.Count}: {actionLabel}";

                progress?.Report(new LibraryBuildProgress(
                    CurrentIndex: processedDirs,
                    TotalCount: subDirs.Length,
                    SkippedCount: skipped,
                    BuiltCount: built,
                    FailedCount: failed,
                    CurrentItemName: name,
                    Stage: $"{hint} ({images.Count}张)"));

                if (images.Count == 0)
                {
                    continue;
                }

                var embeddings = await ExtractEmbeddingsWithFallbackAsync(
                    _embeddingExtractor,
                    images,
                    name,
                    request.BatchSize,
                    cancellationToken);

                float[]? sumEmb = null;
                int valid = 0;
                foreach (var emb in embeddings)
                {
                    if (sumEmb == null) sumEmb = new float[emb.Length];
                    for (int j = 0; j < emb.Length; j++) sumEmb[j] += emb[j];
                    valid++;
                }

                if (sumEmb != null && valid > 0)
                {
                    for (int j = 0; j < sumEmb.Length; j++) sumEmb[j] /= valid;

                    tempArtists[name] = sumEmb;
                    tempCounts[name] = valid;
                    if (isArtist)
                    {
                        tempArtistNames[name] = name;
                    }
                    built++;
                }
                else
                {
                    failed++;
                    AppLogger.Warn($"LibraryBuilderService: 无法为 {name} 提取到有效嵌入向量");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 6. 持久化至版本目录文件
            if (isArtist)
            {
                var snapToSave = new ArtistStoreSnapshot(versionId, tempArtists, tempCounts, 1024);
                ArtistEmbeddingStore.SaveSnapshotToFile(snapToSave, targetFile);

                var lines = tempArtistNames.Select(kv => $"{kv.Key}={kv.Value}");
                File.WriteAllLines(targetNamesFile!, lines);
            }
            else
            {
                var snapToSave = new CharacterStoreSnapshot(versionId, tempArtists, tempCounts, 1024);
                CharacterEmbeddingStore.SaveSnapshotToFile(snapToSave, targetFile);
            }

            // 写入版本 manifest
            var manifestObj = new
            {
                VersionId = versionId,
                Target = isArtist ? "Artist" : "Character",
                Count = tempArtists.Count,
                EmbeddingDim = 1024,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true }));

            // 7. 完整性与维度校验
            if (!File.Exists(targetFile) || new FileInfo(targetFile).Length == 0)
                throw new InvalidDataException("生成向量文件为空或不存在");

            if (isArtist && (!File.Exists(targetNamesFile!) || new FileInfo(targetNamesFile!).Length == 0 && tempArtists.Count > 0))
                throw new InvalidDataException("生成画师名称映射文件异常");

            // 8. 预加载到独立候选内存快照，验证可查询
            ArtistStoreSnapshot? candidateArtistSnap = null;
            CharacterStoreSnapshot? candidateCharSnap = null;

            if (isArtist)
            {
                candidateArtistSnap = ArtistEmbeddingStore.LoadSnapshotFromFile(targetFile, versionId);
                if (candidateArtistSnap.Artists.Count != tempArtists.Count || candidateArtistSnap.EmbeddingDim != 1024)
                    throw new InvalidDataException($"画师快照校验失败: count={candidateArtistSnap.Artists.Count}/{tempArtists.Count}");
            }
            else
            {
                candidateCharSnap = CharacterEmbeddingStore.LoadSnapshotFromFile(targetFile, versionId);
                if (candidateCharSnap.Characters.Count != tempArtists.Count || candidateCharSnap.EmbeddingDim != 1024)
                    throw new InvalidDataException($"角色快照校验失败: count={candidateCharSnap.Characters.Count}/{tempArtists.Count}");
            }

            // ==================== 最终提交点（不可取消） ====================
            // 9. 原子替换当前版本指针 (current.version)
            var pointerFile = Path.Combine(storeBaseDir, "current.version");
            var tempPointer = Path.Combine(storeBaseDir, $"current.version.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPointer, versionId);
            File.Move(tempPointer, pointerFile, overwrite: true);

            // 10. 同步复制一份至根 models 目录保持向后兼容
            try
            {
                var legacyBin = isArtist
                    ? Path.Combine(modelsDir, "artist_embeddings.bin")
                    : Path.Combine(modelsDir, "character_embeddings.bin");
                File.Copy(targetFile, legacyBin, overwrite: true);

                if (isArtist && targetNamesFile != null)
                {
                    var legacyNames = Path.Combine(modelsDir, "artist_names.txt");
                    File.Copy(targetNamesFile, legacyNames, overwrite: true);
                }
            }
            catch (Exception legacyEx)
            {
                AppLogger.Warn($"同步向后兼容文件警告: {legacyEx.Message}");
            }

            // 11. 原子切换内存快照
            if (isArtist)
            {
                _artistStore.SwapSnapshot(candidateArtistSnap!);
                _chineseLib.ReplaceArtistNames(tempArtistNames);
            }
            else
            {
                _characterStore.SwapSnapshot(candidateCharSnap!);
            }

            // 12. 清理过旧版本（保留当前版本和上一个有效版本）
            try
            {
                var allVersionDirs = Directory.GetDirectories(storeBaseDir, "v_*")
                    .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                    .ToList();

                // 保留前 2 个版本目录，其余清理
                foreach (var oldDir in allVersionDirs.Skip(2))
                {
                    try { Directory.Delete(oldDir, recursive: true); } catch { }
                }
            }
            catch { }

            int totalInStore = isArtist ? _artistStore.Count : _characterStore.Count;

            progress?.Report(new LibraryBuildProgress(
                CurrentIndex: subDirs.Length,
                TotalCount: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: built,
                FailedCount: failed,
                CurrentItemName: string.Empty,
                Stage: $"完成！跳过 {skipped} / 重建+新增 {built} (失败 {failed})，共 {totalInStore} 个{(isArtist ? "画师" : "角色")}"));

            return new LibraryBuildResult(
                TotalItems: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: built,
                TotalInStore: totalInStore,
                Completed: true,
                Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("LibraryBuilderService: 构建已被取消，清理未发布的版本目录");
            CleanupVersionDir();
            return new LibraryBuildResult(
                TotalItems: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: built,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: false,
                Cancelled: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"LibraryBuilderService: 构建发生异常: {ex.Message}，保留旧库");
            CleanupVersionDir();
            return new LibraryBuildResult(
                TotalItems: subDirs.Length,
                SkippedCount: skipped,
                BuiltCount: built,
                TotalInStore: isArtist ? _artistStore.Count : _characterStore.Count,
                Completed: false,
                Cancelled: false,
                ErrorMessage: ex.Message);
        }
    }

    private static async Task<List<float[]>> ExtractEmbeddingsWithFallbackAsync(
        IImageEmbeddingExtractor extractor,
        List<string> images,
        string itemName,
        int initialBatchSize,
        CancellationToken ct)
    {
        var result = new List<float[]>();
        int batchSize = initialBatchSize > 0 ? initialBatchSize : 8;
        int index = 0;

        while (index < images.Count)
        {
            ct.ThrowIfCancellationRequested();

            var actualBatchSize = Math.Min(batchSize, images.Count - index);
            var batch = images.Skip(index).Take(actualBatchSize).ToList();

            try
            {
                var embs = await extractor.GetEmbeddingsBatchAsync(batch);
                if (embs != null && embs.Count > 0)
                {
                    result.AddRange(embs);
                }
                index += actualBatchSize;
            }
            catch (Exception ex) when (IsOnnxMemoryException(ex))
            {
                AppLogger.Warn($"Embedding library batch memory pressure item={itemName} batch={actualBatchSize}: {ex.Message}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (actualBatchSize > 1)
                {
                    batchSize = Math.Max(1, actualBatchSize / 2);
                    continue;
                }

                // 单张降级
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var emb = await extractor.GetEmbeddingAsync(batch[0]);
                    if (emb != null)
                    {
                        result.Add(emb);
                    }
                }
                catch (Exception singleEx)
                {
                    AppLogger.Warn($"Embedding library skipped image item={itemName} file={Path.GetFileName(batch[0])}: {singleEx.Message}");
                }
                index++;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Embedding library batch failed item={itemName} batch={actualBatchSize}: {ex.Message}");

                if (actualBatchSize > 1)
                {
                    batchSize = 1;
                    continue;
                }

                try
                {
                    ct.ThrowIfCancellationRequested();
                    var emb = await extractor.GetEmbeddingAsync(batch[0]);
                    if (emb != null)
                    {
                        result.Add(emb);
                    }
                }
                catch (Exception singleEx)
                {
                    AppLogger.Warn($"Embedding library skipped image item={itemName} file={Path.GetFileName(batch[0])}: {singleEx.Message}");
                }
                index++;
            }
        }

        return result;
    }

    private static bool IsOnnxMemoryException(Exception ex)
    {
        return (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException || ex is OutOfMemoryException) &&
               (ex.Message.Contains("Available memory", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("AllocateRawInternal", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("BFCArena", StringComparison.OrdinalIgnoreCase) ||
                ex is OutOfMemoryException);
    }
}
