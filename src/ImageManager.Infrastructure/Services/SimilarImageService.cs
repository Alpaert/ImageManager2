using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Globalization;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Hashing;

namespace ImageManager.Infrastructure.Services;

public sealed class SimilarImageService : ISimilarImageService, IDisposable
{
    private const int VectorQueryBatchSize = 500;
    private const double HistogramThreshold = 0.35;
    private const int AThreshold = 8;
    private const int DThreshold = 8;
    private const int PThreshold = 10;
    private const float PerceptualFallbackThreshold = 0.55f;
    private static readonly TimeSpan PerceptualSnapshotIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VectorSnapshotIdleTimeout = TimeSpan.FromMinutes(30);

    private readonly IImageMetaRepository _metaRepository;
    private readonly IImageEmbeddingRepository _embeddingRepository;
    private readonly ChineseClipService _chineseClip;
    private readonly SemaphoreSlim _perceptualSnapshotBuildGate = new(1, 1);
    private readonly Timer _perceptualSnapshotIdleTimer;
    private readonly Timer _vectorSnapshotIdleTimer;
    private readonly ConcurrentDictionary<VectorSnapshotKey, VectorSnapshot> _vectorSnapshots = new();
    private readonly ConcurrentDictionary<VectorSnapshotKey, SemaphoreSlim> _vectorSnapshotBuildGates = new();
    private readonly ConcurrentDictionary<VectorSnapshotKey, long> _vectorSnapshotLastAccessUtcTicks = new();
    private PerceptualHashSnapshot? _perceptualSnapshot;
    private long _perceptualSnapshotLastAccessUtcTicks;
    private int _activePerceptualSearches;
    private int _activeVectorSearches;

    public SimilarImageService(
        IImageMetaRepository metaRepository,
        IImageEmbeddingRepository embeddingRepository,
        ChineseClipService chineseClip)
    {
        _metaRepository = metaRepository;
        _embeddingRepository = embeddingRepository;
        _chineseClip = chineseClip;
        AppLogger.MemAlways("SimilarSearch.");
        AppLogger.MemAlways("VectorSearch.");
        _perceptualSnapshotIdleTimer = new Timer(
            static state => ((SimilarImageService)state!).ReleaseIdlePerceptualSnapshot(),
            this,
            PerceptualSnapshotIdleTimeout,
            PerceptualSnapshotIdleTimeout);
        _vectorSnapshotIdleTimer = new Timer(
            static state => ((SimilarImageService)state!).ReleaseIdleVectorSnapshots(),
            this,
            VectorSnapshotIdleTimeout,
            VectorSnapshotIdleTimeout);
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
        Interlocked.Increment(ref _activeVectorSearches);
        VectorSnapshotLoadResult snapshotLoad;
        try
        {
            snapshotLoad = await Task.Run(
                () => GetVectorSnapshotAsync(
                    new VectorSnapshotKey(modelKey, modelVersion),
                    candidatePaths,
                    queryVector.Length,
                    ct),
                ct);
        }
        catch
        {
            Interlocked.Decrement(ref _activeVectorSearches);
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

            var results = new List<SimilaritySearchResult>(queue.Count);
            while (queue.TryDequeue(out var result, out _))
                results.Add(result);
            results.Sort((left, right) => right.Score.CompareTo(left.Score));
            return results;
        }
        finally
        {
            AppLogger.Memory($"VectorSearch.End mode={mode} candidates={candidatePaths.Length} batches={snapshotLoad.BatchCount} queued={queue.Count} cacheHit={snapshotLoad.CacheHit} cacheBuild={snapshotLoad.CacheBuilt}");
            Interlocked.Decrement(ref _activeVectorSearches);
        }
    }

    private async Task<VectorSnapshotLoadResult> GetVectorSnapshotAsync(
        VectorSnapshotKey key,
        IReadOnlyList<string> paths,
        int expectedDimension,
        CancellationToken ct)
    {
        ReleaseIdleVectorSnapshots();
        if (_vectorSnapshots.TryGetValue(key, out var snapshot) &&
            await Task.Run(() => snapshot.Matches(paths, expectedDimension), ct))
        {
            TouchVectorSnapshot(key);
            return new VectorSnapshotLoadResult(snapshot, true, false, 0);
        }

        var gate = _vectorSnapshotBuildGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            ReleaseIdleVectorSnapshots();
            if (_vectorSnapshots.TryGetValue(key, out snapshot) &&
                await Task.Run(() => snapshot.Matches(paths, expectedDimension), ct))
            {
                TouchVectorSnapshot(key);
                return new VectorSnapshotLoadResult(snapshot, true, false, 0);
            }

            var statesBefore = await ReadFileStatesAsync(paths, ct);
            var entries = new List<VectorSnapshotEntry>();
            var batchCount = 0;
            foreach (var batch in paths.Chunk(VectorQueryBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var embeddings = await _embeddingRepository.GetValidSearchEmbeddingsByPathsAsync(
                    key.ModelKey,
                    key.ModelVersion,
                    batch,
                    ct);
                batchCount++;
                foreach (var item in embeddings)
                {
                    if (item.Embedding.Length != expectedDimension)
                        continue;
                    if (!statesBefore.TryGetValue(item.FilePath, out var currentState) ||
                        currentState.Length != item.SourceFileSize ||
                        currentState.LastWriteTicks != item.SourceLastWriteTicks)
                        continue;
                    entries.Add(new VectorSnapshotEntry(item.FilePath, item.Embedding));
                }
            }

            var states = await ReadFileStatesAsync(paths, ct);
            if (!FileStatesEqual(statesBefore, states))
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("文件在建立向量搜索快照期间发生变化，请重试搜索");
            }

            var replacement = new VectorSnapshot(paths, states, entries);
            _vectorSnapshots[key] = replacement;
            TouchVectorSnapshot(key);
            return new VectorSnapshotLoadResult(replacement, false, true, batchCount);
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateVectorSnapshots()
    {
        foreach (var key in _vectorSnapshots.Keys)
            _vectorSnapshots.TryRemove(key, out _);
        _vectorSnapshotLastAccessUtcTicks.Clear();
    }

    private static async Task<Dictionary<string, FileState>> ReadFileStatesAsync(
        IReadOnlyList<string> paths,
        CancellationToken ct) => await Task.Run(() =>
        {
            var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                result[path] = FileState.Read(path);
            }
            return result;
        }, ct);

    private static bool FileStatesEqual(
        IReadOnlyDictionary<string, FileState> left,
        IReadOnlyDictionary<string, FileState> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var state) && state.Equals(pair.Value));

    private void ReleaseIdleVectorSnapshots()
    {
        if (Volatile.Read(ref _activeVectorSearches) != 0)
            return;

        var nowTicks = DateTime.UtcNow.Ticks;
        foreach (var pair in _vectorSnapshots)
        {
            if (!_vectorSnapshotLastAccessUtcTicks.TryGetValue(pair.Key, out var lastAccess) ||
                nowTicks - lastAccess < VectorSnapshotIdleTimeout.Ticks)
                continue;
            if (_vectorSnapshots.TryRemove(pair.Key, out _))
            {
                _vectorSnapshotLastAccessUtcTicks.TryRemove(pair.Key, out _);
                AppLogger.Memory($"VectorSearch snapshot released after idle timeout model={pair.Key.ModelKey}/{pair.Key.ModelVersion}");
            }
        }
    }

    private void TouchVectorSnapshot(VectorSnapshotKey key) =>
        _vectorSnapshotLastAccessUtcTicks[key] = DateTime.UtcNow.Ticks;

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

        Interlocked.Increment(ref _activePerceptualSearches);
        try
        {
            AppLogger.Info($"PerceptualSearch candidates={files.Length} base={Path.GetFileName(baseFilePath)}");
            ct.ThrowIfCancellationRequested();
            var snapshot = await GetPerceptualHashSnapshotAsync(files, ct);

            var baseHashValue = await Task.Run(() => HashService.ComputeCombinedPerceptualHashFromFile(baseFilePath), ct);
            if (!TryParsePerceptualHash(baseHashValue, out var baseHash))
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
        finally
        {
            Interlocked.Decrement(ref _activePerceptualSearches);
        }
    }

    private async Task<PerceptualHashSnapshot> GetPerceptualHashSnapshotAsync(
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        ReleaseIdlePerceptualSnapshot();
        var snapshot = Volatile.Read(ref _perceptualSnapshot);
        if (snapshot is not null && await Task.Run(() => snapshot.Matches(files), ct))
        {
            TouchPerceptualSnapshot();
            return snapshot;
        }

        await _perceptualSnapshotBuildGate.WaitAsync(ct);
        try
        {
            ReleaseIdlePerceptualSnapshot();
            snapshot = Volatile.Read(ref _perceptualSnapshot);
            if (snapshot is not null && await Task.Run(() => snapshot.Matches(files), ct))
            {
                TouchPerceptualSnapshot();
                return snapshot;
            }

            var hashes = await _metaRepository.GetPerceptualHashesByPathsAsync(files.ToList());
            var parsed = await Task.Run(() =>
            {
                var result = new Dictionary<string, ParsedPerceptualHash>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in hashes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryParsePerceptualHash(pair.Value, out var hash))
                        result[pair.Key] = hash;
                }
                return result;
            }, ct);

            var states = await Task.Run(() =>
            {
                var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    result[path] = FileState.Read(path);
                }
                return result;
            }, ct);

            var replacement = new PerceptualHashSnapshot(files, states, parsed);
            Interlocked.Exchange(ref _perceptualSnapshot, replacement);
            TouchPerceptualSnapshot();
            return replacement;
        }
        finally
        {
            _perceptualSnapshotBuildGate.Release();
        }
    }

    private void ReleaseIdlePerceptualSnapshot()
    {
        if (Volatile.Read(ref _activePerceptualSearches) != 0 ||
            Volatile.Read(ref _perceptualSnapshot) is null)
            return;
        var lastAccessTicks = Interlocked.Read(ref _perceptualSnapshotLastAccessUtcTicks);
        if (lastAccessTicks == 0 ||
            DateTime.UtcNow.Ticks - lastAccessTicks < PerceptualSnapshotIdleTimeout.Ticks)
            return;
        Interlocked.Exchange(ref _perceptualSnapshot, null);
        AppLogger.Memory("PerceptualSearch snapshot released after idle timeout");
    }

    private void TouchPerceptualSnapshot() =>
        Interlocked.Exchange(ref _perceptualSnapshotLastAccessUtcTicks, DateTime.UtcNow.Ticks);

    public void Dispose()
    {
        _perceptualSnapshotIdleTimer.Dispose();
        _vectorSnapshotIdleTimer.Dispose();
        _perceptualSnapshotBuildGate.Dispose();
        foreach (var gate in _vectorSnapshotBuildGates.Values)
            gate.Dispose();
        _vectorSnapshotBuildGates.Clear();
        _vectorSnapshots.Clear();
        _vectorSnapshotLastAccessUtcTicks.Clear();
        Interlocked.Exchange(ref _perceptualSnapshot, null);
    }

    private static (float Score, bool IsStrict) ScorePerceptualMatch(
        in ParsedPerceptualHash baseHash,
        in ParsedPerceptualHash candidateHash)
    {
        var averageDistance = BitOperations.PopCount(baseHash.Average ^ candidateHash.Average);
        var differenceDistance = BitOperations.PopCount(baseHash.Difference ^ candidateHash.Difference);
        var perceptualDistance = BitOperations.PopCount(baseHash.Perceptual ^ candidateHash.Perceptual);
        var histogramSimilarity = CompareHistograms(baseHash.Histogram, candidateHash.Histogram);
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

    private static bool TryParsePerceptualHash(string? value, out ParsedPerceptualHash hash)
    {
        hash = default;
        if (string.IsNullOrEmpty(value))
            return false;

        var parts = value.Split('|');
        if (parts.Length < 4 ||
            !TryParseBinaryHash(parts[0], out var average) ||
            !TryParseBinaryHash(parts[1], out var difference) ||
            !TryParseBinaryHash(parts[2], out var perceptual))
            return false;

        var histogramParts = parts[3].Split(',');
        if (histogramParts.Length == 0)
            return false;

        var histogram = new double[histogramParts.Length];
        for (var index = 0; index < histogramParts.Length; index++)
        {
            if (!double.TryParse(histogramParts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out histogram[index]))
                return false;
        }

        hash = new ParsedPerceptualHash(average, difference, perceptual, histogram);
        return true;
    }

    private static bool TryParseBinaryHash(string value, out ulong hash)
    {
        hash = 0;
        if (value.Length != 64)
            return false;

        foreach (var bit in value)
        {
            if (bit is not ('0' or '1'))
                return false;
            hash = (hash << 1) | (uint)(bit - '0');
        }
        return true;
    }

    private static float CompareHistograms(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        if (left.Length != right.Length)
            return 0;

        double intersection = 0;
        for (var index = 0; index < left.Length; index++)
            intersection += Math.Min(left[index], right[index]);
        return (float)intersection;
    }

    private readonly record struct ParsedPerceptualHash(
        ulong Average,
        ulong Difference,
        ulong Perceptual,
        double[] Histogram);

    private sealed class PerceptualHashSnapshot
    {
        private readonly string[] _paths;
        private readonly IReadOnlyDictionary<string, FileState> _fileStates;

        public PerceptualHashSnapshot(
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, FileState> fileStates,
            IReadOnlyDictionary<string, ParsedPerceptualHash> hashes)
        {
            _paths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            _fileStates = new Dictionary<string, FileState>(fileStates, StringComparer.OrdinalIgnoreCase);
            Hashes = new Dictionary<string, ParsedPerceptualHash>(hashes, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, ParsedPerceptualHash> Hashes { get; }

        public bool Matches(IReadOnlyList<string> paths)
        {
            if (_paths.Length != paths.Count)
                return false;

            var sortedPaths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var index = 0; index < _paths.Length; index++)
            {
                if (!string.Equals(_paths[index], sortedPaths[index], StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!_fileStates.TryGetValue(sortedPaths[index], out var expected) ||
                    !expected.Equals(FileState.Read(sortedPaths[index])))
                    return false;
            }
            return true;
        }
    }

    private readonly record struct VectorSnapshotKey(string ModelKey, string ModelVersion);

    private readonly record struct VectorSnapshotLoadResult(
        VectorSnapshot Snapshot,
        bool CacheHit,
        bool CacheBuilt,
        int BatchCount);

    private sealed class VectorSnapshot
    {
        private readonly string[] _paths;
        private readonly IReadOnlyDictionary<string, FileState> _fileStates;

        public VectorSnapshot(
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, FileState> fileStates,
            IReadOnlyList<VectorSnapshotEntry> entries)
        {
            _paths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            _fileStates = new Dictionary<string, FileState>(fileStates, StringComparer.OrdinalIgnoreCase);
            Entries = entries.ToArray();
        }

        public IReadOnlyList<VectorSnapshotEntry> Entries { get; }

        public bool Matches(IReadOnlyList<string> paths, int expectedDimension)
        {
            if (_paths.Length != paths.Count)
                return false;

            var sortedPaths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var index = 0; index < _paths.Length; index++)
            {
                if (!string.Equals(_paths[index], sortedPaths[index], StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!_fileStates.TryGetValue(sortedPaths[index], out var expected) ||
                    !expected.Equals(FileState.Read(sortedPaths[index])))
                    return false;
            }

            return Entries.All(item => item.Embedding.Length == expectedDimension);
        }
    }

    private readonly record struct VectorSnapshotEntry(string FilePath, float[] Embedding);

    private readonly record struct FileState(long Length, long LastWriteTicks)
    {
        public static FileState Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileState(info.Length, info.LastWriteTimeUtc.Ticks)
                    : new FileState(-1, 0);
            }
            catch (IOException)
            {
                return new FileState(-1, 0);
            }
            catch (UnauthorizedAccessException)
            {
                return new FileState(-1, 0);
            }
        }
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

    private static double GetManagedMemoryMb() => GC.GetTotalMemory(false) / 1048576.0;

}
