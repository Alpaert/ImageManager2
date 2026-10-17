using System;
using System.IO;
using System.Threading.Tasks;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class ConcurrentWriteSafetyTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;

    public ConcurrentWriteSafetyTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-WriteSafetyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _metaRepo = new ImageMetaRepository(_dbFactory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpdateHashBatch_WhenVersionMatches_UpdatesDatabaseAndReportsSuccess()
    {
        var filePath = Path.Combine(_testRoot, "image1.jpg");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
        var fi = new FileInfo(filePath);

        var regResult = await _metaRepo.RegisterNewFilesBatchAsync(new[]
        {
            new ImageMeta { FilePath = filePath, FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks }
        });
        Assert.Equal(1, regResult);

        var meta = await _metaRepo.GetByPathAsync(filePath);
        Assert.NotNull(meta);

        var updateItem = new ImageHashUpdateItem(
            meta!.Id,
            filePath,
            fi.LastWriteTimeUtc.Ticks,
            fi.Length,
            "md5hash_123",
            "phash_456",
            1920,
            1080,
            1);

        var updateResult = await _metaRepo.UpdateHashBatchAsync(new[] { updateItem });

        Assert.Equal(1, updateResult.UpdatedCount);
        Assert.Equal(0, updateResult.StaleCount);

        var updatedMeta = await _metaRepo.GetByPathAsync(filePath);
        Assert.NotNull(updatedMeta);
        Assert.Equal("md5hash_123", updatedMeta!.FileHash);
        Assert.Equal("phash_456", updatedMeta.PerceptualHash);
        Assert.Equal(1920, updatedMeta.Width);
        Assert.Equal(1080, updatedMeta.Height);
    }

    [Fact]
    public async Task UpdateHashBatch_WhenPathMoved_DetectsStale_DoesNotReviveOldRecord()
    {
        var oldPath = Path.Combine(_testRoot, "old_image.jpg");
        var newPath = Path.Combine(_testRoot, "new_image.jpg");
        await File.WriteAllBytesAsync(oldPath, new byte[] { 1, 2, 3 });
        var fi = new FileInfo(oldPath);

        await _metaRepo.RegisterNewFilesBatchAsync(new[]
        {
            new ImageMeta { FilePath = oldPath, FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks }
        });

        var meta = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(meta);

        // Simulate moving the file in DB
        await _metaRepo.UpdateFilePathAsync(meta!.Id, newPath, 0);

        // Now a stale background hash result arrives carrying the OLD path
        var staleItem = new ImageHashUpdateItem(
            meta.Id,
            oldPath, // Stale path!
            fi.LastWriteTimeUtc.Ticks,
            fi.Length,
            "stale_md5",
            "stale_phash",
            100,
            100,
            1);

        var result = await _metaRepo.UpdateHashBatchAsync(new[] { staleItem });

        // Stale update must affect 0 rows, report stale, and NOT re-create the old path
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.StaleCount);

        var oldMetaCheck = await _metaRepo.GetByPathAsync(oldPath);
        Assert.Null(oldMetaCheck); // Must NOT revive old path!

        var newMetaCheck = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(newMetaCheck);
        Assert.Null(newMetaCheck!.FileHash); // Remains unchanged
    }

    [Fact]
    public async Task UpdateHashBatch_WhenFileSizeOrTicksChanged_DetectsStale()
    {
        var filePath = Path.Combine(_testRoot, "modified.jpg");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
        var fi = new FileInfo(filePath);

        await _metaRepo.RegisterNewFilesBatchAsync(new[]
        {
            new ImageMeta { FilePath = filePath, FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks }
        });

        var meta = await _metaRepo.GetByPathAsync(filePath);
        Assert.NotNull(meta);

        // Stale item with mismatched last write ticks
        var staleTicksItem = new ImageHashUpdateItem(
            meta!.Id,
            filePath,
            fi.LastWriteTimeUtc.Ticks - 1000, // Mismatched ticks!
            fi.Length,
            "hash",
            "phash",
            100, 100, 1);

        var resultTicks = await _metaRepo.UpdateHashBatchAsync(new[] { staleTicksItem });
        Assert.Equal(0, resultTicks.UpdatedCount);
        Assert.Equal(1, resultTicks.StaleCount);

        // Stale item with mismatched file size
        var staleSizeItem = new ImageHashUpdateItem(
            meta.Id,
            filePath,
            fi.LastWriteTimeUtc.Ticks,
            fi.Length + 999, // Mismatched size!
            "hash",
            "phash",
            100, 100, 1);

        var resultSize = await _metaRepo.UpdateHashBatchAsync(new[] { staleSizeItem });
        Assert.Equal(0, resultSize.UpdatedCount);
        Assert.Equal(1, resultSize.StaleCount);
    }
}
