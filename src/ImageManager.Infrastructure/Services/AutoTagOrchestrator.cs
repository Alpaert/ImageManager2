using System.Diagnostics;
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
    private readonly IImageEmbeddingRepository _embeddingRepo;
    private readonly IMessenger _messenger;
    private const string EmbeddingModelKey = "pixai";
    private const string EmbeddingModelVersion = "v0.9";
    private TagMode _currentMode = TagMode.Ensemble;
    private CancellationTokenSource? _cts;
    private readonly object _runLock = new();
    private bool _disposed;
    private bool _pipelineHadErrors;
    public bool LastRunCancelled { get; private set; }
    public int LastPreparationFailed { get; private set; }
    public int LastSkippedCount { get; private set; }

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
        IImageEmbeddingRepository embeddingRepo,
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
        _embeddingRepo = embeddingRepo;
        _messenger = messenger;

        // Wire pipeline progress → messenger
        _pipeline.ProgressChanged += p =>
        {
            if (p.Phase == "Error") _pipelineHadErrors = true;
            _messenger.Send(new AutoTagProgressMessage(p.Phase, p.Processed, p.Total, p.StatusText));
        };
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

    public Task RunPipelineAsync(long folderId, string folderPath, List<string> filePaths, string action,
        CancellationToken cancellationToken = default) =>
        RunAsync(folderId, _ => Task.FromResult(filePaths), action, false, cancellationToken);

    public Task RunSelectedImagesAsync(List<string> filePaths, CancellationToken cancellationToken = default) =>
        RunAsync(0, _ => Task.FromResult(filePaths), "Start", true, cancellationToken);

    public Task RunFolderAsync(long folderId, string folderPath, bool recursive,
        CancellationToken cancellationToken = default) =>
        RunAsync(folderId, ct => Task.Run(() => ScanFolder(folderPath, recursive, ct), ct),
            "Start", false, cancellationToken);

    private List<string> ScanFolder(string root, bool recursive, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var files = new List<string>();
        var folders = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        folders.Enqueue(Path.GetFullPath(root));
        Publish("Scan", 0, 0, "正在扫描图片文件...");
        while (folders.TryDequeue(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileTypeConstants.IsImageFile(file)) files.Add(file);
                    if (files.Count > 0 && files.Count % 500 == 0)
                        Publish("Scan", files.Count, 0, $"正在扫描图片文件：已找到 {files.Count} 张");
                }
                if (recursive)
                    foreach (var child in Directory.EnumerateDirectories(folder))
                    {
                        ct.ThrowIfCancellationRequested();
                        // Junctions can lead back into an already scanned directory.
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            folders.Enqueue(child);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (string.Equals(folder, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw;
                AppLogger.Warn($"AutoTag.Scan skipped folder={folder}: {ex.Message}");
            }
        }
        AppLogger.Info($"AutoTag.Scan files={files.Count} folders={visited.Count} elapsedMs={watch.ElapsedMilliseconds}");
        return files;
    }

    private void Publish(string phase, int processed, int total, string text) =>
        _messenger.Send(new AutoTagProgressMessage(phase, processed, total, text));

    private Task RunAsync(long folderId, Func<CancellationToken, Task<List<string>>> getFiles,
        string action, bool selectedImages, CancellationToken cancellationToken)
    {
        CancellationTokenSource run;
        lock (_runLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts != null) throw new InvalidOperationException("自动打标正在进行中");
            run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cts = run;
        }
        // Establish cancellation and maintenance exclusion synchronously, before queuing work.
        var autoTagRun = AutoTagRuntimeState.Enter();
        return Task.Run(async () =>
        {
            using var runScope = autoTagRun;
            var ct = run.Token;
            var watch = Stopwatch.StartNew();
            LastProcessedPaths = new();
            LastRunCancelled = false;
            LastPreparationFailed = 0;
            LastSkippedCount = 0;
            _pipelineHadErrors = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                var files = await getFiles(ct);
                var preparation = await new AutoTagPreparationService(_metaRepo).PrepareAsync(folderId, files,
                    p => Publish(p.Phase, p.Processed, p.Total, p.StatusText), ct);
                LastPreparationFailed = preparation.Failed;
                LastSkippedCount = preparation.Skipped;
                if (preparation.Images.Count == 0)
                {
                    Publish(preparation.Failed > 0 ? "Error" : "Done", 0, preparation.Total,
                        preparation.Failed > 0 ? $"已跳过 {preparation.Skipped} 张，{preparation.Failed} 张准备失败，请查看日志" :
                        preparation.Total == 0 ? "没有图片文件需要处理" : $"全部 {preparation.Skipped} 张图片已打标，跳过");
                    return;
                }

                ct.ThrowIfCancellationRequested();
                var activeTagService = _factory.Create(_currentMode);
                if (!activeTagService.IsModelLoaded)
                {
                    Publish("Model", 0, preparation.Images.Count, "正在加载打标模型...");
                    var modelWatch = Stopwatch.StartNew();
                    await activeTagService.LoadModelAsync(ModelPath, ct);
                    AppLogger.Info($"AutoTag.Model elapsedMs={modelWatch.ElapsedMilliseconds}");
                }
                ct.ThrowIfCancellationRequested();
                Publish("Inference", 0, preparation.Images.Count,
                    $"正在推理 {preparation.Images.Count} 张图片（已跳过 {preparation.Skipped} 张）...");
                if (selectedImages)
                {
                    foreach (var meta in preparation.Images)
                    {
                        ct.ThrowIfCancellationRequested();
                        Publish("Inference", LastProcessedPaths.Count, preparation.Images.Count,
                            $"正在推理：{LastProcessedPaths.Count + 1}/{preparation.Images.Count}");
                        var items = await RunSingleImageAsync(meta.FilePath, ct);
                        ct.ThrowIfCancellationRequested();
                        if (items.Count > 0) await SaveMappingsAndTagsAsync(meta.FilePath, items);
                        await _metaRepo.SetAutoTagStatusByPathAsync(meta.FilePath, 1);
                        LastProcessedPaths.Add(meta.FilePath);
                    }
                }
                else
                {
                    LastProcessedPaths = await _pipeline.RunInferenceAsync(folderId, preparation.Images, action, ct, activeTagService);
                }
                ct.ThrowIfCancellationRequested();
                Publish(_pipelineHadErrors || preparation.Failed > 0 || LastProcessedPaths.Count < preparation.Images.Count ? "Error" : "Done",
                    LastProcessedPaths.Count, preparation.Total,
                    $"打标完成 {LastProcessedPaths.Count} 张，跳过 {preparation.Skipped} 张，准备失败 {preparation.Failed} 张" +
                    (LastProcessedPaths.Count < preparation.Images.Count ? $"，未完成 {preparation.Images.Count - LastProcessedPaths.Count} 张，请查看日志" :
                        _pipelineHadErrors ? "，部分数据保存失败，请查看日志" : ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LastRunCancelled = true;
                Publish("Stopped", LastProcessedPaths.Count, 0, $"自动打标已停止，完成 {LastProcessedPaths.Count} 张");
            }
            finally
            {
                AppLogger.Info($"AutoTag.End processed={LastProcessedPaths.Count} skipped={LastSkippedCount} " +
                    $"prepareFailed={LastPreparationFailed} cancelled={LastRunCancelled} elapsedMs={watch.ElapsedMilliseconds}");
                lock (_runLock)
                {
                    if (ReferenceEquals(_cts, run)) _cts = null;
                    run.Dispose();
                }
            }
        });
    }

    public async Task<List<TagTranslationDto>> RunSingleImageAsync(string filePath, CancellationToken ct = default)
    {
        var activeTagService = _factory.Create(_currentMode);
        var result = await activeTagService.PredictWithSourcesAsync(filePath, ct);
        var predictions = result.MergedTags;
        var filtered = predictions
            .Where(p => p.Confidence >= 0.1)
            .Take(300)
            .ToList();

        if (result.Embedding is { Length: > 0 })
            await SaveImageEmbeddingAsync(filePath, result.Embedding);

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

    private async Task SaveImageEmbeddingAsync(string filePath, float[] embedding)
    {
        try
        {
            var meta = await _metaRepo.GetByPathAsync(filePath);
            if (meta == null || meta.Id <= 0)
                return;

            await _embeddingRepo.UpsertAsync(
                meta.Id,
                meta.FileHash,
                embedding,
                EmbeddingModelKey,
                EmbeddingModelVersion);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Embedding save failed path={Path.GetFileName(filePath)}: {ex.Message}");
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

    public async Task<int> DeleteAllAutoTagsAsync(string folderPath)
    {
        var count = await _metaRepo.DeleteAllAutoTagsByFolderAsync(folderPath);
        AppLogger.Tag("DeleteAutoTags", $"folder={folderPath} deleted={count}");
        return count;
    }

    public Task CancelAsync()
    {
        lock (_runLock) _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_runLock)
        {
            _disposed = true;
            _cts?.Cancel();
            // The active run owns disposal; a cancellation callback must never see a disposed source.
        }
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
