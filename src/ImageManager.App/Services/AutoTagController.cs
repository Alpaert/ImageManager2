using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Services;

namespace ImageManager.App.Services;

public class AutoTagController
{
    private readonly AutoTagPipelineService _pipeline;
    private readonly IFolderRepository _folderRepo;
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagMappingRepository _mappingRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly ITagRepository _tagRepo;
    private readonly IAutoTagStateRepository _stateRepo;
    private readonly ThumbnailCacheService _thumbCache;
    private readonly ChineseTagLibrary _chineseLib;
    private readonly TagServiceFactory _factory;
    private readonly DeepSeekRecommendService _recommendService;
    private readonly ArtistEmbeddingStore _artistStore;
    private readonly PixaiTagService _pixaiService;
    private string _currentFolderPath = string.Empty;
    private long _currentFolderId;
    private TagMode _currentMode = TagMode.Ensemble;

    public event Action<AutoTagPipelineProgress>? ProgressChanged;
    public event Action? TranslationReady;

    public AutoTagController(
        AutoTagPipelineService pipeline,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagMappingRepository mappingRepo,
        IEnsembleTagService tagService,
        ITagRepository tagRepo,
        IAutoTagStateRepository stateRepo,
        ThumbnailCacheService thumbCache,
        ChineseTagLibrary chineseLib,
        TagServiceFactory factory,
        DeepSeekRecommendService recommendService,
        ArtistEmbeddingStore artistStore,
        PixaiTagService pixaiService)
    {
        _pipeline = pipeline;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _mappingRepo = mappingRepo;
        _tagService = tagService;
        _tagRepo = tagRepo;
        _stateRepo = stateRepo;
        _thumbCache = thumbCache;
        _chineseLib = chineseLib;
        _factory = factory;
        _recommendService = recommendService;
        _artistStore = artistStore;
        _pixaiService = pixaiService;

        _pipeline.ProgressChanged += p => ProgressChanged?.Invoke(p);
        _tagService.ProgressChanged += p => ProgressChanged?.Invoke(
            new AutoTagPipelineProgress("Model", p.Processed, p.Total, p.StatusText));
    }

    public void Configure(TagMode mode, double confidenceThreshold, int maxTagsPerImage,
        double pixaiThreshold, double artistMatchThreshold, string? apiKey)
    {
        _currentMode = mode;

        if (mode == TagMode.SingleModel)
        {
            var singleSvc = _factory.Create(TagMode.SingleModel) as SingleModelTagService;
            singleSvc?.Configure(confidenceThreshold);
        }
        else
        {
            var ensSvc = _factory.Create(TagMode.Ensemble) as EnsembleTagService;
            ensSvc?.Configure(new MergeConfig
            {
                MaxTags = maxTagsPerImage,
                ModelThresholds = new Dictionary<string, double>
                {
                    ["pixai"] = pixaiThreshold
                },
                ArtistMatchThreshold = artistMatchThreshold
            });
        }

        _pipeline.Configure(confidenceThreshold, maxTagsPerImage);
        if (!string.IsNullOrEmpty(apiKey))
            _recommendService.SetApiKey(apiKey);
    }

    public string ModelPath =>
        System.IO.Path.Combine(_thumbCache.CacheDirectory, "models");

    public bool IsModelLoaded => _tagService.IsModelLoaded;

    public async Task<List<TagPrediction>> TestPredictAsync(string imagePath)
        => await _tagService.PredictAsync(imagePath);

    public async Task LoadModelAsync()
    {
        var svc = _factory.Create(_currentMode);
        if (!svc.IsModelLoaded)
            await svc.LoadModelAsync(ModelPath);
    }

    public async Task<FolderTagActionResult> DetermineActionAsync(FolderInfo folder)
    {
        var metas = await _metaRepo.GetByFolderIdAsync(folder.Id);
        return await _pipeline.DetermineActionAsync(folder.Id, metas.Count);
    }

    public async Task RunPipelineAsync(FolderInfo folder, List<string> filePaths, string action)
    {
        _currentFolderPath = folder.Path;
        _currentFolderId = folder.Id;

        // Resume：推理已完成，跳过推理直接 AutoConfirm
        if (action == "Resume")
        {
            AppLogger.Tag("Pipeline", "Resume: 跳过推理，直接 AutoConfirm");
            await AutoConfirmAllAsync();
            TranslationReady?.Invoke();
            return;
        }

        // Recover / ReTag / Retry：先清旧自动标签再重新推理
        if (action is "Recover" or "ReTag" or "Retry")
        {
            AppLogger.Tag("Pipeline", $"{action}: 删除旧自动标签");
            await DeleteAllAutoTagsAsync(_currentFolderPath);
        }

        // Resolve ImageMeta IDs from file paths
        var metas = new List<(long Id, string FilePath)>();
        foreach (var path in filePaths)
        {
            var meta = await _metaRepo.GetByPathAsync(path);
            metas.Add(meta != null ? (meta.Id, path) : (0L, path));
        }

        // Phase 1: Inference
        await _pipeline.RunInferenceAsync(folder.Id, metas, action);

        // Phase 2: Auto-confirm
        await AutoConfirmAllAsync();

        TranslationReady?.Invoke();
    }

    public async Task<List<TagTranslationItem>> GetReviewDataAsync()
    {
        var data = await _pipeline.GetReviewDataAsync(_currentFolderId, _currentFolderPath);
        return data.Select(d => new TagTranslationItem
        {
            EnglishTag = d.EnglishTag,
            ChineseTranslation = d.UserEditedText ?? d.ChineseTranslation,
            ImageCount = d.ImageCount,
            IsConfirmed = d.IsConfirmed,
            IsExistingMapping = d.IsExistingMapping,
            IsEditing = false
        }).ToList();
    }

    public async Task ConfirmTagAsync(TagTranslationItem item)
    {
        var chineseName = item.UserEditedText;
        if (string.IsNullOrWhiteSpace(chineseName))
            chineseName = item.ChineseTranslation;
        if (string.IsNullOrWhiteSpace(chineseName)) return;

        await _pipeline.ConfirmTagAsync(_currentFolderId, _currentFolderPath, item.EnglishTag, chineseName);
        item.IsConfirmed = true;
    }

    /// <summary>自动确认所有标签：查中文库替换 English→Chinese，跳过审核窗口</summary>
    public async Task AutoConfirmAllAsync()
    {
        var metas = await _metaRepo.GetByFolderAsync(_currentFolderPath);
        var allEnglishTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
            foreach (var tag in meta.Tags)
                allEnglishTags.Add(tag.Name);

        AppLogger.Tag("AutoConfirm", $"folder={_currentFolderPath} images={metas.Count} uniqueTags={allEnglishTags.Count}");

        int confirmed = 0;
        foreach (var enTag in allEnglishTags)
        {
            var zh = _chineseLib.Lookup(enTag);
            if (string.IsNullOrWhiteSpace(zh))
            {
                zh = enTag;  // 无翻译 → 使用英文原名
            }

            // Save to TagMapping
            await _mappingRepo.UpsertAsync(enTag, zh);

            // Replace English → Chinese in ImageTag
            var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(zh);
            foreach (var meta in metas)
            {
                var hasTag = meta.Tags.Any(t =>
                    string.Equals(t.Name, enTag, StringComparison.OrdinalIgnoreCase));
                if (hasTag)
                {
                    try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, enTag, chineseTagId); }
                    catch { /* best effort */ }
                }
            }
            confirmed++;
        }

        await MarkFolderDoneAsync(_currentFolderId);
        AppLogger.Tag("AutoConfirm", $"完成 confirmed={confirmed}");
    }

    public async Task ConfirmAllAsync(List<TagTranslationItem> items)
    {
        await _pipeline.PreloadMetasAsync(_currentFolderPath);
        foreach (var item in items.Where(i => !i.IsConfirmed))
            await ConfirmTagAsync(item);
        _pipeline.ClearMetaCache();
    }

    public async Task SaveDraftAsync(List<TagTranslationItem> items)
    {
        foreach (var item in items.Where(i => !i.IsConfirmed && !i.IsExistingMapping))
        {
            var edited = item.UserEditedText;
            if (string.IsNullOrWhiteSpace(edited)) edited = item.ChineseTranslation;
            await _stateRepo.SaveTranslationAsync(_currentFolderId, item.EnglishTag,
                string.IsNullOrWhiteSpace(item.ChineseTranslation) ? null : item.ChineseTranslation,
                string.IsNullOrWhiteSpace(edited) ? null : edited,
                isConfirmed: false, isExistingMapping: false);
        }
    }

    public async Task<List<string>> GetImagesWithTagAsync(string englishTag)
    {
        return await _pipeline.GetImagesWithTagAsync(_currentFolderId, _currentFolderPath, englishTag);
    }

    public async Task<List<TagTranslationItem>> RunSingleImageAsync(string filePath)
    {
        var predictions = await _tagService.PredictAsync(filePath);
        var filtered = predictions
            .Where(p => p.Confidence >= 0.1) // low floor for manual review
            .Take(300)
            .ToList();

        var existingMappings = await _mappingRepo.GetAllAsync();
        var items = new List<TagTranslationItem>();
        foreach (var pred in filtered)
        {
            var existing = existingMappings.FirstOrDefault(m =>
                string.Equals(m.EnglishName, pred.TagName, StringComparison.OrdinalIgnoreCase));
            // Chinese: prefer pred.ChineseName → TagMapping → ChineseTagLibrary → English fallback
            var chinese = pred.ChineseName ?? existing?.ChineseName ?? _chineseLib.Lookup(pred.TagName) ?? pred.TagName;
            items.Add(new TagTranslationItem
            {
                EnglishTag = pred.TagName,
                ChineseTranslation = chinese,
                ImageCount = 1,
                IsConfirmed = true,      // auto-confirmed — no review needed
                IsExistingMapping = existing != null
            });
        }

        return items;
    }

    public async Task SaveMappingsOnlyAsync(List<TagTranslationItem> items)
    {
        foreach (var item in items)
        {
            var chinese = item.UserEditedText;
            if (string.IsNullOrWhiteSpace(chinese)) chinese = item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chinese)) continue;
            await _mappingRepo.UpsertAsync(item.EnglishTag, chinese);
        }
    }

    public async Task SaveMappingsAndTagsAsync(string filePath, List<TagTranslationItem> items)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        foreach (var item in items)
        {
            var chinese = item.UserEditedText;
            if (string.IsNullOrWhiteSpace(chinese)) chinese = item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chinese)) continue;

            // Save mapping for future reuse
            await _mappingRepo.UpsertAsync(item.EnglishTag, chinese);

            // If confirmed, write to image
            if (item.IsConfirmed)
            {
                var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(chinese);
                try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, item.EnglishTag, chineseTagId); }
                catch { }
                await _metaRepo.AddAutoTagsAsync(meta.Id, new List<string> { chinese });
            }
        }
    }

    public async Task WriteConfirmedTagsAsync(string filePath, List<TagTranslationItem> items)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        foreach (var item in items.Where(i => i.IsConfirmed))
        {
            var chineseName = item.UserEditedText;
            if (string.IsNullOrWhiteSpace(chineseName)) chineseName = item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chineseName)) continue;

            await _mappingRepo.UpsertAsync(item.EnglishTag, chineseName);

            var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(chineseName);
            // Add as confirmed auto-tag
            try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, item.EnglishTag, chineseTagId); }
            catch { /* might not exist yet — just add directly */ }
            await _metaRepo.AddAutoTagsAsync(meta.Id, new List<string> { chineseName });
        }
    }

    public async Task DeleteTagAsync(TagTranslationItem item)
    {
        await _pipeline.DeleteAutoTagAsync(_currentFolderId, _currentFolderPath, item.EnglishTag);
    }

    /// <summary>删除文件夹下所有自动标签（高效批量 SQL）</summary>
    public async Task<int> DeleteAllAutoTagsAsync(string folderPath)
    {
        var count = await _metaRepo.DeleteAllAutoTagsByFolderAsync(folderPath);
        AppLogger.Tag("DeleteAutoTags", $"folder={folderPath} deleted={count}");
        return count;
    }

    public async Task MarkFolderDoneAsync(long folderId)
    {
        var state = await _stateRepo.GetStateAsync(folderId);
        if (state != null)
        {
            state.Status = "Done";
            await _stateRepo.UpsertStateAsync(state);
        }
    }

    // ==================== 画师嵌入库管理 ====================

    /// <summary>注册画师：从参考图提取嵌入，加入画师库</summary>
    public async Task<bool> RegisterArtistAsync(string artistName, string imagePath)
    {
        if (!_pixaiService.IsModelLoaded)
        {
            AppLogger.Warn("RegisterArtist: PixAI 模型未加载");
            return false;
        }

        var embedding = await _pixaiService.GetEmbeddingAsync(imagePath);
        if (embedding == null)
        {
            AppLogger.Warn($"RegisterArtist: 提取嵌入失败 image={imagePath}");
            return false;
        }

        _artistStore.Add(artistName, embedding);
        var dbPath = Path.Combine(_thumbCache.CacheDirectory, "models", "artist_embeddings.bin");
        _artistStore.Save(dbPath);

        AppLogger.Tag("Artist", $"注册画师 artist={artistName} storeCount={_artistStore.Count}");
        return true;
    }

    /// <summary>注册画师：使用已计算的嵌入（多图均值）直接加入</summary>
    public void RegisterArtistWithEmbeddingAsync(string artistName, float[] embedding, int imageCount)
    {
        _artistStore.Add(artistName, embedding, imageCount);
        var modelsDir = Path.Combine(_thumbCache.CacheDirectory, "models");
        var dbPath = Path.Combine(modelsDir, "artist_embeddings.bin");
        _artistStore.Save(dbPath);
        _chineseLib.Register(artistName, artistName);
        var namesPath = Path.Combine(modelsDir, "artist_names.txt");
        _chineseLib.SaveArtistNames(namesPath);
        AppLogger.Tag("Artist", $"注册画师 artist={artistName} imgs={imageCount} storeCount={_artistStore.Count}");
    }

    /// <summary>获取画师库中的画师数量</summary>
    public int GetArtistStoreCount() => _artistStore.Count;
}
