using System.Collections.Concurrent;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

internal readonly record struct VectorSnapshotKey(string ModelKey, string ModelVersion);

internal readonly record struct VectorSnapshotLoadResult(
    VectorSnapshot Snapshot,
    bool CacheHit,
    bool CacheBuilt,
    int BatchCount);

internal readonly record struct VectorSnapshotEntry(string FilePath, float[] Embedding);

internal readonly record struct FileState(long Length, long LastWriteTicks)
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

internal sealed class PerceptualHashSnapshot
{
    private readonly string[] _paths;
    private readonly IReadOnlyDictionary<string, FileState> _fileStates;

    public PerceptualHashSnapshot(
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, FileState> fileStates,
        IReadOnlyDictionary<string, ParsedPerceptualHash> hashes,
        long version)
    {
        _paths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        _fileStates = new Dictionary<string, FileState>(fileStates, StringComparer.OrdinalIgnoreCase);
        Hashes = new Dictionary<string, ParsedPerceptualHash>(hashes, StringComparer.OrdinalIgnoreCase);
        Version = version;
    }

    public IReadOnlyDictionary<string, ParsedPerceptualHash> Hashes { get; }
    public long Version { get; }

    public bool Matches(IReadOnlyList<string> paths, long currentVersion)
    {
        if (Version != currentVersion)
            return false;

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

internal sealed class VectorSnapshot
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

internal sealed class SimilaritySnapshotManager : IDisposable
{
    private const int VectorQueryBatchSize = 500;
    private static readonly TimeSpan PerceptualSnapshotIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VectorSnapshotIdleTimeout = TimeSpan.FromMinutes(30);

    private readonly IImageHashRepository _hashRepository;
    private readonly IImageEmbeddingRepository _embeddingRepository;
    private readonly IHashDataVersionTracker? _versionTracker;

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
    private int _perceptualSnapshotBuildCount;

    public int PerceptualSnapshotBuildCount => Volatile.Read(ref _perceptualSnapshotBuildCount);
    public Action? PerceptualSnapshotBuildingHook { get; set; }

    public SimilaritySnapshotManager(
        IImageHashRepository hashRepository,
        IImageEmbeddingRepository embeddingRepository,
        IHashDataVersionTracker? versionTracker = null)
    {
        _hashRepository = hashRepository ?? throw new ArgumentNullException(nameof(hashRepository));
        _embeddingRepository = embeddingRepository ?? throw new ArgumentNullException(nameof(embeddingRepository));
        _versionTracker = versionTracker;

        _perceptualSnapshotIdleTimer = new Timer(
            static state => ((SimilaritySnapshotManager)state!).ReleaseIdlePerceptualSnapshot(),
            this,
            PerceptualSnapshotIdleTimeout,
            PerceptualSnapshotIdleTimeout);

        _vectorSnapshotIdleTimer = new Timer(
            static state => ((SimilaritySnapshotManager)state!).ReleaseIdleVectorSnapshots(),
            this,
            VectorSnapshotIdleTimeout,
            VectorSnapshotIdleTimeout);
    }

    public void OnPerceptualSearchStarted() => Interlocked.Increment(ref _activePerceptualSearches);
    public void OnPerceptualSearchEnded() => Interlocked.Decrement(ref _activePerceptualSearches);

    public void OnVectorSearchStarted() => Interlocked.Increment(ref _activeVectorSearches);
    public void OnVectorSearchEnded() => Interlocked.Decrement(ref _activeVectorSearches);

    public async Task<VectorSnapshotLoadResult> GetVectorSnapshotAsync(
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

    public async Task<PerceptualHashSnapshot> GetPerceptualHashSnapshotAsync(
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        ReleaseIdlePerceptualSnapshot();
        var currentVersion = _versionTracker?.CurrentVersion ?? 0;
        var snapshot = Volatile.Read(ref _perceptualSnapshot);
        if (snapshot is not null && await Task.Run(() => snapshot.Matches(files, currentVersion), ct))
        {
            TouchPerceptualSnapshot();
            return snapshot;
        }

        await _perceptualSnapshotBuildGate.WaitAsync(ct);
        try
        {
            ReleaseIdlePerceptualSnapshot();
            currentVersion = _versionTracker?.CurrentVersion ?? 0;
            snapshot = Volatile.Read(ref _perceptualSnapshot);
            if (snapshot is not null && await Task.Run(() => snapshot.Matches(files, currentVersion), ct))
            {
                TouchPerceptualSnapshot();
                return snapshot;
            }

            Interlocked.Increment(ref _perceptualSnapshotBuildCount);
            var buildVersion = currentVersion;
            PerceptualSnapshotBuildingHook?.Invoke();

            var hashes = await _hashRepository.GetPerceptualHashesByPathsAsync(files.ToList());
            var parsed = await Task.Run(() =>
            {
                var result = new Dictionary<string, ParsedPerceptualHash>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in hashes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (SimilarImageScorer.TryParsePerceptualHash(pair.Value, out var hash))
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

            var replacement = new PerceptualHashSnapshot(files, states, parsed, buildVersion);
            var endVersion = _versionTracker?.CurrentVersion ?? 0;
            if (endVersion == buildVersion)
            {
                Interlocked.Exchange(ref _perceptualSnapshot, replacement);
                TouchPerceptualSnapshot();
            }
            else
            {
                // Version changed during build: do not publish stale cache
                Interlocked.CompareExchange(ref _perceptualSnapshot, null, snapshot);
            }

            return replacement;
        }
        finally
        {
            _perceptualSnapshotBuildGate.Release();
        }
    }

    public void InvalidateVectorSnapshots()
    {
        foreach (var key in _vectorSnapshots.Keys)
            _vectorSnapshots.TryRemove(key, out _);
        _vectorSnapshotLastAccessUtcTicks.Clear();
    }

    public void InvalidatePerceptualSnapshot()
    {
        Interlocked.Exchange(ref _perceptualSnapshot, null);
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
}
