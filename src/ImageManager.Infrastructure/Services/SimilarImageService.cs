using System.Collections.Concurrent;
using System.Numerics;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Hashing;

namespace ImageManager.Infrastructure.Services;

public sealed class SimilarImageService : ISimilarImageService
{
    private const int VectorQueryBatchSize = 500;
    private const double HistogramThreshold = 0.35;
    private const int AThreshold = 8;
    private const int DThreshold = 8;
    private const int PThreshold = 10;
    private const float PerceptualFallbackThreshold = 0.55f;

    private readonly IImageMetaRepository _metaRepository;
    private readonly IImageEmbeddingRepository _embeddingRepository;
    private readonly ChineseClipService _chineseClip;

    public SimilarImageService(
        IImageMetaRepository metaRepository,
        IImageEmbeddingRepository embeddingRepository,
        ChineseClipService chineseClip)
    {
        _metaRepository = metaRepository;
        _embeddingRepository = embeddingRepository;
        _chineseClip = chineseClip;
    }

    public Task<List<SimilaritySearchResult>> SearchByImageAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        int limit = 50,
        CancellationToken ct = default)
    {
        return mode == SimilaritySearchMode.Perceptual
            ? SearchPerceptualAsync(baseFilePath, candidates, limit, ct)
            : SearchVectorByImageAsync(baseFilePath, candidates, mode, limit, ct);
    }

    public async Task<List<SimilaritySearchResult>> SearchByTextAsync(
        string query,
        IEnumerable<string> candidates,
        int limit = 50,
        CancellationToken ct = default)
    {
        var queryVector = await _chineseClip.GetTextEmbeddingAsync(query, ct);
        return await SearchVectorsAsync(
            queryVector,
            candidates,
            SimilaritySearchMode.Semantic,
            null,
            limit,
            ct);
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
        var queue = new PriorityQueue<SimilaritySearchResult, float>();
        var batchCount = 0;
        AppLogger.Memory($"VectorSearch.Start mode={mode} candidates={candidatePaths.Length} batchSize={VectorQueryBatchSize}");
        try
        {
            foreach (var batch in candidatePaths.Chunk(VectorQueryBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var embeddings = await _embeddingRepository.GetValidSearchEmbeddingsByPathsAsync(
                    modelKey, modelVersion, batch, ct);
                batchCount++;
                await Task.Run(() =>
                {
                    foreach (var item in embeddings)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.Equals(item.FilePath, excludedPath, StringComparison.OrdinalIgnoreCase) ||
                            item.Embedding.Length != queryVector.Length)
                            continue;

                        var score = mode switch
                        {
                            SimilaritySearchMode.Semantic => DotProduct(queryVector, item.Embedding),
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
            }

            var results = new List<SimilaritySearchResult>(queue.Count);
            while (queue.TryDequeue(out var result, out _))
                results.Add(result);
            results.Sort((left, right) => right.Score.CompareTo(left.Score));
            return results;
        }
        finally
        {
            AppLogger.Memory($"VectorSearch.End mode={mode} candidates={candidatePaths.Length} batches={batchCount} queued={queue.Count}");
        }
    }

    private async Task<List<SimilaritySearchResult>> SearchPerceptualAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        int limit,
        CancellationToken ct)
    {
        var files = candidates.ToList();
        if (files.Count == 0)
            return [];
        var hashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashes = await _metaRepository.GetPerceptualHashesByPathsAsync(files);
        foreach (var pair in hashes)
        {
            if (!string.IsNullOrEmpty(pair.Value) && pair.Value.Split('|').Length >= 4)
                hashCache[pair.Key] = pair.Value;
        }

        var baseHash = await Task.Run(() => HashService.ComputeCombinedPerceptualHashFromFile(baseFilePath), ct);
        if (string.IsNullOrEmpty(baseHash))
            return [];
        var results = new ConcurrentBag<SimilaritySearchResult>();
        await Task.Run(() => Parallel.ForEach(files, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, path =>
        {
            if (string.Equals(path, baseFilePath, StringComparison.OrdinalIgnoreCase) ||
                !hashCache.TryGetValue(path, out var candidateHash))
                return;
            var (score, isStrict) = ScorePerceptualMatch(baseHash, candidateHash);
            if (!isStrict && score < PerceptualFallbackThreshold)
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

    private static (float Score, bool IsStrict) ScorePerceptualMatch(
        string baseHash,
        string candidateHash)
    {
        var baseParts = baseHash.Split('|');
        var candidateParts = candidateHash.Split('|');
        if (baseParts.Length < 4 || candidateParts.Length < 4)
            return (0, false);

        var averageDistance = HammingDistance(baseParts[0], candidateParts[0]);
        var differenceDistance = HammingDistance(baseParts[1], candidateParts[1]);
        var perceptualDistance = HammingDistance(baseParts[2], candidateParts[2]);
        var histogramSimilarity = (float)HashService.CompareHistograms(baseParts[3], candidateParts[3]);
        var votes = 0;
        if (averageDistance <= AThreshold) votes++;
        if (differenceDistance <= DThreshold) votes++;
        if (perceptualDistance <= PThreshold) votes++;
        var isStrict = histogramSimilarity >= HistogramThreshold && votes >= 2;

        var averageSimilarity = 1f - Math.Clamp(averageDistance / 64f, 0f, 1f);
        var differenceSimilarity = 1f - Math.Clamp(differenceDistance / 64f, 0f, 1f);
        var perceptualSimilarity = 1f - Math.Clamp(perceptualDistance / 64f, 0f, 1f);
        var score = 0.15f * averageSimilarity +
                    0.30f * differenceSimilarity +
                    0.40f * perceptualSimilarity +
                    0.15f * Math.Clamp(histogramSimilarity, 0f, 1f);
        return (score, isStrict);
    }

    private static int HammingDistance(string left, string right)
    {
        if (left.Length != right.Length)
            return int.MaxValue;
        var distance = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                distance++;
        }
        return distance;
    }

    private static (string ModelKey, string ModelVersion) GetModel(SimilaritySearchMode mode) => mode switch
    {
        SimilaritySearchMode.Semantic => (ChineseClipService.ModelKey, ChineseClipService.ModelVersion),
        SimilaritySearchMode.Atmosphere => (ImageSignatureService.AtmosphereModelKey, ImageSignatureService.ModelVersion),
        SimilaritySearchMode.Color => (ImageSignatureService.ColorModelKey, ImageSignatureService.ModelVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static float DotProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var vectorSize = Vector<float>.Count;
        var index = 0;
        var sum = Vector<float>.Zero;
        for (; index <= left.Length - vectorSize; index += vectorSize)
            sum += new Vector<float>(left.Slice(index, vectorSize)) * new Vector<float>(right.Slice(index, vectorSize));
        var result = Vector.Dot(sum, Vector<float>.One);
        for (; index < left.Length; index++)
            result += left[index] * right[index];
        return result;
    }

}
