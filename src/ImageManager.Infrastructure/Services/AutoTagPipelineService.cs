using System.Collections.Concurrent;
using System.Threading.Channels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

public readonly record struct AutoTagPipelineProgress(
    string Phase, int Processed, int Total, string StatusText);

public readonly record struct FolderTagActionResult(
    string Action,   // "Start" / "Resume" / "NewFiles" / "Blocked" / "Busy" / "Retry"
    string Message,  // User-facing message
    bool CanProceed);

public class AutoTagPipelineService
{
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly ITagMappingRepository _mappingRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly IAutoTagStateRepository _stateRepo;
    private double _confidenceThreshold = 0.35;
    private int _maxTagsPerImage = 20;
    private int _maxConcurrency = 1;  // GPU 推理时 1，避免显存争抢
    private List<ImageMeta>? _cachedMetas;
    private string? _cachedFolderPath;

    public event Action<AutoTagPipelineProgress>? ProgressChanged;

    public AutoTagPipelineService(
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ITagMappingRepository mappingRepo,
        IEnsembleTagService tagService,
        IAutoTagStateRepository stateRepo)
    {
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _mappingRepo = mappingRepo;
        _tagService = tagService;
        _stateRepo = stateRepo;
    }

    public void Configure(double confidenceThreshold, int maxTagsPerImage, int maxConcurrency = 1)
    {
        _confidenceThreshold = confidenceThreshold;
        _maxTagsPerImage = maxTagsPerImage;
        _maxConcurrency = maxConcurrency;
    }

    // ==================== Situation Assessment ====================

    public async Task<FolderTagActionResult> DetermineActionAsync(long folderId, long folderFileCount)
    {
        var state = await _stateRepo.GetStateAsync(folderId);

        if (state == null)
            return new FolderTagActionResult("Start", "开始对文件夹进行自动打标？", true);

        return state.Status switch
        {
            "Processing" => new FolderTagActionResult("Recover",
                $"检测到上次打标中断（已完成 {state.Processed}/{state.TotalFiles}）。\n是否清理并重新打标？", true),

            "Done" when state.LastFileCount == folderFileCount => new FolderTagActionResult("ReTag",
                "此文件夹已完成打标。\n是否删除所有自动标签并重新打标？\n（手动标签不受影响）", true),

            "Done" when state.LastFileCount < folderFileCount => new FolderTagActionResult("NewFiles",
                $"此文件夹已打标，但检测到 {folderFileCount - state.LastFileCount} 张新图片。是否仅对新图片打标？", true),

            "Done" => new FolderTagActionResult("Blocked",
                "此文件夹已完成打标，无需重复。", false),

            "AwaitingReview" => new FolderTagActionResult("Resume",
                "此文件夹推理已完成，是否直接确认标签？（不会重新推理）\n选择\"否\"可重新打标", true),

            "Failed" => new FolderTagActionResult("Retry",
                $"上次打标失败。错误：{state.ErrorMsg ?? "未知"}\n是否重新打标？", true),

            _ => new FolderTagActionResult("Start", "开始对文件夹进行自动打标？", true)
        };
    }

    // ==================== Phase 1: Inference ====================

    public async Task RunInferenceAsync(long folderId, List<(long Id, string FilePath)> metas, string action)
    {
        var state = new AutoTagState
        {
            FolderId = folderId,
            Status = "Processing",
            TotalFiles = metas.Count,
            Processed = 0,
            LastFileCount = metas.Count,
            StartedAt = DateTime.UtcNow
        };
        await _stateRepo.UpsertStateAsync(state);

        if (action == "Resume")
        {
            // Clear only unconfirmed translations (keep confirmed)
            var existingTranslations = await _stateRepo.GetTranslationsAsync(folderId);
            foreach (var t in existingTranslations)
            {
                if (!t.IsConfirmed)
                    await _stateRepo.DeleteTranslationAsync(folderId, t.EnglishTag);
            }
        }
        else
        {
            await _stateRepo.DeleteTranslationsAsync(folderId);
        }

        var channel = Channel.CreateBounded<(long ImageId, string FilePath, List<TagPrediction> Predictions)>(
            new BoundedChannelOptions(50) { SingleWriter = false, SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait });

        var ioSemaphore = new SemaphoreSlim(_maxConcurrency);
        int processed = 0;
        var errors = new ConcurrentQueue<string>();

        // Producer tasks
        var produceTasks = metas.Select(async meta =>
        {
            try
            {
                await ioSemaphore.WaitAsync();
                try
                {
                    var predictions = await _tagService.PredictAsync(meta.FilePath);
                    var filtered = predictions
                        .Where(p => p.Confidence >= _confidenceThreshold)
                        .Take(_maxTagsPerImage)
                        .ToList();
                    await channel.Writer.WriteAsync((meta.Id, meta.FilePath, filtered));
                }
                finally { ioSemaphore.Release(); }
            }
            catch (Exception ex)
            {
                errors.Enqueue($"{Path.GetFileName(meta.FilePath)}: {ex.Message}");
            }
        });

        // Consumer
        var consumerTask = Task.Run(async () =>
        {
            var completed = 0;
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                try
                {
                    if (item.Predictions.Count > 0)
                    {
                        var tagNames = item.Predictions.Select(p => p.TagName).ToList();
                        await _metaRepo.AddAutoTagsAsync(item.ImageId, tagNames);
                    }
                    Interlocked.Increment(ref processed);
                    completed++;
                    if (completed % 10 == 0 || completed == metas.Count)
                    {
                        await _stateRepo.UpsertStateAsync(new AutoTagState
                        {
                            FolderId = folderId, Status = "Processing",
                            TotalFiles = metas.Count, Processed = processed,
                            LastFileCount = metas.Count
                        });
                        ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                            "Inference", processed, metas.Count,
                            $"正在推理图片标签... {processed}/{metas.Count}"));
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue($"consumer: {ex.Message}");
                }
            }

            if (!errors.IsEmpty)
            {
                var msg = string.Join("\n", errors.Take(5));
                if (errors.Count > 5) msg += $"\n... 及其他 {errors.Count - 5} 个错误";
                ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                    "Error", errors.Count, metas.Count, msg));
            }
        });

        _ = Task.Run(async () =>
        {
            await Task.WhenAll(produceTasks);
            channel.Writer.Complete();
        });

        await consumerTask;

        await _stateRepo.UpsertStateAsync(new AutoTagState
        {
            FolderId = folderId, Status = "AwaitingReview",
            TotalFiles = metas.Count, Processed = metas.Count,
            LastFileCount = metas.Count,
            CompletedAt = DateTime.UtcNow
        });
    }

    // ==================== Phase 2: Translation ====================

    public async Task TranslateAndPrepareReviewAsync(long folderId, string folderPath)
    {
        ProgressChanged?.Invoke(new AutoTagPipelineProgress(
            "Translation", 0, 0, "正在收集标签..."));

        // Collect all unique English auto-tags for this folder
        // Use GetByFolderAsync (LIKE path prefix) in case FolderId isn't set on all images
        var metas = await _metaRepo.GetByFolderAsync(folderPath);
        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
        {
            foreach (var tag in meta.Tags)
                allTags.Add(tag.Name);
        }

        // Filter: keep only English-only tags (those not in TagMapping as already confirmed)
        var existingMappings = await _mappingRepo.GetAllAsync();
        var mappedSet = new HashSet<string>(
            existingMappings.Select(m => m.EnglishName), StringComparer.OrdinalIgnoreCase);

        // Save auto-confirmed (already mapped) entries and replace English→Chinese in ImageTag
        foreach (var mapping in existingMappings)
        {
            if (!allTags.Contains(mapping.EnglishName)) continue;

            // Save to review data
            await _stateRepo.SaveTranslationAsync(folderId, mapping.EnglishName,
                mapping.ChineseName, null, isConfirmed: true, isExistingMapping: true);

            // Actually replace the English tag with Chinese on all images in this folder
            var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(mapping.ChineseName);
            foreach (var meta in metas)
            {
                var hasTag = meta.Tags.Any(t =>
                    string.Equals(t.Name, mapping.EnglishName, StringComparison.OrdinalIgnoreCase));
                if (hasTag)
                {
                    try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, mapping.EnglishName, chineseTagId); }
                    catch { }
                }
            }
        }

        var toTranslate = allTags.Where(t => !mappedSet.Contains(t)).ToList();

        // Save untranslated tags as-is → ChineseTagLibrary provides Chinese at review time
        foreach (var tag in toTranslate)
        {
            await _stateRepo.SaveTranslationAsync(folderId, tag,
                null, null, isConfirmed: false, isExistingMapping: false);
        }

        ProgressChanged?.Invoke(new AutoTagPipelineProgress(
            "Translation", toTranslate.Count, toTranslate.Count, "翻译完成"));
    }

    // ==================== Review Data ====================

    public async Task<List<(string EnglishTag, string ChineseTranslation, string? UserEditedText,
        bool IsConfirmed, bool IsExistingMapping, int ImageCount)>> GetReviewDataAsync(long folderId, string folderPath)
    {
        var translations = await _stateRepo.GetTranslationsAsync(folderId);
        var metas = await _metaRepo.GetByFolderAsync(folderPath);

        var tagImageCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
        {
            foreach (var tag in meta.Tags)
            {
                var name = tag.Name;
                if (tagImageCount.ContainsKey(name))
                    tagImageCount[name]++;
                else
                    tagImageCount[name] = 1;
            }
        }

        return translations.Select(t => (
            EnglishTag: t.EnglishTag,
            ChineseTranslation: t.ChineseTranslation ?? "",
            UserEditedText: t.UserEditedText,
            IsConfirmed: t.IsConfirmed,
            IsExistingMapping: t.IsExistingMapping,
            ImageCount: tagImageCount.TryGetValue(t.EnglishTag, out var c) ? c : 0
        )).OrderByDescending(t => t.IsExistingMapping)
          .ThenBy(t => t.IsConfirmed)
          .ThenByDescending(t => t.ImageCount)
          .ToList();
    }

    // ==================== Confirm ====================

    public async Task ConfirmTagAsync(long folderId, string folderPath, string englishTag, string chineseName)
    {
        await _mappingRepo.UpsertAsync(englishTag, chineseName);
        await _stateRepo.SaveTranslationAsync(folderId, englishTag, chineseName,
            null, isConfirmed: true, isExistingMapping: false);

        // Use cached metas if preloaded, otherwise load on demand
        var metas = (_cachedFolderPath == folderPath && _cachedMetas != null)
            ? _cachedMetas
            : (await _metaRepo.GetByFolderAsync(folderPath)).ToList();
        var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(chineseName);

        foreach (var meta in metas)
        {
            var hasEnglishTag = meta.Tags.Any(t =>
                string.Equals(t.Name, englishTag, StringComparison.OrdinalIgnoreCase));
            if (!hasEnglishTag) continue;

            await _metaRepo.ReplaceAutoTagAsync(meta.Id, englishTag, chineseTagId);
        }
    }

    public async Task PreloadMetasAsync(string folderPath)
    {
        _cachedMetas = (await _metaRepo.GetByFolderAsync(folderPath)).ToList();
        _cachedFolderPath = folderPath;
    }

    public void ClearMetaCache()
    {
        _cachedMetas = null;
        _cachedFolderPath = null;
    }

    public async Task DeleteAutoTagAsync(long folderId, string folderPath, string englishTag)
    {
        await _stateRepo.DeleteTranslationAsync(folderId, englishTag);
        var metas = (_cachedFolderPath == folderPath && _cachedMetas != null)
            ? _cachedMetas
            : (await _metaRepo.GetByFolderAsync(folderPath)).ToList();
        foreach (var meta in metas)
        {
            try { await _metaRepo.DeleteAutoTagFromImageAsync(meta.Id, englishTag); }
            catch { }
        }
    }

    public async Task<List<string>> GetImagesWithTagAsync(long folderId, string folderPath, string englishTag)
    {
        var metas = (_cachedFolderPath == folderPath && _cachedMetas != null)
            ? _cachedMetas
            : (await _metaRepo.GetByFolderAsync(folderPath)).ToList();
        return metas
            .Where(m => m.Tags.Any(t =>
                string.Equals(t.Name, englishTag, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.FilePath)
            .ToList();
    }
}
