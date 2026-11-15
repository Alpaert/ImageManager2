using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Services;

public sealed class VectorIndexService : IVectorIndexService
{
    private readonly IImageEmbeddingRepository _repository;
    private readonly IImageMetaRepository _metaRepository;
    private readonly ChineseClipService _chineseClip;
    private readonly ISimilarImageService _similarImageService;
    private readonly SscdEmbeddingService? _sscd;
    private readonly SscdIndexWriter? _sscdWriter;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _runCancellation;
    private volatile bool _paused;
    private int _running;

    public VectorIndexService(
        IImageEmbeddingRepository repository,
        IImageMetaRepository metaRepository,
        ChineseClipService chineseClip,
        ISimilarImageService similarImageService,
        SscdEmbeddingService? sscd = null,
        SscdIndexWriter? sscdWriter = null)
    {
        _repository = repository;
        _metaRepository = metaRepository;
        _chineseClip = chineseClip;
        _similarImageService = similarImageService;
        _sscd = sscd;
        _sscdWriter = sscdWriter;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<IReadOnlyList<VectorIndexStatus>> GetStatusesAsync(VectorIndexScope scope)
    {
        var candidates = await GetImageCandidatesAsync(scope);
        var candidateIds = candidates.Select(item => item.ImageMetaId).ToHashSet();
        var statuses = new List<VectorIndexStatus>(4);
        foreach (var kind in new[] { VectorIndexKind.Semantic, VectorIndexKind.Atmosphere, VectorIndexKind.Color, VectorIndexKind.Sscd })
        {
            var (modelKey, modelVersion) = GetModel(kind);
            var validIds = kind == VectorIndexKind.Sscd && _sscdWriter is not null
                ? await _sscdWriter.GetValidIdsAsync()
                : await _repository.GetValidSearchEmbeddingIdsAsync(modelKey, modelVersion);
            var indexed = kind == VectorIndexKind.Sscd
                ? candidates.Count(item => validIds.Contains(item.ImageMetaId) &&
                    RegionSearchService.MatchesFileState(item.FilePath, item.FileSize, item.LastWriteTicks))
                : validIds.Count(candidateIds.Contains);
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
            if (kind == VectorIndexKind.Sscd)
            {
                await BuildSscdAsync(rebuild, scope, progress, token);
                return;
            }

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
            try
            {
                try
                {
                    if (kind != VectorIndexKind.Sscd)
                        _similarImageService.InvalidateVectorSnapshots();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to invalidate vector snapshots after build: {ex.Message}");
                }

                if (kind == VectorIndexKind.Semantic)
                {
                    try
                    {
                        _chineseClip.ReleaseImageSession();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Failed to release Chinese CLIP image session: {ex.Message}");
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                    _paused = false;
                }
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }

    private async Task BuildSscdAsync(
        bool rebuild, VectorIndexScope scope, IProgress<VectorIndexProgress>? progress, CancellationToken ct)
    {
        if (_sscd is null || _sscdWriter is null || !_sscd.IsAvailable)
            throw new InvalidOperationException("SSCD 模型不可用，请先安装模型。");

        await AutoTagRuntimeState.WaitForIdleAsync(ct);
        await _sscd.ValidateModelAsync(ct);

        const VectorIndexKind kind = VectorIndexKind.Sscd;
        const int batchSize = 8;
        var candidates = await GetImageCandidatesAsync(scope);
        var validIds = rebuild ? [] : await _sscdWriter.GetValidIdsAsync(ct);
        var batch = new List<SscdIndexWrite>(batchSize);
        var processed = 0;
        var written = 0;
        var skipped = 0;
        var failed = 0;

        Report(progress, kind, candidates.Count, processed, written, skipped, failed, null, null);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(ct);
            await AutoTagRuntimeState.WaitForIdleAsync(ct);
            string? error = null;
            try
            {
                if (!RegionSearchService.MatchesFileState(candidate.FilePath, candidate.FileSize, candidate.LastWriteTicks))
                    throw new IOException("源图片状态已变化，请先更新图库元数据。");

                if (validIds.Contains(candidate.ImageMetaId))
                {
                    skipped++;
                }
                else
                {
                    var state = await _sscdWriter.GetSourceAsync(candidate.ImageMetaId, ct);
                    if (state is not { } current || current.FileSize != candidate.FileSize ||
                        current.LastWriteTicks != candidate.LastWriteTicks ||
                        !string.Equals(current.FilePath, candidate.FilePath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("图库中的源图片状态已变化。");

                    using var source = File.Open(candidate.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length != candidate.FileSize ||
                        !RegionSearchService.MatchesFileState(candidate.FilePath, candidate.FileSize, candidate.LastWriteTicks))
                        throw new IOException("源图片在编码前发生变化。");
                    using var bitmap = InferenceImageLoader.DecodeForInference(candidate.FilePath)
                        ?? throw new InvalidDataException("无法解码图片。");
                    var vector = await _sscd.EncodeAsync(bitmap, ct);
                    if (!RegionSearchService.IsUsableVector(vector, SscdEmbeddingService.Dimension))
                        throw new InvalidDataException("无法提取有效的 SSCD 向量。");
                    if (!RegionSearchService.MatchesFileState(candidate.FilePath, candidate.FileSize, candidate.LastWriteTicks))
                        throw new IOException("源图片在编码期间发生变化。");

                    batch.Add(new SscdIndexWrite(
                        candidate.ImageMetaId, candidate.FilePath, candidate.FileSize, candidate.LastWriteTicks,
                        vector, current.FileHash));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                error = ex.Message;
                AppLogger.Warn($"SSCD index failed file={candidate.FilePath}: {error}");
            }

            processed++;
            if (batch.Count >= batchSize)
                await FlushSscdAsync();
            Report(progress, kind, candidates.Count, processed, written, skipped, failed, candidate.FilePath, error);
        }
        await FlushSscdAsync();
        Report(progress, kind, candidates.Count, processed, written, skipped, failed, null, null);

        async Task FlushSscdAsync()
        {
            if (batch.Count == 0)
                return;
            var committed = await _sscdWriter.WriteAsync(batch, rebuild, ct);
            written += committed;
            failed += batch.Count - committed;
            batch.Clear();
        }
    }

    public async Task RepairHashesAsync(
        VectorIndexScope scope,
        IProgress<HashRepairProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("已有向量索引或图片指纹任务正在运行");

        lock (_stateLock)
        {
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _paused = false;
        }
        var token = _runCancellation.Token;
        var batch = new List<ImageMeta>(64);
        try
        {
            var files = await EnumerateImageFilesAsync(scope, token);
            if (files.Count == 0)
            {
                progress?.Report(new HashRepairProgress(0, 0, 0, 0, 0, null, null));
                return;
            }

            var metadata = await _metaRepository.GetHashMetadataByPathsAsync(files);
            var metadataMap = metadata.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>();
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                if (!metadataMap.TryGetValue(path, out var meta) || !IsUsableHash(meta, path))
                    pending.Add(path);
            }

            var processed = 0;
            var generated = 0;
            var skipped = files.Count - pending.Count;
            var failed = 0;
            progress?.Report(new HashRepairProgress(files.Count, 0, 0, skipped, 0, null, null));
            foreach (var path in pending)
            {
                token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(token);
                try
                {
                    var info = new FileInfo(path);
                    using var stream = File.OpenRead(path);
                    var fileHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream)).ToLowerInvariant();
                    var perceptualHash = HashService.ComputeCombinedPerceptualHashFromFile(path);
                    var dimensions = ThumbnailGenerator.GetDimensions(path);
                    batch.Add(new ImageMeta
                    {
                        FilePath = path,
                        FileHash = fileHash,
                        PerceptualHash = perceptualHash,
                        Width = dimensions.Width,
                        Height = dimensions.Height,
                        FileSize = info.Length,
                        LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                        FolderId = metadataMap.TryGetValue(path, out var existing) ? existing.FolderId : null,
                        HashStatus = string.IsNullOrEmpty(perceptualHash) ? -1 : 1
                    });
                    if (string.IsNullOrEmpty(perceptualHash)) failed++; else generated++;
                    if (batch.Count >= 64)
                    {
                        await _metaRepository.BulkUpsertAsync(batch);
                        batch.Clear();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    progress?.Report(new HashRepairProgress(files.Count, processed, generated, skipped, failed, path, ex.Message));
                }
                processed++;
                progress?.Report(new HashRepairProgress(files.Count, processed, generated, skipped, failed, path, null));
            }
            if (batch.Count > 0)
                await _metaRepository.BulkUpsertAsync(batch);
            batch.Clear();
            progress?.Report(new HashRepairProgress(files.Count, processed, generated, skipped, failed, null, null));
        }
        catch (OperationCanceledException)
        {
            if (batch.Count > 0)
                await _metaRepository.BulkUpsertAsync(batch);
            throw;
        }
        finally
        {
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

    private async Task<List<string>> EnumerateImageFilesAsync(VectorIndexScope scope, CancellationToken ct)
    {
        if (scope.IsAll)
        {
            var indexed = await _repository.GetSearchIndexCandidatesAsync(scope);
            return indexed.Select(item => item.FilePath)
                .Where(File.Exists)
                .Where(FileTypeConstants.IsImageFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var roots = new[] { Path.GetFullPath(scope.FolderPath!) };
        var files = new List<string>();
        foreach (var root in roots)
        {
            var option = scope.IsAll || scope.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = option == SearchOption.AllDirectories,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };
                foreach (var file in Directory.EnumerateFiles(root, "*.*", enumerationOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileTypeConstants.IsImageFile(file)) files.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsUsableHash(ImageMeta meta, string path)
    {
        if (meta.HashStatus != 1 || string.IsNullOrWhiteSpace(meta.PerceptualHash) || meta.PerceptualHash.Split('|').Length < 4)
            return false;
        try
        {
            var info = new FileInfo(path);
            return info.Length == meta.FileSize && info.LastWriteTimeUtc.Ticks == meta.LastWriteTicks;
        }
        catch { return false; }
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
        VectorIndexKind.Sscd => (SscdEmbeddingService.ModelKey, SscdEmbeddingService.ModelVersion),
        VectorIndexKind.Semantic => (ChineseClipService.ModelKey, ChineseClipService.ModelVersion),
        VectorIndexKind.Atmosphere => (ImageSignatureService.AtmosphereModelKey, ImageSignatureService.ModelVersion),
        VectorIndexKind.Color => (ImageSignatureService.ColorModelKey, ImageSignatureService.ModelVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
