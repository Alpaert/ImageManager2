using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 后台角色比对服务：扫描指定目录下图片，查询已有向量并在后台批量比对角色库，将匹配结果写入标签。
/// </summary>
public class CharacterMatchingService : ICharacterMatchingService
{
    private readonly IImageMetaRepository _metaRepo;
    private readonly IImageEmbeddingRepository _embeddingRepo;
    private readonly CharacterEmbeddingStore _characterStore;

    public CharacterMatchingService(
        IImageMetaRepository metaRepo,
        IImageEmbeddingRepository embeddingRepo,
        CharacterEmbeddingStore characterStore)
    {
        _metaRepo = metaRepo ?? throw new ArgumentNullException(nameof(metaRepo));
        _embeddingRepo = embeddingRepo ?? throw new ArgumentNullException(nameof(embeddingRepo));
        _characterStore = characterStore ?? throw new ArgumentNullException(nameof(characterStore));
    }

    public Task<CharacterMatchResult> MatchCharactersAsync(
        CharacterMatchRequest request,
        IProgress<CharacterMatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(request.FolderPath) || !Directory.Exists(request.FolderPath))
            {
                return new CharacterMatchResult(
                    TotalImages: 0,
                    WithEmbeddings: 0,
                    MissingEmbeddings: 0,
                    MatchedCount: 0,
                    WrittenTagsCount: 0,
                    Completed: true,
                    Cancelled: false);
            }

            // 1. 递归或非递归扫描 request.FolderPath 下图片文件
            var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            List<string> imageFiles;
            try
            {
                imageFiles = Directory.EnumerateFiles(request.FolderPath, "*.*", searchOption)
                    .Where(FileTypeConstants.IsImageFile)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"CharacterMatchingService: 扫描目录失败: {ex.Message}");
                return new CharacterMatchResult(
                    TotalImages: 0,
                    WithEmbeddings: 0,
                    MissingEmbeddings: 0,
                    MatchedCount: 0,
                    WrittenTagsCount: 0,
                    Completed: false,
                    Cancelled: false,
                    ErrorMessage: ex.Message);
            }

            int totalImages = imageFiles.Count;
            if (totalImages == 0)
            {
                return new CharacterMatchResult(
                    TotalImages: 0,
                    WithEmbeddings: 0,
                    MissingEmbeddings: 0,
                    MatchedCount: 0,
                    WrittenTagsCount: 0,
                    Completed: true,
                    Cancelled: false);
            }

            // 2. 若角色库为空，返回错误结果
            if (_characterStore.Count == 0)
            {
                return new CharacterMatchResult(
                    TotalImages: 0,
                    WithEmbeddings: 0,
                    MissingEmbeddings: 0,
                    MatchedCount: 0,
                    WrittenTagsCount: 0,
                    Completed: false,
                    Cancelled: false,
                    ErrorMessage: "角色 Embedding 库为空，请先构建角色库");
            }

            // 3. 查询已有 embedding 并过滤出匹配的图片
            var allEmbeddings = await _embeddingRepo.GetByFolderPrefixAsync(request.FolderPath, "pixai", "v0.9");
            var imageFileSet = new HashSet<string>(imageFiles, StringComparer.OrdinalIgnoreCase);
            var embeddings = allEmbeddings.Where(e => imageFileSet.Contains(e.FilePath)).ToList();

            // 4. 计算 missing 数量
            int withEmbeddings = embeddings.Count;
            int missing = Math.Max(0, totalImages - withEmbeddings);

            if (embeddings.Count == 0)
            {
                progress?.Report(new CharacterMatchProgress(
                    ProcessedCount: 0,
                    TotalCount: totalImages,
                    MatchedCount: 0,
                    MissingVectorCount: missing,
                    WrittenTagsCount: 0,
                    Stage: "未找到任何已有 Embedding 向量"));

                return new CharacterMatchResult(
                    TotalImages: totalImages,
                    WithEmbeddings: 0,
                    MissingEmbeddings: missing,
                    MatchedCount: 0,
                    WrittenTagsCount: 0,
                    Completed: true,
                    Cancelled: false);
            }

            // 5. 分批比对与写入
            int batchSize = request.BatchSize > 0 ? request.BatchSize : 100;
            int matchedCount = 0;
            int writtenTags = 0;
            int processedCount = 0;
            var pending = new List<(long ImageMetaId, string TagName)>();

            progress?.Report(new CharacterMatchProgress(
                ProcessedCount: 0,
                TotalCount: totalImages,
                MatchedCount: 0,
                MissingVectorCount: missing,
                WrittenTagsCount: 0,
                Stage: "开始比对角色..."));

            try
            {
                for (int i = 0; i < embeddings.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new CharacterMatchResult(
                            TotalImages: totalImages,
                            WithEmbeddings: withEmbeddings,
                            MissingEmbeddings: missing,
                            MatchedCount: matchedCount,
                            WrittenTagsCount: writtenTags,
                            Completed: false,
                            Cancelled: true);
                    }

                    var item = embeddings[i];
                    var matches = _characterStore.SearchTop(item.Embedding, request.MatchThreshold, request.MaxMatchesPerImage);
                    if (matches.Count > 0)
                    {
                        matchedCount++;
                        foreach (var match in matches)
                        {
                            pending.Add((item.ImageMetaId, match.CharacterName));
                        }
                    }

                    processedCount++;

                    // 批满时提交数据库写入
                    if (pending.Count >= batchSize)
                    {
                        var written = await _embeddingRepo.AddCharacterEmbeddingTagsAsync(pending, "CharacterEmbedding");
                        writtenTags += written;
                        pending.Clear();

                        progress?.Report(new CharacterMatchProgress(
                            ProcessedCount: processedCount,
                            TotalCount: totalImages,
                            MatchedCount: matchedCount,
                            MissingVectorCount: missing,
                            WrittenTagsCount: writtenTags,
                            Stage: $"已写入角色标签批次 ({writtenTags}个)"));
                    }

                    // 进度合并通知：每隔 100 张或处理完毕时触发
                    if (processedCount % 100 == 0 || processedCount == embeddings.Count)
                    {
                        progress?.Report(new CharacterMatchProgress(
                            ProcessedCount: processedCount,
                            TotalCount: totalImages,
                            MatchedCount: matchedCount,
                            MissingVectorCount: missing,
                            WrittenTagsCount: writtenTags,
                            Stage: $"正在比对角色 ({processedCount}/{withEmbeddings})"));
                    }
                }

                // 提交剩余待写入标签
                if (pending.Count > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new CharacterMatchResult(
                            TotalImages: totalImages,
                            WithEmbeddings: withEmbeddings,
                            MissingEmbeddings: missing,
                            MatchedCount: matchedCount,
                            WrittenTagsCount: writtenTags,
                            Completed: false,
                            Cancelled: true);
                    }

                    var written = await _embeddingRepo.AddCharacterEmbeddingTagsAsync(pending, "CharacterEmbedding");
                    writtenTags += written;
                    pending.Clear();
                }

                progress?.Report(new CharacterMatchProgress(
                    ProcessedCount: processedCount,
                    TotalCount: totalImages,
                    MatchedCount: matchedCount,
                    MissingVectorCount: missing,
                    WrittenTagsCount: writtenTags,
                    Stage: "角色比对完成"));

                return new CharacterMatchResult(
                    TotalImages: totalImages,
                    WithEmbeddings: withEmbeddings,
                    MissingEmbeddings: missing,
                    MatchedCount: matchedCount,
                    WrittenTagsCount: writtenTags,
                    Completed: true,
                    Cancelled: false);
            }
            catch (OperationCanceledException)
            {
                return new CharacterMatchResult(
                    TotalImages: totalImages,
                    WithEmbeddings: withEmbeddings,
                    MissingEmbeddings: missing,
                    MatchedCount: matchedCount,
                    WrittenTagsCount: writtenTags,
                    Completed: false,
                    Cancelled: true);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"CharacterMatchingService: 比对发生异常: {ex.Message}");
                return new CharacterMatchResult(
                    TotalImages: totalImages,
                    WithEmbeddings: withEmbeddings,
                    MissingEmbeddings: missing,
                    MatchedCount: matchedCount,
                    WrittenTagsCount: writtenTags,
                    Completed: false,
                    Cancelled: false,
                    ErrorMessage: ex.Message);
            }
        }, cancellationToken);
    }
}
