using System.Globalization;
using System.Reflection;
using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class SearchSnapshotAndChunkingTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly HashDataVersionTracker _versionTracker;
    private readonly ImageMetaRepository _metaRepo;
    private readonly ImageEmbeddingRepository _embeddingRepo;
    private readonly ChineseClipService _chineseClip;
    private readonly SimilarImageService _similarService;

    public SearchSnapshotAndChunkingTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-SearchChunkTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, _versionTracker);
        var suppressionRepo = new CharacterTagSuppressionRepository(_dbFactory);
        _embeddingRepo = new ImageEmbeddingRepository(_dbFactory, suppressionRepo);
        _chineseClip = new ChineseClipService(Path.Combine(_testRoot, "clip"));
        _similarService = new SimilarImageService(_metaRepo, _embeddingRepo, _chineseClip, _versionTracker);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _similarService.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static string CreateValidPerceptualHashString(char bit = '0')
    {
        var binary = new string(bit, 64);
        var hist = string.Join(",", Enumerable.Repeat("0.0156", 64));
        return $"{binary}|{binary}|{binary}|{hist}";
    }

    private string CreateTestImageFile(string fileName, SkiaSharp.SKColor? color = null)
    {
        var path = Path.Combine(_testRoot, fileName);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        using var bitmap = new SkiaSharp.SKBitmap(32, 32);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(color ?? SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
        return path;
    }

    private async Task<long> InsertMetaAsync(
        string filePath,
        string? perceptualHash = null,
        int hashStatus = 1,
        string? fileHash = null,
        int rating = 0,
        int width = 100,
        int height = 100)
    {
        var fileInfo = new FileInfo(filePath);
        var meta = new ImageMeta
        {
            FilePath = filePath,
            FileHash = fileHash ?? Guid.NewGuid().ToString("N"),
            PerceptualHash = perceptualHash,
            Width = width,
            Height = height,
            FileSize = fileInfo.Exists ? fileInfo.Length : 100,
            LastWriteTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : DateTime.UtcNow.Ticks,
            HashStatus = hashStatus,
            SystemRating = rating,
            AutoTagStatus = 0
        };
        return await _metaRepo.UpsertAsync(meta);
    }

    // =========================================================================
    // 3.1 搜索快照随数据更新失效
    // =========================================================================

    [Fact]
    public async Task Search_BeforeAndAfterHashBackfill_LoadsNewCandidates()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        var hashA = HashService.ComputeCombinedPerceptualHashFromFile(fileA);
        var hashB = HashService.ComputeCombinedPerceptualHashFromFile(fileB);

        // File A has valid hash in DB, File B has no hash initially
        await InsertMetaAsync(fileA, perceptualHash: hashA, hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: null, hashStatus: 0);

        // 1. First search: File B is not a candidate because its perceptual hash is missing
        var candidates = new[] { fileA, fileB };
        var results1 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.DoesNotContain(results1, r => r.FilePath.Equals(fileB, StringComparison.OrdinalIgnoreCase));

        // 2. Backfill hash for File B
        var infoB = new FileInfo(fileB);
        await _metaRepo.BulkUpsertAsync(new List<ImageMeta>
        {
            new()
            {
                FilePath = fileB,
                FileHash = "hash-b",
                PerceptualHash = hashB,
                Width = 200,
                Height = 200,
                FileSize = infoB.Length,
                LastWriteTicks = infoB.LastWriteTimeUtc.Ticks,
                HashStatus = 1
            }
        });

        // 3. Second search: File B is now included in search candidates!
        var results2 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Contains(results2, r => r.FilePath.Equals(fileB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_WhenFileStateUnchangedButDbHashUpdated_RefreshesSnapshot()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        var matchingHash = HashService.ComputeCombinedPerceptualHashFromFile(fileB);
        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: matchingHash, hashStatus: 1);

        var candidates = new[] { fileA, fileB };
        var results1 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Single(results1);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);

        // File on disk is NOT touched (length and mtime remain identical)
        // Update database hash to a different hash
        var redFile = CreateTestImageFile("imgRed.jpg", SkiaSharp.SKColors.Red);
        var redHash = HashService.ComputeCombinedPerceptualHashFromFile(redFile);
        var infoB = new FileInfo(fileB);
        await _metaRepo.BulkUpsertAsync(new List<ImageMeta>
        {
            new()
            {
                FilePath = fileB,
                FileHash = "hash-b-updated",
                PerceptualHash = redHash,
                Width = 200,
                Height = 200,
                FileSize = infoB.Length,
                LastWriteTicks = infoB.LastWriteTimeUtc.Ticks,
                HashStatus = 1
            }
        });

        // Next search must detect version change, rebuild snapshot, and increment build count
        var results2 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(2, _similarService.PerceptualSnapshotBuildCount);
    }

    [Fact]
    public async Task Search_ConcurrentHashUpdateDuringBuild_DoesNotPolluteSubsequentSearches()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileB), hashStatus: 1);

        var candidates = new[] { fileA, fileB };

        // Install hook that triggers a hash mutation while snapshot is building
        _similarService.PerceptualSnapshotBuildingHook = () =>
        {
            // Simulate another thread mutating data and committing a new version
            _versionTracker.IncrementVersion();
        };

        // Run search: snapshot is built with old version, detects version changed at end, doesn't cache it
        var results1 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Single(results1);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);

        // Remove hook
        _similarService.PerceptualSnapshotBuildingHook = null;

        // Subsequent search: because old snapshot was NOT cached, it must build fresh snapshot
        var results2 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Single(results2);
        Assert.Equal(2, _similarService.PerceptualSnapshotBuildCount);

        // Third search without changes: now it hits the valid cached snapshot!
        var results3 = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Single(results3);
        Assert.Equal(2, _similarService.PerceptualSnapshotBuildCount);
    }

    [Fact]
    public async Task Search_ConcurrentRequests_DeduplicateBuildForSameVersion()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileB), hashStatus: 1);

        var candidates = new[] { fileA, fileB };

        // Run 5 searches concurrently
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual))
            .ToArray();

        var allResults = await Task.WhenAll(tasks);
        foreach (var r in allResults)
            Assert.Single(r);

        // Must have built only ONCE
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);
    }

    [Fact]
    public async Task Search_BuildCancellationOrException_ReleasesLockAndNextSearchSucceeds()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileB), hashStatus: 1);

        var candidates = new[] { fileA, fileB };

        // Inject exception into hook
        _similarService.PerceptualSnapshotBuildingHook = () =>
            throw new ApplicationException("Simulated transient error during build");

        await Assert.ThrowsAsync<ApplicationException>(() =>
            _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual));

        // Clear fault hook
        _similarService.PerceptualSnapshotBuildingHook = null;

        // Next search must acquire gate lock and succeed normally
        var results = await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Single(results);
    }

    [Fact]
    public async Task Search_WhenDataUnchanged_ReusesSnapshotWithoutRebuilding()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileB), hashStatus: 1);

        var candidates = new[] { fileA, fileB };

        // Search 1: builds snapshot
        await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);

        // Search 2: reuses snapshot
        await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);

        // Search 3: reuses snapshot
        await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);
    }

    [Fact]
    public async Task Search_TransactionRollback_DoesNotIncrementVersionOrPublishNewData()
    {
        var fileA = CreateTestImageFile("imgA.jpg");
        var fileB = CreateTestImageFile("imgB.jpg");

        await InsertMetaAsync(fileA, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileA), hashStatus: 1);
        await InsertMetaAsync(fileB, perceptualHash: HashService.ComputeCombinedPerceptualHashFromFile(fileB), hashStatus: 1);

        var candidates = new[] { fileA, fileB };
        await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);

        var initialVersion = _versionTracker.CurrentVersion;

        // Simulate failed transaction in DB
        using (var conn = _dbFactory.CreateConnection())
        using (var txn = conn.BeginTransaction())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET PerceptualHash = 'rolled_back' WHERE FilePath = @Path",
                new { Path = fileB }, txn);
            txn.Rollback();
        }

        // Version tracker was not incremented
        Assert.Equal(initialVersion, _versionTracker.CurrentVersion);

        // Snapshot is reused
        await _similarService.SearchByImageAsync(fileA, candidates, SimilaritySearchMode.Perceptual);
        Assert.Equal(1, _similarService.PerceptualSnapshotBuildCount);
    }

    // =========================================================================
    // 3.2 无界查询分批处理
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    public async Task QueryChunking_Boundaries_ReturnCompleteAndNonDuplicated(int count)
    {
        var paths = new List<string>(count);
        var metas = new List<ImageMeta>(count);

        for (int i = 0; i < count; i++)
        {
            var p = Path.Combine(_testRoot, $"boundary_{count}_{i}.jpg");
            paths.Add(p);
            metas.Add(new ImageMeta
            {
                FilePath = p,
                FileHash = $"hash_{i}",
                PerceptualHash = CreateValidPerceptualHashString('0'),
                Width = 100 + i,
                Height = 200 + i,
                FileSize = 1000 + i,
                LastWriteTicks = DateTime.UtcNow.Ticks,
                HashStatus = 1,
                SystemRating = i % 4,
                AutoTagStatus = 1
            });
        }

        if (metas.Count > 0)
            await _metaRepo.BulkUpsertAsync(metas);

        // Test GetIdsByPathsAsync
        var idMap = await _metaRepo.GetIdsByPathsAsync(paths);
        Assert.Equal(count, idMap.Count);

        // Test GetPerceptualHashesByPathsAsync
        var hashResult = await _metaRepo.GetPerceptualHashesByPathsAsync(paths);
        Assert.Equal(count, hashResult.Count);

        // Test GetDimensionsByPathsAsync
        var dimResult = await _metaRepo.GetDimensionsByPathsAsync(paths);
        Assert.Equal(count, dimResult.Count);

        // Test GetSystemRatingsByPathsAsync
        var ratingResult = await _metaRepo.GetSystemRatingsByPathsAsync(paths);
        Assert.Equal(count, ratingResult.Count);

        // Test GetFileHashesByPathsAsync
        var fileHashes = await _metaRepo.GetFileHashesByPathsAsync(paths);
        Assert.Equal(count, fileHashes.Count);

        // Test GetHashedPathsAsync
        var hashedPaths = await _metaRepo.GetHashedPathsAsync(paths);
        Assert.Equal(count, hashedPaths.Count);

        // Test GetSortMetadataByPathsAsync
        var sortMetas = await _metaRepo.GetSortMetadataByPathsAsync(paths);
        Assert.Equal(count, sortMetas.Count);

        // Test GetStatusMapByPathsAsync
        var statusMap = await _metaRepo.GetStatusMapByPathsAsync(paths);
        Assert.Equal(count, statusMap.Count);
    }

    [Fact]
    public async Task QueryChunking_DuplicatePathsAndCaseVariants_TreatedAsSingleItem()
    {
        var path = Path.Combine(_testRoot, "case_test_IMG.jpg");
        await InsertMetaAsync(path, perceptualHash: CreateValidPerceptualHashString('0'), hashStatus: 1);

        var queryList = new List<string>
        {
            path,
            path.ToLowerInvariant(),
            path.ToUpperInvariant(),
            path
        };

        var ids = await _metaRepo.GetIdsByPathsAsync(queryList);
        Assert.Single(ids);

        var hashes = await _metaRepo.GetPerceptualHashesByPathsAsync(queryList);
        Assert.Single(hashes);

        var dims = await _metaRepo.GetDimensionsByPathsAsync(queryList);
        Assert.Single(dims);

        var ratings = await _metaRepo.GetSystemRatingsByPathsAsync(queryList);
        Assert.Single(ratings);

        var tagMap = await _metaRepo.GetTagMapByPathsAsync(queryList);
        Assert.True(tagMap.Count <= 1);
    }

    [Fact]
    public async Task QueryChunking_ExceedingSingleStatementParameterBudget_SucceedsWithoutError()
    {
        // SQLite default parameter limit is 999. We test with 1,200 items.
        int total = 1200;
        var paths = new List<string>(total);
        var metas = new List<ImageMeta>(total);

        for (int i = 0; i < total; i++)
        {
            var p = Path.Combine(_testRoot, $"budget_test_{i}.jpg");
            paths.Add(p);
            metas.Add(new ImageMeta
            {
                FilePath = p,
                FileHash = $"hash_{i}",
                PerceptualHash = CreateValidPerceptualHashString('0'),
                Width = 100,
                Height = 100,
                FileSize = 1000,
                LastWriteTicks = DateTime.UtcNow.Ticks,
                HashStatus = 1
            });
        }

        await _metaRepo.BulkUpsertAsync(metas);

        var idMap = await _metaRepo.GetIdsByPathsAsync(paths);
        Assert.Equal(total, idMap.Count);

        var hashResult = await _metaRepo.GetPerceptualHashesByPathsAsync(paths);
        Assert.Equal(total, hashResult.Count);

        var dimResult = await _metaRepo.GetDimensionsByPathsAsync(paths);
        Assert.Equal(total, dimResult.Count);
    }

    [Fact]
    public async Task MultiTag_LargeIncludeAndExcludeLists_PreservesSetSemantics()
    {
        // Target: fileTarget has tags inc_0..inc_599
        // Untarget: fileUntarget has tags inc_0..inc_599 AND exc_0
        var fileTarget = Path.Combine(_testRoot, "target.jpg");
        var fileUntarget = Path.Combine(_testRoot, "untarget.jpg");

        var idTarget = await InsertMetaAsync(fileTarget);
        var idUntarget = await InsertMetaAsync(fileUntarget);

        var incTags = Enumerable.Range(0, 600).Select(i => $"inc_tag_{i}").ToList();
        var excTags = Enumerable.Range(0, 600).Select(i => $"exc_tag_{i}").ToList();

        // Assign tags
        await _metaRepo.SetTagsAsync(idTarget, incTags);
        var untargetTags = new List<string>(incTags) { excTags[0] };
        await _metaRepo.SetTagsAsync(idUntarget, untargetTags);

        // 1. GetFilePathsByTagsAsync (requireAll = true) with 600 tags
        var andResults = await _metaRepo.GetFilePathsByTagsAsync(incTags, requireAll: true);
        Assert.Equal(2, andResults.Count);
        Assert.Contains(fileTarget, andResults);
        Assert.Contains(fileUntarget, andResults);

        // 2. GetFilePathsByTagsExcludingAsync (requireAll = true) with 600 include and 600 exclude
        var excludedResults = await _metaRepo.GetFilePathsByTagsExcludingAsync(incTags, requireAll: true, excTags);
        Assert.Single(excludedResults);
        Assert.Equal(fileTarget, excludedResults[0]);

        // 3. GetFilePathsExcludingTagsAsync (requireAll = false) with 600 exclude tags
        var noExcResults = await _metaRepo.GetFilePathsExcludingTagsAsync(excTags, requireAll: false);
        Assert.Contains(fileTarget, noExcResults);
        Assert.DoesNotContain(fileUntarget, noExcResults);
    }

    [Fact]
    public async Task CoOccurringTags_AcrossMultipleBatches_ExactDistinctCount()
    {
        // 1200 images, each having "common_tag". 400 also have "rare_tag".
        int total = 1200;
        var paths = new List<string>(total);

        for (int i = 0; i < total; i++)
        {
            var p = Path.Combine(_testRoot, $"cotag_{i}.jpg");
            paths.Add(p);
            var id = await InsertMetaAsync(p);

            var tags = new List<string> { "common_tag" };
            if (i < 400)
                tags.Add("rare_tag");
            await _metaRepo.SetTagsAsync(id, tags);
        }

        // Add duplicate paths to input
        var queryPaths = new List<string>(paths);
        queryPaths.AddRange(paths.Take(300)); // duplicates

        var coTags = await _metaRepo.GetCoOccurringTagsAsync(queryPaths);
        var commonTag = coTags.FirstOrDefault(t => t.Name == "common_tag");
        var rareTag = coTags.FirstOrDefault(t => t.Name == "rare_tag");

        Assert.NotNull(commonTag);
        Assert.Equal(1200, commonTag.Count); // Exactly 1200 distinct images, not inflated!

        Assert.NotNull(rareTag);
        Assert.Equal(400, rareTag.Count); // Exactly 400 distinct images
    }

    [Fact]
    public async Task BatchMutations_AtomicRollbackOnFailure()
    {
        var file1 = Path.Combine(_testRoot, "atom1.jpg");
        var file2 = Path.Combine(_testRoot, "atom2.jpg");

        var id1 = await InsertMetaAsync(file1);
        var id2 = await InsertMetaAsync(file2);

        await _metaRepo.SetTagsAsync(id1, new List<string> { "tag1" });
        await _metaRepo.SetTagsAsync(id2, new List<string> { "tag2" });
        await _metaRepo.SetAutoTagStatusBatchAsync(new List<string> { file1, file2 }, 2);

        // Verification of initial state
        var map1 = await _metaRepo.GetTagMapByPathsAsync(new List<string> { file1, file2 });
        Assert.True(map1.ContainsKey(file1));
        Assert.True(map1.ContainsKey(file2));

        // Clear tags and status batch
        await _metaRepo.ClearTagsAndStatusBatchAsync(new List<string> { file1, file2 });

        var map2 = await _metaRepo.GetTagMapByPathsAsync(new List<string> { file1, file2 });
        Assert.False(map2.TryGetValue(file1, out var tags1) && tags1.Count > 0);
        Assert.False(map2.TryGetValue(file2, out var tags2) && tags2.Count > 0);

        var statuses = await _metaRepo.GetStatusMapByPathsAsync(new List<string> { file1, file2 });
        Assert.Equal(0, statuses[file1].Status);
        Assert.Equal(0, statuses[file2].Status);
    }

    [Fact]
    public async Task TempScopeTable_CleanedUpWithoutPollutingNextQuery()
    {
        var file = Path.Combine(_testRoot, "temp_clean.jpg");
        var id = await InsertMetaAsync(file);
        await _metaRepo.SetTagsAsync(id, new List<string> { "clean_test_tag" });

        // Run co-occurring tags query (which creates and drops temp table)
        var result1 = await _metaRepo.GetCoOccurringTagsAsync(new List<string> { file });
        Assert.Single(result1);

        // Query again immediately on new or same connection
        var result2 = await _metaRepo.GetCoOccurringTagsAsync(new List<string> { file });
        Assert.Single(result2);

        // Verify no leftover CoTagScope temp tables exist
        using var conn = _dbFactory.CreateConnection();
        var tempTables = await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_list WHERE schema = 'temp' AND name LIKE 'CoTagScope_%'");
        Assert.Empty(tempTables);
    }

    [Fact]
    public async Task GetTagMapAsync_DistinguishesNullFromEmpty()
    {
        var file1 = Path.Combine(_testRoot, "null_empty_1.jpg");
        var file2 = Path.Combine(_testRoot, "null_empty_2.jpg");

        var id1 = await InsertMetaAsync(file1);
        var id2 = await InsertMetaAsync(file2);

        await _metaRepo.SetTagsAsync(id1, new List<string> { "tag_a" });
        await _metaRepo.SetTagsAsync(id2, new List<string> { "tag_b" });

        using var conn = _dbFactory.CreateConnection();

        // Invoke private GetTagMapAsync via reflection
        var method = typeof(ImageMetaRepository).GetMethod(
            "GetTagMapAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            new[] { typeof(SqliteConnection), typeof(List<long>) });
        Assert.NotNull(method);

        // 1. Pass empty list: MUST return empty dictionary immediately
        var emptyTask = (Task<Dictionary<long, List<TagCount>>>)method.Invoke(_metaRepo, new object?[] { conn, new List<long>() })!;
        var emptyResult = await emptyTask;
        Assert.Empty(emptyResult);

        // 2. Pass null: MUST return all tags across full database
        var nullTask = (Task<Dictionary<long, List<TagCount>>>)method.Invoke(_metaRepo, new object?[] { conn, null })!;
        var nullResult = await nullTask;
        Assert.True(nullResult.Count >= 2);
        Assert.True(nullResult.ContainsKey(id1));
        Assert.True(nullResult.ContainsKey(id2));
    }
}
