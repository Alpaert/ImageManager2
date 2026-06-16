using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Messages;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Orchestrates the auto-tag pipeline: model loading, inference, translation, review, and artist registration.
/// Lives in Infrastructure with no dependency on Avalonia UI.
/// Communicates progress and results via <see cref="IMessenger"/>.
/// All UI-thread marshalling is handled by injected <see cref="IDispatcher"/>.
/// </summary>
public class AutoTagOrchestrator : IDisposable
{
    private readonly AutoTagPipelineService _pipeline;
    private readonly IFolderRepository _folderRepo;
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagMappingRepository _mappingRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly ITagRepository _tagRepo;
    private readonly IAutoTagStateRepository _stateRepo;
    private readonly IThumbnailCacheService _thumbCache;
    private readonly ChineseTagLibrary _chineseLib;
    private readonly TagServiceFactory _factory;
    private readonly DeepSeekRecommendService _recommendService;
    private readonly ArtistEmbeddingStore _artistStore;
    private readonly PixaiTagService _pixaiService;
    private readonly IMessenger _messenger;
    private readonly IDispatcher _dispatcher;
    private string _currentFolderPath = string.Empty;
    private long _currentFolderId;
    private TagMode _currentMode = TagMode.Ensemble;
    private CancellationTokenSource? _cts;

    /// <summary>Paths actually processed in the last pipeline run (excluding skipped).</summary>
    public List<string> LastProcessedPaths { get; private set; } = new();

    public AutoTagOrchestrator(
        AutoTagPipelineService pipeline,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagMappingRepository mappingRepo,
        IEnsembleTagService tagService,
        ITagRepository tagRepo,
        IAutoTagStateRepository stateRepo,
        IThumbnailCacheService thumbCache,
        ChineseTagLibrary chineseLib,
        TagServiceFactory factory,
        DeepSeekRecommendService recommendService,
        ArtistEmbeddingStore artistStore,
        PixaiTagService pixaiService,
        IMessenger messenger,
        IDispatcher dispatcher)
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
        _messenger = messenger;
        _dispatcher = dispatcher;

        // Wire pipeline progress → messenger
        _pipeline.ProgressChanged += p =>
            _messenger.Send(new AutoTagProgressMessage(p.Phase, p.Processed, p.Total, p.StatusText));
        _tagService.ProgressChanged += p =>
            _messenger.Send(new AutoTagProgressMessage("Model", p.Processed, p.Total, p.StatusText));
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
        Path.Combine(_thumbCache.CacheDirectory, "models");

    public bool IsModelLoaded => _tagService.IsModelLoaded;

    public async Task<List<TagPrediction>> TestPredictAsync(string imagePath)
        => await _tagService.PredictAsync(imagePath);

    public async Task LoadModelAsync()
    {
        var svc = _factory.Create(_currentMode);
        if (!svc.IsModelLoaded)
            await svc.LoadModelAsync(ModelPath);
    }

    public async Task<FolderTagActionResult> DetermineActionAsync(long folderId)
    {
        var metas = folderId > 0 ? await _metaRepo.GetByFolderIdAsync(folderId) : new List<ImageMeta>();
        return await _pipeline.DetermineActionAsync(folderId, metas.Count);
    }

    public async Task RunPipelineAsync(long folderId, string folderPath, List<string> filePaths, string action)
    {
        _currentFolderPath = folderPath;
        _currentFolderId = folderId;

        filePaths = filePaths.Where(f => FileTypeConstants.IsImageFile(f)).ToList();
        if (filePaths.Count == 0)
        {
            LastProcessedPaths = new List<string>();
            _messenger.Send(new AutoTagProgressMessage("Done", 0, 0, "没有图片文件需要处理"));
            _messenger.Send(new TranslationReadyMessage());
            return;
        }

        var statusMap = await _metaRepo.GetStatusMapByPathsAsync(filePaths);

        var metas = new List<(long Id, string FilePath)>();
        foreach (var path in filePaths)
        {
            if (statusMap.TryGetValue(path, out var existing))
            {
                if (existing.Status == 1) { AppLogger.Info($"AutoTag skip: {path}"); continue; }
                metas.Add((existing.Id, path));
            }
            else
            {
                long newId = 0;
                try
                {
                    string md5;
                    using (var fs = File.OpenRead(path))
                        md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
                    var match = await _metaRepo.GetByFileHashAsync(md5);
                    if (match != null && !File.Exists(match.FilePath))
                    {
                        await _metaRepo.UpdateFilePathAsync(match.Id, path, folderId);
                        newId = match.Id;
                    }
                    else
                    {
                        var fi = new FileInfo(path);
                        var newMeta = new ImageMeta
                        {
                            FilePath = path, FileHash = md5, FolderId = folderId,
                            FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks
                        };
                        newId = await _metaRepo.UpsertAsync(newMeta);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"AutoTag: failed to process new file {Path.GetFileName(path)}: {ex.Message}");
                }
                metas.Add((newId, path));
            }
        }

        if (metas.Count == 0)
        {
            LastProcessedPaths = new List<string>();
            _messenger.Send(new AutoTagProgressMessage("Done", 0, 0, "全部图片已打标，跳过"));
            _messenger.Send(new TranslationReadyMessage());
            return;
        }

        LastProcessedPaths = metas.Select(m => m.FilePath).ToList();

        ResetCts();
        try
        {
            LastProcessedPaths = await _pipeline.RunInferenceAsync(folderId, metas, action, _cts!.Token);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("AutoTag pipeline cancelled by user");
        }

        _messenger.Send(new TranslationReadyMessage());
    }

    public async Task<List<TagTranslationDto>> GetReviewDataAsync()
    {
        var data = await _pipeline.GetReviewDataAsync(_currentFolderId, _currentFolderPath);
        return data.Select(d => new TagTranslationDto
        {
            EnglishTag = d.EnglishTag,
            ChineseTranslation = d.UserEditedText ?? d.ChineseTranslation,
            ImageCount = d.ImageCount,
            IsConfirmed = d.IsConfirmed,
            IsExistingMapping = d.IsExistingMapping
        }).ToList();
    }

    public async Task ConfirmTagAsync(string englishTag, string chineseName)
    {
        if (string.IsNullOrWhiteSpace(chineseName)) return;
        await _pipeline.ConfirmTagAsync(_currentFolderId, _currentFolderPath, englishTag, chineseName);
    }

    public async Task AutoConfirmAllAsync()
    {
        var metas = await _metaRepo.GetByFolderAsync(_currentFolderPath);

        var tagToImageIds = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
        {
            foreach (var tag in meta.Tags)
            {
                if (!tagToImageIds.TryGetValue(tag.Name, out var list))
                {
                    list = new List<long>();
                    tagToImageIds[tag.Name] = list;
                }
                list.Add(meta.Id);
            }
        }

        AppLogger.Tag("AutoConfirm", $"folder={_currentFolderPath} images={metas.Count} uniqueTags={tagToImageIds.Count}");

        int confirmed = 0;
        foreach (var (enTag, _) in tagToImageIds)
        {
            var zh = _chineseLib.Lookup(enTag);
            if (string.IsNullOrWhiteSpace(zh)) zh = enTag;
            await _mappingRepo.UpsertAsync(enTag, zh);
            confirmed++;
        }

        var batch = new List<(long ImageId, string EnglishName, long ChineseId)>();
        foreach (var (enTag, imageIds) in tagToImageIds)
        {
            var zh = _chineseLib.Lookup(enTag);
            if (string.IsNullOrWhiteSpace(zh)) zh = enTag;
            var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(zh);
            foreach (var imageId in imageIds)
                batch.Add((imageId, enTag, chineseTagId));
        }

        await _metaRepo.ReplaceAutoTagsBatchAsync(batch);

        if (_currentFolderId > 0) await MarkFolderDoneAsync(_currentFolderId);
        AppLogger.Tag("AutoConfirm", $"完成 confirmed={confirmed} batchSize={batch.Count}");
    }

    public async Task ConfirmAllAsync(List<TagTranslationDto> items)
    {
        try
        {
            await _pipeline.PreloadMetasAsync(_currentFolderPath);
            foreach (var item in items.Where(i => !i.IsConfirmed))
            {
                var chineseName = item.UserEditedText ?? item.ChineseTranslation;
                await ConfirmTagAsync(item.EnglishTag, chineseName);
            }
        }
        finally { _pipeline.ClearMetaCache(); }
    }

    public async Task SaveDraftAsync(List<TagTranslationDto> items)
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

    public async Task<List<TagTranslationDto>> RunSingleImageAsync(string filePath)
    {
        var predictions = await _tagService.PredictAsync(filePath);
        var filtered = predictions
            .Where(p => p.Confidence >= 0.1)
            .Take(300)
            .ToList();

        var existingMappings = await _mappingRepo.GetAllAsync();
        var items = new List<TagTranslationDto>();
        foreach (var pred in filtered)
        {
            var existing = existingMappings.FirstOrDefault(m =>
                string.Equals(m.EnglishName, pred.TagName, StringComparison.OrdinalIgnoreCase));
            var chinese = pred.ChineseName ?? existing?.ChineseName ?? _chineseLib.Lookup(pred.TagName) ?? pred.TagName;
            items.Add(new TagTranslationDto
            {
                EnglishTag = pred.TagName,
                ChineseTranslation = chinese,
                ImageCount = 1,
                IsConfirmed = true,
                IsExistingMapping = existing != null
            });
        }

        return items;
    }

    public async Task SaveMappingsOnlyAsync(List<TagTranslationDto> items)
    {
        foreach (var item in items)
        {
            var chinese = item.UserEditedText ?? item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chinese)) continue;
            await _mappingRepo.UpsertAsync(item.EnglishTag, chinese);
        }
    }

    public async Task SaveMappingsAndTagsAsync(string filePath, List<TagTranslationDto> items)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        foreach (var item in items)
        {
            var chinese = item.UserEditedText ?? item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chinese)) continue;

            await _mappingRepo.UpsertAsync(item.EnglishTag, chinese);

            if (item.IsConfirmed)
            {
                var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(chinese);
                try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, item.EnglishTag, chineseTagId); }
                catch (Exception ex)
                {
                    AppLogger.Warn($"SaveMappingsAndTags: ReplaceAutoTagAsync failed for image={meta.Id} tag={item.EnglishTag}: {ex.Message}");
                }
                await _metaRepo.AddAutoTagsAsync(meta.Id, new List<string> { chinese });
            }
        }
    }

    public async Task WriteConfirmedTagsAsync(string filePath, List<TagTranslationDto> items)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        foreach (var item in items.Where(i => i.IsConfirmed))
        {
            var chineseName = item.UserEditedText ?? item.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(chineseName)) continue;

            await _mappingRepo.UpsertAsync(item.EnglishTag, chineseName);

            var chineseTagId = await _tagRepo.GetOrCreateTagIdAsync(chineseName);
            try { await _metaRepo.ReplaceAutoTagAsync(meta.Id, item.EnglishTag, chineseTagId); }
            catch (Exception ex)
            {
                AppLogger.Warn($"WriteConfirmedTags: ReplaceAutoTagAsync failed for image={meta.Id} tag={item.EnglishTag}: {ex.Message}");
            }
            await _metaRepo.AddAutoTagsAsync(meta.Id, new List<string> { chineseName });
        }
    }

    public async Task DeleteTagAsync(string englishTag)
    {
        await _pipeline.DeleteAutoTagAsync(_currentFolderId, _currentFolderPath, englishTag);
    }

    public async Task<int> DeleteAllAutoTagsAsync(string folderPath)
    {
        var count = await _metaRepo.DeleteAllAutoTagsByFolderAsync(folderPath);
        AppLogger.Tag("DeleteAutoTags", $"folder={folderPath} deleted={count}");
        return count;
    }

    public async Task CancelAsync()
    {
        _cts?.Cancel();
        await Task.CompletedTask;
    }

    private void ResetCts()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        _cts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
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

    public int GetArtistStoreCount() => _artistStore.Count;
}
