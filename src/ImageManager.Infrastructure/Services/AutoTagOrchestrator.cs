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
    private readonly CharacterEmbeddingStore _characterStore;
    private readonly PixaiTagService _pixaiService;
    private readonly IMessenger _messenger;
    private TagMode _currentMode = TagMode.Ensemble;
    private CancellationTokenSource? _cts;

    /// <summary>Paths actually processed in the last pipeline run (excluding skipped).</summary>
    public List<string> LastProcessedPaths { get; private set; } = new();

    public AutoTagOrchestrator(
        AutoTagPipelineService pipeline,
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
        CharacterEmbeddingStore characterStore,
        PixaiTagService pixaiService,
        IMessenger messenger)
    {
        _pipeline = pipeline;
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
        _characterStore = characterStore;
        _pixaiService = pixaiService;
        _messenger = messenger;

        // Wire pipeline progress → messenger
        _pipeline.ProgressChanged += p =>
            _messenger.Send(new AutoTagProgressMessage(p.Phase, p.Processed, p.Total, p.StatusText));
        _tagService.ProgressChanged += p =>
            _messenger.Send(new AutoTagProgressMessage("Model", p.Processed, p.Total, p.StatusText));
    }

    public void Configure(TagMode mode, double confidenceThreshold, int maxTagsPerImage,
        double pixaiThreshold, double artistMatchThreshold, bool enableCharacterRecognition,
        double characterMatchThreshold, int characterMaxMatchesPerImage, string? apiKey)
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
                TagThresholds = new Dictionary<string, double>
                {
                    ["pixai"] = pixaiThreshold
                },
                ArtistMatchThreshold = artistMatchThreshold,
                EnableCharacterRecognition = enableCharacterRecognition,
                CharacterMatchThreshold = characterMatchThreshold,
                CharacterMaxMatchesPerImage = Math.Clamp(characterMaxMatchesPerImage, 1, 5)
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
        long fileCount = folderId > 0 ? await _metaRepo.CountByFolderIdAsync(folderId) : 0;
        return await _pipeline.DetermineActionAsync(folderId, fileCount);
    }

    public async Task RunPipelineAsync(long folderId, string folderPath, List<string> filePaths, string action)
    {
        filePaths = filePaths.Where(f => FileTypeConstants.IsImageFile(f)).ToList();
        if (filePaths.Count == 0)
        {
            LastProcessedPaths = new List<string>();
            _messenger.Send(new AutoTagProgressMessage("Done", 0, 0, "没有图片文件需要处理"));
            return;
        }

        AppLogger.Memory($"Orch.Start action={action} files={filePaths.Count} folder={Path.GetFileName(folderPath)}");

        var statusMap = await _metaRepo.GetStatusMapByPathsAsync(filePaths);

        var metas = new List<(long Id, string FilePath)>();
        foreach (var path in filePaths)
        {
            if (statusMap.TryGetValue(path, out var existing))
            {
                if (existing.Status == 1) { continue; }
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
                            FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                            HashStatus = 0  // Hash not yet computed
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
            return;
        }

        LastProcessedPaths = metas.Select(m => m.FilePath).ToList();

        ResetCts();
        try
        {
            var activeTagService = _factory.Create(_currentMode);
            LastProcessedPaths = await _pipeline.RunInferenceAsync(folderId, metas, action, _cts!.Token, activeTagService);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("AutoTag pipeline cancelled by user");
        }

        AppLogger.Memory($"Orch.End processed={LastProcessedPaths.Count}");
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

    public void RegisterCharacterWithEmbedding(string characterName, float[] embedding, int imageCount)
    {
        _characterStore.Add(characterName, embedding, imageCount);
        var modelsDir = Path.Combine(_thumbCache.CacheDirectory, "models");
        var dbPath = Path.Combine(modelsDir, "character_embeddings.bin");
        _characterStore.Save(dbPath);
        AppLogger.Tag("Character", $"注册角色 character={characterName} imgs={imageCount} storeCount={_characterStore.Count}");
    }

    public int GetCharacterStoreCount() => _characterStore.Count;
}
