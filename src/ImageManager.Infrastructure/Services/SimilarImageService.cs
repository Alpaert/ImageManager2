using System.Collections.Concurrent;
using System.Diagnostics;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Hashing;

namespace ImageManager.Infrastructure.Services;

public sealed class SimilarImageService : ISimilarImageService, IDisposable
{
    private const int VectorQueryBatchSize = 500;

    private readonly ChineseClipService _chineseClip;
    private readonly SimilaritySnapshotManager _snapshotManager;

    internal int PerceptualSnapshotBuildCount => _snapshotManager.PerceptualSnapshotBuildCount;
    internal Action? PerceptualSnapshotBuildingHook
    {
        get => _snapshotManager.PerceptualSnapshotBuildingHook;
        set => _snapshotManager.PerceptualSnapshotBuildingHook = value;
    }
    internal Action? PerceptualSnapshotWaitingGateHook
    {
        get => _snapshotManager.PerceptualSnapshotWaitingGateHook;
        set => _snapshotManager.PerceptualSnapshotWaitingGateHook = value;
    }

    public SimilarImageService(
        IImageMetaRepository metaRepository,
        IImageEmbeddingRepository embeddingRepository,
        ChineseClipService chineseClip,
        IHashDataVersionTracker? versionTracker = null)
        : this(new SimilaritySnapshotManager(metaRepository, embeddingRepository, versionTracker), chineseClip)
    {
    }

    internal SimilarImageService(
        SimilaritySnapshotManager snapshotManager,
        ChineseClipService chineseClip)
    {
        _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
        _chineseClip = chineseClip ?? throw new ArgumentNullException(nameof(chineseClip));
        AppLogger.MemAlways("SimilarSearch.");
        AppLogger.MemAlways("VectorSearch.");
    }

    public async Task<List<SimilaritySearchResult>> SearchByImageAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        int limit = 50,
        CancellationToken ct = default)
    {
        var candidatePaths = candidates.ToArray();
        var watch = Stopwatch.StartNew();
        AppLogger.Memory($"SimilarSearch.Start kind=image mode={mode} candidates={candidatePaths.Length} managedMB={GetManagedMemoryMb():F1}");
        List<SimilaritySearchResult>? results = null;
        try
        {
            results = mode == SimilaritySearchMode.Perceptual
                ? await SearchPerceptualAsync(baseFilePath, candidatePaths, limit, ct)
                : await SearchVectorByImageAsync(baseFilePath, candidatePaths, mode, limit, ct);
            return results;
        }
        finally
        {
            AppLogger.Memory($"SimilarSearch.End kind=image mode={mode} elapsedMs={watch.ElapsedMilliseconds} results={results?.Count ?? -1} managedMB={GetManagedMemoryMb():F1}");
        }
    }

    public async Task<List<SimilaritySearchResult>> SearchByTextAsync(
        string query,
        IEnumerable<string> candidates,
        int limit = 50,
        CancellationToken ct = default)
    {
        var candidatePaths = candidates.ToArray();
        var watch = Stopwatch.StartNew();
        AppLogger.Memory($"SimilarSearch.Start kind=text mode={SimilaritySearchMode.Semantic} candidates={candidatePaths.Length} managedMB={GetManagedMemoryMb():F1}");
        List<SimilaritySearchResult>? results = null;
        try
        {
            var embeddingWatch = Stopwatch.StartNew();
            float[] queryVector;
            try
            {
                queryVector = await _chineseClip.GetTextEmbeddingAsync(query, ct);
            }
            finally
            {
                AppLogger.Memory($"SimilarSearch.TextEmbedding elapsedMs={embeddingWatch.ElapsedMilliseconds}");
            }

            var vectorWatch = Stopwatch.StartNew();
            try
            {
                results = await SearchVectorsAsync(
                    queryVector,
                    candidatePaths,
                    SimilaritySearchMode.Semantic,
                    null,
                    limit,
                    ct);
            }
            finally
            {
                AppLogger.Memory($"SimilarSearch.VectorStage elapsedMs={vectorWatch.ElapsedMilliseconds} results={results?.Count ?? -1}");
            }
            return results;
        }
        finally
        {
            AppLogger.Memory($"SimilarSearch.End kind=text mode={SimilaritySearchMode.Semantic} elapsedMs={watch.ElapsedMilliseconds} results={results?.Count ?? -1} managedMB={GetManagedMemoryMb():F1}");
        }
    }

    private async Task<List<SimilaritySearchResult>> SearchVectorByImageAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        int limit,
        CancellationToken ct)
    {
        var queryVector = mode switch
        {
            SimilaritySearchMode.Semantic => await _chineseClip.GetImageEmbeddingAsync(baseFilePath, ct),
            SimilaritySearchMode.Atmosphere => await Task.Run(() => ImageSignatureService.ComputeAtmosphere(baseFilePath), ct),
            SimilaritySearchMode.Color => await Task.Run(() => ImageSignatureService.ComputeColor(baseFilePath), ct),
            _ => []
        };
        return await SearchVectorsAsync(queryVector, candidates, mode, baseFilePath, limit, ct);
    }

    private async Task<List<SimilaritySearchResult>> SearchVectorsAsync(
        float[] queryVector,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        string? excludedPath,
        int limit,
        CancellationToken ct)
    {
        if (queryVector.Length == 0 || limit <= 0)
            return [];

        var candidateSet = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateSet.Count == 0)
            return [];

        var candidatePaths = candidateSet.ToArray();
        var (modelKey, modelVersion) = GetModel(mode);
        _snapshotManager.OnVectorSearchStarted();
        VectorSnapshotLoadResult snapshotLoad;
        try
        {
            snapshotLoad = await Task.Run(
                () => _snapshotManager.GetVectorSnapshotAsync(
                    new VectorSnapshotKey(modelKey, modelVersion),
                    candidatePaths,
                    queryVector.Length,
                    ct),
                ct);
        }
        catch
        {
            _snapshotManager.OnVectorSearchEnded();
            throw;
        }

        var queue = new PriorityQueue<SimilaritySearchResult, float>();
        AppLogger.Memory($"VectorSearch.Start mode={mode} candidates={candidatePaths.Length} batchSize={VectorQueryBatchSize} cacheHit={snapshotLoad.CacheHit} cacheBuild={snapshotLoad.CacheBuilt}");
        try
        {
            await Task.Run(() =>
            {
                foreach (var item in snapshotLoad.Snapshot.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(item.FilePath, excludedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var score = mode switch
                    {
                        SimilaritySearchMode.Semantic => SimilarImageScorer.DotProduct(queryVector, item.Embedding),
                        SimilaritySearchMode.Atmosphere => ImageSignatureService.AtmosphereScore(queryVector, item.Embedding),
                        SimilaritySearchMode.Color => ImageSignatureService.ColorScore(queryVector, item.Embedding),
                        _ => float.NegativeInfinity
                    };
                    if (!float.IsFinite(score))
                        continue;

                    var result = new SimilaritySearchResult(item.FilePath, score);
                    if (queue.Count < limit)
                        queue.Enqueue(result, score);
                    else if (queue.TryPeek(out _, out var minimum) && score > minimum)
                    {
                        queue.Dequeue();
                        queue.Enqueue(result, score);
                    }
                }
            }, ct);

            var results = new List<SimilaritySearchResult>(queue.Count);
            while (queue.TryDequeue(out var result, out _))
                results.Add(result);
            results.Sort((left, right) => right.Score.CompareTo(left.Score));
            return results;
        }
        finally
        {
            AppLogger.Memory($"VectorSearch.End mode={mode} candidates={candidatePaths.Length} batches={snapshotLoad.BatchCount} queued={queue.Count} cacheHit={snapshotLoad.CacheHit} cacheBuild={snapshotLoad.CacheBuilt}");
            _snapshotManager.OnVectorSearchEnded();
        }
    }

    public void InvalidateVectorSnapshots()
    {
        _snapshotManager.InvalidateVectorSnapshots();
    }

    public void InvalidatePerceptualSnapshot()
    {
        _snapshotManager.InvalidatePerceptualSnapshot();
    }

    private async Task<List<SimilaritySearchResult>> SearchPerceptualAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        int limit,
        CancellationToken ct)
    {
        var files = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return [];

        _snapshotManager.OnPerceptualSearchStarted();
        try
        {
            AppLogger.Info($"PerceptualSearch candidates={files.Length} base={Path.GetFileName(baseFilePath)}");
            ct.ThrowIfCancellationRequested();
            var snapshot = await _snapshotManager.GetPerceptualHashSnapshotAsync(files, ct);

            var baseHashValue = await Task.Run(() => HashService.ComputeCombinedPerceptualHashFromFile(baseFilePath), ct);
            if (!SimilarImageScorer.TryParsePerceptualHash(baseHashValue, out var baseHash))
            {
                AppLogger.Warn($"PerceptualSearch base hash failed: {baseFilePath}");
                return [];
            }
            AppLogger.Info($"PerceptualSearch usableHashes={snapshot.Hashes.Count} baseHashLength={baseHashValue.Length} cached=true");
            var results = new ConcurrentBag<SimilaritySearchResult>();
            await Task.Run(() => Parallel.ForEach(files, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, path =>
            {
                if (string.Equals(path, baseFilePath, StringComparison.OrdinalIgnoreCase) ||
                    !snapshot.Hashes.TryGetValue(path, out var candidateHash))
                    return;
                var (score, isStrict) = SimilarImageScorer.ScorePerceptualMatch(baseHash, candidateHash);
                if (!isStrict && score < SimilarImageScorer.PerceptualFallbackThreshold)
                    return;
                results.Add(new SimilaritySearchResult(
                    path,
                    score,
                    isStrict ? SimilarityMatchKind.PerceptualStrict : SimilarityMatchKind.PerceptualFallback));
            }), ct);
            return results
                .OrderBy(item => item.MatchKind == SimilarityMatchKind.PerceptualStrict ? 0 : 1)
                .ThenByDescending(item => item.Score)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _snapshotManager.OnPerceptualSearchEnded();
        }
    }

    public void Dispose()
    {
        _snapshotManager.Dispose();
    }

    private static (string ModelKey, string ModelVersion) GetModel(SimilaritySearchMode mode) => mode switch
    {
        SimilaritySearchMode.Semantic => (ChineseClipService.ModelKey, ChineseClipService.ModelVersion),
        SimilaritySearchMode.Atmosphere => (ImageSignatureService.AtmosphereModelKey, ImageSignatureService.ModelVersion),
        SimilaritySearchMode.Color => (ImageSignatureService.ColorModelKey, ImageSignatureService.ModelVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static double GetManagedMemoryMb() => GC.GetTotalMemory(false) / 1048576.0;
}
