using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class RegionCandidateSnapshotTests : IDisposable
{
    private static readonly RegionCandidateModel Pixai = new("pixai", "v0.9", 1024);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ImageManager2-region-");
    private readonly TestDatabase _database;
    private readonly RegionCandidateSnapshotCache _cache;

    public RegionCandidateSnapshotTests()
    {
        _database = new TestDatabase(Path.Combine(_root.FullName, "test.db"));
        using var connection = _database.CreateConnection();
        connection.Execute("PRAGMA journal_mode=WAL");
        DatabaseInitializer.Initialize(connection);
        _cache = new RegionCandidateSnapshotCache(_database.Path);
    }

    [Fact]
    public async Task PixaiServiceReusesScopeWhenQueryChangesAndRechecksFiles()
    {
        var a = CreatePixaiFile("a.jpg");
        var b = CreatePixaiFile("b.jpg");
        var c = CreatePixaiFile("c.jpg");
        var repository = new ImageEmbeddingRepository(_database, new CharacterTagSuppressionRepository(_database));
        using var sscd = new SscdEmbeddingService(_root.FullName);
        using var pixai = new PixaiTagService();
        var service = new RegionSearchService(repository, repository, sscd, pixai, _root.FullName, _cache);

        Task<RegionSearchResponse> Search(string query, params string[] paths) =>
            service.SearchAsync(query, null, paths, RegionSearchMode.SimilarContent, limit: 0);

        var readsBefore = _database.OpenCount;
        File.Delete(c.FilePath);
        var first = await Search(a.FilePath, a.FilePath, b.FilePath, c.FilePath);
        Assert.Equal(2, first.CandidateImages);
        Assert.Equal(1, first.IndexedImages);
        Assert.Empty(first.Results);
        File.AppendAllText(a.FilePath, "modified");
        File.WriteAllText(c.FilePath, "source");
        File.SetLastWriteTimeUtc(c.FilePath, new DateTime(c.SourceLastWriteTicks, DateTimeKind.Utc));

        var second = await Search(b.FilePath, c.FilePath, a.FilePath.ToUpperInvariant(), b.FilePath, b.FilePath);
        Assert.Equal(2, second.CandidateImages);
        Assert.Equal(1, second.IndexedImages);
        Assert.Empty(second.Results);

        File.WriteAllText(a.FilePath, "source");
        File.SetLastWriteTimeUtc(a.FilePath, new DateTime(a.SourceLastWriteTicks, DateTimeKind.Utc));
        Assert.Equal(2, (await Search(b.FilePath, a.FilePath, b.FilePath, c.FilePath)).IndexedImages);
        Assert.Equal(readsBefore + 1, _database.OpenCount);

        using var writer = _database.CreateConnection();
        writer.Execute("DELETE FROM ImageEmbedding WHERE ImageMetaId = @ImageMetaId", c);
        var readsAfterCommit = _database.OpenCount;
        Assert.Equal(1, (await Search(b.FilePath, a.FilePath, b.FilePath, c.FilePath)).IndexedImages);
        Assert.Equal(readsAfterCommit + 1, _database.OpenCount);
    }

    [Fact]
    public async Task CacheRetainsOnlyMostRecentScopePerModelAndValidVectors()
    {
        var a = MakeRow("a.jpg", [1, 2]);
        var b = MakeRow("b.jpg", [2, 1]);
        var model = new RegionCandidateModel("model", "v1", 2);
        var other = new RegionCandidateModel("other", "v1", 2);
        var loads = 0;
        Task<List<ImageEmbeddingRecord>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<List<ImageEmbeddingRecord>>([
                a, b, MakeRow("zero.jpg", [0, 0]), MakeRow("nan.jpg", [float.NaN, 1]), MakeRow("dim.jpg", [1])]);
        }

        var full = Scope(a.FilePath, b.FilePath);
        var first = await _cache.GetAsync(model, full, Load, default);
        Assert.False(first.CacheHit);
        Assert.Equal(new[] { a, b }, first.Rows);
        Assert.True((await _cache.GetAsync(model, Scope(b.FilePath, a.FilePath.ToUpperInvariant()), Load, default)).CacheHit);
        Assert.False((await _cache.GetAsync(other, full, Load, default)).CacheHit);
        Assert.True((await _cache.GetAsync(model, full, Load, default)).CacheHit);
        Assert.False((await _cache.GetAsync(model, Scope(a.FilePath), Load, default)).CacheHit);
        Assert.False((await _cache.GetAsync(model, full, Load, default)).CacheHit);
        Assert.False((await _cache.GetAsync(model with { Version = "v2" }, full, Load, default)).CacheHit);
        Assert.False((await _cache.GetAsync(model with { Version = "v2", Dimension = 3 }, full, Load, default)).CacheHit);
        Assert.Equal(6, loads);
    }

    [Fact]
    public async Task CommitDuringLoadRetriesAndCancellationDoesNotPublish()
    {
        var a = CreatePixaiFile("a.jpg");
        var paths = Scope(a.FilePath);
        var loads = 0;
        var repository = new ImageEmbeddingRepository(_database, new CharacterTagSuppressionRepository(_database));

        async Task<List<ImageEmbeddingRecord>> LoadWithCommit(CancellationToken token)
        {
            var rows = await repository.GetPixaiSearchEmbeddingsByPathsAsync(paths, token);
            if (++loads == 1)
            {
                using var writer = _database.CreateConnection();
                writer.Execute("DELETE FROM ImageEmbedding WHERE ImageMetaId = @ImageMetaId", a);
            }
            return rows;
        }

        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cache.GetAsync(Pixai, paths, _ =>
        {
            cancel.Cancel();
            return Task.FromResult<List<ImageEmbeddingRecord>>([a]);
        }, cancel.Token));

        var loaded = await _cache.GetAsync(Pixai, paths, LoadWithCommit, default);
        Assert.False(loaded.CacheHit);
        Assert.Empty(loaded.Rows);
        Assert.Equal(2, loads);
        Assert.True((await _cache.GetAsync(Pixai, paths, LoadWithCommit, default)).CacheHit);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task CacheHonorsSharedBudgetAndIdleExpiry()
    {
        var clock = new ManualClock();
        using var cache = new RegionCandidateSnapshotCache(_database.Path, 7000, TimeSpan.FromMinutes(2), clock);
        var other = Pixai with { Key = "other" };
        var vector = new float[1024];
        vector[0] = 1;
        var a = MakeRow("a.jpg", vector);
        var b = MakeRow("b.jpg", (float[])vector.Clone());
        var paths = Scope(a.FilePath);
        Task<List<ImageEmbeddingRecord>> Single(CancellationToken _) => Task.FromResult<List<ImageEmbeddingRecord>>([a]);
        Task<List<ImageEmbeddingRecord>> Oversized(CancellationToken _) => Task.FromResult<List<ImageEmbeddingRecord>>([a, b]);

        var largeScope = Scope(a.FilePath, b.FilePath);
        Assert.Equal(2, (await cache.GetAsync(Pixai, largeScope, Oversized, default)).Rows.Count);
        Assert.False((await cache.GetAsync(Pixai, largeScope, Oversized, default)).CacheHit);
        Assert.False((await cache.GetAsync(Pixai, paths, Single, default)).CacheHit);
        Assert.True((await cache.GetAsync(Pixai, paths, Single, default)).CacheHit);
        Assert.False((await cache.GetAsync(other, paths, Single, default)).CacheHit);
        Assert.False((await cache.GetAsync(Pixai, paths, Single, default)).CacheHit);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await cache.GetAsync(Pixai, paths, Single, default)).CacheHit);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False((await cache.GetAsync(Pixai, paths, Single, default)).CacheHit);
    }

    [Fact]
    public async Task DisposeReturnsWhileLoaderIsRunning()
    {
        var cache = new RegionCandidateSnapshotCache(_database.Path);
        var row = MakeRow("a.jpg", [1]);
        var model = Pixai with { Dimension = 1 };
        var paths = Scope(row.FilePath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<List<ImageEmbeddingRecord>> Load(CancellationToken token)
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return [row];
        }

        var loading = cache.GetAsync(model, paths, Load, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(cache.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            cache.Dispose();
        }

        Assert.Single((await loading).Rows);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync(model, paths, Load, default));
    }

    private ImageEmbeddingRecord CreatePixaiFile(string name)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, "source");
        var file = new FileInfo(path);
        var vector = new float[1024];
        vector[0] = 1;
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        using var connection = _database.CreateConnection();
        var id = connection.ExecuteScalar<long>("""
            INSERT INTO ImageMeta (FilePath, FileSize, LastWriteTicks)
            VALUES (@Path, @Size, @Ticks) RETURNING Id
            """, new { Path = path, Size = file.Length, Ticks = file.LastWriteTimeUtc.Ticks });
        connection.Execute("""
            INSERT INTO ImageEmbedding (ImageMetaId, ModelKey, ModelVersion, EmbeddingDim, EmbeddingBlob)
            VALUES (@Id, 'pixai', 'v0.9', 1024, @Blob)
            """, new { Id = id, Blob = blob });
        return new ImageEmbeddingRecord(id, path, null, vector, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private ImageEmbeddingRecord MakeRow(string name, float[] vector) => new(0, Path.Combine(_root.FullName, name), null, vector);
    private static HashSet<string> Scope(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _cache.Dispose();
        _root.Delete(recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class TestDatabase(string path) : IDbContextFactory
    {
        public string Path { get; } = path;
        public int OpenCount { get; private set; }

        public SqliteConnection CreateConnection(int commandTimeout = 30)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                DefaultTimeout = commandTimeout
            }.ToString());
            connection.Open();
            OpenCount++;
            return connection;
        }
    }
}
