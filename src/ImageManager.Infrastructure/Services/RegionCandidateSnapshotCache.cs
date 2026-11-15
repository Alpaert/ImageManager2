using System.Data;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Services;

internal readonly record struct RegionCandidateModel(string Key, string Version, int Dimension);

internal readonly record struct RegionCandidateLoadResult(
    IReadOnlyList<ImageEmbeddingRecord> Rows, bool CacheHit);

public sealed class RegionCandidateSnapshotCache : IDisposable
{
    private readonly string _connectionString;
    private readonly long _byteLimit;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ITimer _idleTimer;
    private SqliteConnection? _connection;
    private long? _dataVersion;
    private int _disposed;

    public RegionCandidateSnapshotCache(string databasePath)
        : this(databasePath, 256L * 1024 * 1024, TimeSpan.FromMinutes(2), TimeProvider.System)
    {
    }

    internal RegionCandidateSnapshotCache(string databasePath, long byteLimit, TimeSpan idleTimeout,
        TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString();
        _byteLimit = byteLimit;
        _idleTimeout = idleTimeout;
        _timeProvider = timeProvider;
        var cleanupInterval = TimeSpan.FromTicks(Math.Max(1, idleTimeout.Ticks / 12));
        _idleTimer = timeProvider.CreateTimer(static state => ((RegionCandidateSnapshotCache)state!).ReleaseIdle(),
            this, cleanupInterval, cleanupInterval);
    }

    internal async Task<RegionCandidateLoadResult> GetAsync(
        RegionCandidateModel model, HashSet<string> paths,
        Func<CancellationToken, Task<List<ImageEmbeddingRecord>>> load, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            RemoveIdle();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var version = ReadDataVersion();
                var versionConnection = _connection;
                if (_snapshots.TryGetValue(model.Key, out var snapshot) &&
                    snapshot.Model == model && snapshot.Paths.SetEquals(paths))
                {
                    ct.ThrowIfCancellationRequested();
                    snapshot.LastUse = _timeProvider.GetTimestamp();
                    return new RegionCandidateLoadResult(snapshot.Rows, true);
                }

                _snapshots.Remove(model.Key);
                var rows = FilterUsableVectors(await load(ct).ConfigureAwait(false), model.Dimension, ct);
                if (version is null)
                    return new RegionCandidateLoadResult(rows, false);

                var estimatedBytes = EstimateSize(paths, rows, ct);
                var versionAfterLoad = ReadDataVersion();
                ct.ThrowIfCancellationRequested();
                if (versionAfterLoad != version || !ReferenceEquals(versionConnection, _connection))
                    continue;

                if (estimatedBytes <= _byteLimit && Volatile.Read(ref _disposed) == 0)
                {
                    while (_snapshots.Values.Sum(item => item.EstimatedBytes) + estimatedBytes > _byteLimit)
                        _snapshots.Remove(_snapshots.Values.MinBy(item => item.LastUse)!.Model.Key);

                    _snapshots.Add(model.Key, new Snapshot(model, paths, rows, estimatedBytes,
                        _timeProvider.GetTimestamp()));
                }
                return new RegionCandidateLoadResult(rows, false);
            }
            throw new InvalidOperationException("向量索引正在更新，请稍后重新搜索。");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static List<ImageEmbeddingRecord> FilterUsableVectors(List<ImageEmbeddingRecord> rows,
        int dimension, CancellationToken ct)
    {
        rows.RemoveAll(row =>
        {
            ct.ThrowIfCancellationRequested();
            return !RegionSearchService.IsUsableVector(row.Embedding, dimension);
        });
        ct.ThrowIfCancellationRequested();
        return rows;
    }

    private long? ReadDataVersion()
    {
        try
        {
            if (_connection?.State != ConnectionState.Open)
            {
                ResetConnection();
                _connection = new SqliteConnection(_connectionString);
                _connection.Open();
                using var setReadOnly = _connection.CreateCommand();
                setReadOnly.CommandText = "PRAGMA query_only=ON";
                setReadOnly.ExecuteNonQuery();
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA data_version";
            if (command.ExecuteScalar() is not long version)
                throw new InvalidOperationException("SQLite 未返回数据版本。");

            // Values are comparable only within the lifetime of this connection.
            if (_dataVersion != version)
                _snapshots.Clear();
            _dataVersion = version;
            return version;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            ResetConnection();
            AppLogger.Warn($"RegionSearch.CandidateVersionUnavailable: {ex.Message}");
            return null;
        }
    }

    private void ResetConnection()
    {
        _snapshots.Clear();
        _dataVersion = null;
        _connection?.Dispose();
        _connection = null;
    }

    private static long EstimateSize(HashSet<string> paths, List<ImageEmbeddingRecord> rows, CancellationToken ct)
    {
        // Include path set capacity, record and array overhead, plus separately loaded strings.
        long bytes = 256;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            bytes += 64 + 2L * path.Length;
        }
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            bytes += 128 + 2L * (row.FilePath.Length + (row.FileHash?.Length ?? 0)) + 4L * row.Embedding.Length;
        }
        return bytes + 8L * rows.Capacity;
    }

    private void ReleaseIdle()
    {
        if (!_gate.Wait(0))
            return;
        try
        {
            RemoveIdle();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RemoveIdle()
    {
        foreach (var entry in _snapshots)
        {
            if (_timeProvider.GetElapsedTime(entry.Value.LastUse) >= _idleTimeout)
                _snapshots.Remove(entry.Key);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _idleTimer.Dispose();
        _ = CloseWhenIdleAsync();
    }

    private async Task CloseWhenIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Snapshot(RegionCandidateModel model, HashSet<string> paths, List<ImageEmbeddingRecord> rows,
        long estimatedBytes, long lastUse)
    {
        public RegionCandidateModel Model { get; } = model;
        public HashSet<string> Paths { get; } = paths;
        public List<ImageEmbeddingRecord> Rows { get; } = rows;
        public long EstimatedBytes { get; } = estimatedBytes;
        public long LastUse { get; set; } = lastUse;
    }
}
