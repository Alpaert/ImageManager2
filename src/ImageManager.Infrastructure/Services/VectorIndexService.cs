using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

public sealed class VectorIndexService : IVectorIndexService
{
    private readonly IImageEmbeddingRepository _repository;
    private readonly ChineseClipService _chineseClip;
    private readonly ISimilarImageService _similarImageService;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _runCancellation;
    private volatile bool _paused;
    private int _running;

    public VectorIndexService(
        IImageEmbeddingRepository repository,
        ChineseClipService chineseClip,
        ISimilarImageService similarImageService)
    {
        _repository = repository;
        _chineseClip = chineseClip;
        _similarImageService = similarImageService;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<IReadOnlyList<VectorIndexStatus>> GetStatusesAsync(VectorIndexScope scope)
    {
        var candidates = await GetImageCandidatesAsync(scope);
        var candidateIds = candidates.Select(item => item.ImageMetaId).ToHashSet();
        var statuses = new List<VectorIndexStatus>(3);
        foreach (var kind in new[] { VectorIndexKind.Semantic, VectorIndexKind.Atmosphere, VectorIndexKind.Color })
        {
            var (modelKey, modelVersion) = GetModel(kind);
            var validIds = await _repository.GetValidSearchEmbeddingIdsAsync(modelKey, modelVersion);
            var indexed = validIds.Count(candidateIds.Contains);
            statuses.Add(new VectorIndexStatus(kind, candidates.Count, indexed, candidates.Count - indexed));
        }
        return statuses;
    }

    public async Task<VectorIndexStatus> GetStatusAsync(VectorIndexKind kind, VectorIndexScope scope)
    {
        var statuses = await GetStatusesAsync(scope);
        return statuses.First(status => status.Kind == kind);
    }

    public async Task BuildAsync(
        VectorIndexKind kind,
        bool rebuild,
        VectorIndexScope scope,
        IProgress<VectorIndexProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("已有向量索引任务正在运行");

        lock (_stateLock)
        {
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _paused = false;
        }

        var token = _runCancellation.Token;
        var (modelKey, modelVersion) = GetModel(kind);
        try
        {
            if (kind == VectorIndexKind.Semantic)
                _chineseClip.ValidateImageModelFiles();
            var candidates = await GetImageCandidatesAsync(scope);
            if (rebuild)
            {
                if (scope.IsAll)
                    await _repository.DeleteModelAsync(modelKey, modelVersion);
                else
                    await _repository.DeleteModelForImagesAsync(
                        modelKey,
                        modelVersion,
                        candidates.Select(item => item.ImageMetaId).ToArray());
            }
            var validIds = rebuild
                ? []
                : await _repository.GetValidSearchEmbeddingIdsAsync(modelKey, modelVersion);
            var pending = candidates.Where(item => !validIds.Contains(item.ImageMetaId)).ToList();
            var processed = 0;
            var generated = 0;
            var skipped = candidates.Count - pending.Count;
            var failed = 0;
            var batch = new List<SearchEmbeddingWrite>(kind == VectorIndexKind.Semantic ? 8 : 64);

            Report(progress, kind, candidates.Count, processed, generated, skipped, failed, null, null);
            foreach (var candidate in pending)
            {
                token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(token);
                try
                {
                    var vector = await GenerateAsync(kind, candidate.FilePath, token);
                    if (vector.Length == 0)
                    {
                        failed++;
                    }
                    else
                    {
                        batch.Add(new SearchEmbeddingWrite(
                            candidate.ImageMetaId,
                            candidate.FileSize,
                            candidate.LastWriteTicks,
                            vector));
                        generated++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLogger.Warn($"Vector index failed kind={kind} file={candidate.FilePath}: {ex.Message}");
                    Report(progress, kind, candidates.Count, processed, generated, skipped, failed, candidate.FilePath, ex.Message);
                }

                processed++;
                if (batch.Count >= (kind == VectorIndexKind.Semantic ? 8 : 64))
                    await FlushAsync(batch, modelKey, modelVersion);
                Report(progress, kind, candidates.Count, processed, generated, skipped, failed, candidate.FilePath, null);
            }

            await FlushAsync(batch, modelKey, modelVersion);
            Report(progress, kind, candidates.Count, processed, generated, skipped, failed, null, null);
        }
        finally
        {
            if (kind == VectorIndexKind.Semantic)
                _chineseClip.ReleaseImageSession();
            lock (_stateLock)
            {
                _runCancellation?.Dispose();
                _runCancellation = null;
                _paused = false;
            }
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Pause()
    {
        if (IsRunning)
            _paused = true;
    }

    public void Resume() => _paused = false;

    public void Cancel()
    {
        lock (_stateLock)
            _runCancellation?.Cancel();
    }

    private async Task<List<SearchIndexCandidate>> GetImageCandidatesAsync(VectorIndexScope scope)
    {
        var candidates = await _repository.GetSearchIndexCandidatesAsync(scope);
        var images = candidates
            .Where(item => FileTypeConstants.IsImageFile(item.FilePath) && File.Exists(item.FilePath))
            .ToList();
        if (scope.IsAll || scope.IncludeSubfolders)
            return images;

        var root = Path.GetFullPath(scope.FolderPath!)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return images
            .Where(item => string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(item.FilePath))?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                root,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<float[]> GenerateAsync(VectorIndexKind kind, string path, CancellationToken ct)
    {
        return kind switch
        {
            VectorIndexKind.Semantic => await _chineseClip.GetImageEmbeddingAsync(path, ct),
            VectorIndexKind.Atmosphere => await Task.Run(() => ImageSignatureService.ComputeAtmosphere(path), ct),
            VectorIndexKind.Color => await Task.Run(() => ImageSignatureService.ComputeColor(path), ct),
            _ => []
        };
    }

    private async Task FlushAsync(List<SearchEmbeddingWrite> batch, string modelKey, string modelVersion)
    {
        if (batch.Count == 0)
            return;
        await _repository.UpsertSearchBatchAsync(batch, modelKey, modelVersion);
        batch.Clear();
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_paused)
            await Task.Delay(100, ct);
    }

    private void Report(
        IProgress<VectorIndexProgress>? progress,
        VectorIndexKind kind,
        int total,
        int processed,
        int generated,
        int skipped,
        int failed,
        string? currentFile,
        string? error)
    {
        progress?.Report(new VectorIndexProgress(
            kind, total, processed, generated, skipped, failed,
            currentFile, _paused, error));
    }

    public static (string ModelKey, string ModelVersion) GetModel(VectorIndexKind kind) => kind switch
    {
        VectorIndexKind.Semantic => (ChineseClipService.ModelKey, ChineseClipService.ModelVersion),
        VectorIndexKind.Atmosphere => (ImageSignatureService.AtmosphereModelKey, ImageSignatureService.ModelVersion),
        VectorIndexKind.Color => (ImageSignatureService.ColorModelKey, ImageSignatureService.ModelVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
