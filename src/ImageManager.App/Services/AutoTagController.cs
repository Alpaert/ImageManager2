using ImageManager.App.ViewModels;
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
    private readonly IAutoTagService _tagService;
    private readonly DeepSeekTranslationService _translationService;
    private readonly ITagRepository _tagRepo;
    private readonly IAutoTagStateRepository _stateRepo;
    private readonly ThumbnailCacheService _thumbCache;
    private string _currentFolderPath = string.Empty;
    private long _currentFolderId;

    public event Action<AutoTagPipelineProgress>? ProgressChanged;
    public event Action? TranslationReady;

    public AutoTagController(
        AutoTagPipelineService pipeline,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagMappingRepository mappingRepo,
        IAutoTagService tagService,
        DeepSeekTranslationService translationService,
        ITagRepository tagRepo,
        IAutoTagStateRepository stateRepo,
        ThumbnailCacheService thumbCache)
    {
        _pipeline = pipeline;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _mappingRepo = mappingRepo;
        _tagService = tagService;
        _translationService = translationService;
        _tagRepo = tagRepo;
        _stateRepo = stateRepo;
        _thumbCache = thumbCache;

        _pipeline.ProgressChanged += p => ProgressChanged?.Invoke(p);
        _tagService.ProgressChanged += p => ProgressChanged?.Invoke(
            new AutoTagPipelineProgress("Model", p.Processed, p.Total, p.StatusText));
    }

    public void Configure(double confidenceThreshold, int maxTagsPerImage, string? apiKey)
    {
        _pipeline.Configure(confidenceThreshold, maxTagsPerImage);
        if (!string.IsNullOrEmpty(apiKey))
            _translationService.SetApiKey(apiKey);
    }

    public string ModelPath =>
        System.IO.Path.Combine(_thumbCache.CacheDirectory, "models", "wd14");

    public bool IsModelLoaded => _tagService.IsModelLoaded;

    public async Task<List<TagPrediction>> TestPredictAsync(string imagePath)
        => await _tagService.PredictAsync(imagePath);

    public async Task LoadModelAsync()
    {
        var modelPath = ModelPath;
        if (!_tagService.IsModelLoaded)
            await _tagService.LoadModelAsync(modelPath);
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

        // Resolve ImageMeta IDs from file paths (may not have FolderId set)
        var metas = new List<(long Id, string FilePath)>();
        foreach (var path in filePaths)
        {
            var meta = await _metaRepo.GetByPathAsync(path);
            metas.Add(meta != null ? (meta.Id, path) : (0L, path));
        }

        // Phase 1: Inference
        await _pipeline.RunInferenceAsync(folder.Id, metas, action);

        // Phase 2: Translation (skip during Resume — preserve existing review state)
        if (action != "Resume")
            await _pipeline.TranslateAndPrepareReviewAsync(folder.Id, folder.Path);

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
            items.Add(new TagTranslationItem
            {
                EnglishTag = pred.TagName,
                ChineseTranslation = existing?.ChineseName ?? "",
                ImageCount = 1,
                IsConfirmed = existing != null,
                IsExistingMapping = existing != null
            });
        }

        // Translate untranslated
        var toTranslate = items.Where(i => !i.IsExistingMapping).Select(i => i.EnglishTag).ToList();
        if (toTranslate.Count > 0 && _translationService.IsAvailable)
        {
            var translations = await _translationService.TranslateBatchAsync(toTranslate);
            foreach (var item in items.Where(i => !i.IsExistingMapping))
            {
                if (translations.TryGetValue(item.EnglishTag, out var ch) && !string.IsNullOrWhiteSpace(ch))
                    item.ChineseTranslation = ch;
            }
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

    public async Task MarkFolderDoneAsync(long folderId)
    {
        var state = await _stateRepo.GetStateAsync(folderId);
        if (state != null)
        {
            state.Status = "Done";
            await _stateRepo.UpsertStateAsync(state);
        }
    }
}
