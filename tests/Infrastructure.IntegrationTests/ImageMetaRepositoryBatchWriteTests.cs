using Dapper;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class ImageMetaRepositoryBatchWriteTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly HashDataVersionTracker _versionTracker;
    private readonly ImageMetaRepository _metaRepo;

    public ImageMetaRepositoryBatchWriteTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-BatchWriteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "batch_test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, _versionTracker);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Fact]
    public async Task BulkUpsertAsync_LargeBatchExceeding100Items_ChunksAndUpsertsAll()
    {
        const int totalCount = 250;
        var metas = new List<ImageMeta>(totalCount);
        for (int i = 0; i < totalCount; i++)
        {
            metas.Add(new ImageMeta
            {
                FilePath = Path.Combine(_testRoot, $"bulk_{i}.png"),
                FileHash = $"hash_{i}",
                PerceptualHash = new string('0', 64),
                Width = 100,
                Height = 100,
                FileSize = 1000 + i,
                LastWriteTicks = 638000000000000000L + i,
                HashStatus = 1
            });
        }

        // Act 1: Initial batch insert of 250 items (exceeds 100 chunk limit)
        await _metaRepo.BulkUpsertAsync(metas);

        // Assert 1: All 250 inserted
        using (var conn = _dbFactory.CreateConnection())
        {
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ImageMeta;");
            Assert.Equal(totalCount, count);
        }

        // Act 2: Bulk update all 250 with new FileSize
        foreach (var m in metas)
        {
            m.FileSize = 99999;
        }
        await _metaRepo.BulkUpsertAsync(metas);

        // Assert 2: All 250 updated in place, no duplicate rows
        using (var conn = _dbFactory.CreateConnection())
        {
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ImageMeta;");
            Assert.Equal(totalCount, count);

            var updatedCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ImageMeta WHERE FileSize = 99999;");
            Assert.Equal(totalCount, updatedCount);
        }
    }

    [Fact]
    public async Task BulkUpsertAsync_TransactionRollbackOnFailure_PreservesOriginalData()
    {
        var metas = new List<ImageMeta>
        {
            new() { FilePath = Path.Combine(_testRoot, "f1.png"), FileHash = "orig1", FileSize = 100 },
            new() { FilePath = Path.Combine(_testRoot, "f2.png"), FileHash = "orig2", FileSize = 200 }
        };
        await _metaRepo.BulkUpsertAsync(metas);

        // Prepare updates
        var updates = new List<ImageMeta>
        {
            new() { FilePath = Path.Combine(_testRoot, "f1.png"), FileHash = "new1", FileSize = 111 },
            new() { FilePath = Path.Combine(_testRoot, "f2.png"), FileHash = "new2", FileSize = 222 }
        };

        // Install trigger that fails on updating f2.png
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_update_f2
                BEFORE UPDATE ON ImageMeta
                FOR EACH ROW
                WHEN NEW.FilePath LIKE '%f2.png'
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated update failure');
                END;");
        }

        // Act: Run updates
        await Assert.ThrowsAnyAsync<SqliteException>(() => _metaRepo.BulkUpsertAsync(updates));

        // Assert: Atomic rollback; f1 was NOT updated to new1
        var f1 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "f1.png"));
        Assert.NotNull(f1);
        Assert.Equal("orig1", f1.FileHash);
        Assert.Equal(100, f1.FileSize);

        // Drop trigger and retry
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DROP TRIGGER trg_fail_update_f2;");
        }

        await _metaRepo.BulkUpsertAsync(updates);
        var f1Updated = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "f1.png"));
        Assert.Equal("new1", f1Updated!.FileHash);
    }

    [Fact]
    public async Task SetTagsAsync_TransactionRollbackOnFailure_PreservesOriginalTags()
    {
        var path = Path.Combine(_testRoot, "tagged.png");
        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = path,
            FileHash = "h1",
            FileSize = 100
        });

        // Set initial tags
        await _metaRepo.SetTagsAsync(id, new List<string> { "tagA", "tagB" });
        var metaBefore = await _metaRepo.GetByPathAsync(path);
        Assert.Equal(2, metaBefore!.Tags.Count);

        // Install trigger on ImageTag that aborts on INSERT of 'failTag'
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_imagetag_insert
                BEFORE INSERT ON ImageTag
                FOR EACH ROW
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated tag insert failure');
                END;");
        }

        // Act: Attempt to replace tags with new tags
        await Assert.ThrowsAnyAsync<SqliteException>(() =>
            _metaRepo.SetTagsAsync(id, new List<string> { "newTag1", "newTag2" }));

        // Assert: Even though DELETE happened before INSERT in SetTagsAsync,
        // the rollback restored the original tags!
        var metaAfter = await _metaRepo.GetByPathAsync(path);
        Assert.NotNull(metaAfter);
        Assert.Equal(2, metaAfter.Tags.Count);
        Assert.Contains(metaAfter.Tags, t => t.Name == "tagA");
        Assert.Contains(metaAfter.Tags, t => t.Name == "tagB");

        // Drop trigger and retry
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DROP TRIGGER trg_fail_imagetag_insert;");
        }

        await _metaRepo.SetTagsAsync(id, new List<string> { "newTag1", "newTag2" });
        var metaFinal = await _metaRepo.GetByPathAsync(path);
        Assert.Equal(2, metaFinal!.Tags.Count);
        Assert.Contains(metaFinal.Tags, t => t.Name == "newTag1");
        Assert.Contains(metaFinal.Tags, t => t.Name == "newTag2");
    }

    [Fact]
    public async Task AddTagToImagesAsync_BatchAcrossMultipleImages_IsAtomic()
    {
        var id1 = await _metaRepo.UpsertAsync(new ImageMeta { FilePath = Path.Combine(_testRoot, "b1.png"), FileHash = "b1" });
        var id2 = await _metaRepo.UpsertAsync(new ImageMeta { FilePath = Path.Combine(_testRoot, "b2.png"), FileHash = "b2" });
        var id3 = await _metaRepo.UpsertAsync(new ImageMeta { FilePath = Path.Combine(_testRoot, "b3.png"), FileHash = "b3" });

        // Act 1: Success batch add
        await _metaRepo.AddTagToImagesAsync(new List<long> { id1, id2, id3 }, "common_tag");

        var m1 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b1.png"));
        var m2 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b2.png"));
        var m3 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b3.png"));
        Assert.Contains(m1!.Tags, t => t.Name == "common_tag");
        Assert.Contains(m2!.Tags, t => t.Name == "common_tag");
        Assert.Contains(m3!.Tags, t => t.Name == "common_tag");

        // Act 2: Failure on 3rd image rolls back all
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync($@"
                CREATE TRIGGER trg_fail_on_id3
                BEFORE INSERT ON ImageTag
                FOR EACH ROW
                WHEN NEW.ImageMetaId = {id3}
                BEGIN
                    SELECT RAISE(ABORT, 'Fail on id3');
                END;");
        }

        await Assert.ThrowsAnyAsync<SqliteException>(() =>
            _metaRepo.AddTagToImagesAsync(new List<long> { id1, id2, id3 }, "aborted_tag"));

        // Assert 2: None of the images have aborted_tag
        m1 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b1.png"));
        m2 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b2.png"));
        m3 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "b3.png"));
        Assert.DoesNotContain(m1!.Tags, t => t.Name == "aborted_tag");
        Assert.DoesNotContain(m2!.Tags, t => t.Name == "aborted_tag");
        Assert.DoesNotContain(m3!.Tags, t => t.Name == "aborted_tag");
    }

    [Fact]
    public async Task ClearTagsFromImagesAsync_ClearsOnlyTargetImages()
    {
        var id1 = await _metaRepo.UpsertAsync(new ImageMeta { FilePath = Path.Combine(_testRoot, "c1.png"), FileHash = "c1" });
        var id2 = await _metaRepo.UpsertAsync(new ImageMeta { FilePath = Path.Combine(_testRoot, "c2.png"), FileHash = "c2" });

        await _metaRepo.SetTagsAsync(id1, new List<string> { "tag1" });
        await _metaRepo.SetTagsAsync(id2, new List<string> { "tag2" });

        // Act: Clear only id1
        await _metaRepo.ClearTagsFromImagesAsync(new List<long> { id1 });

        // Assert
        var m1 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "c1.png"));
        var m2 = await _metaRepo.GetByPathAsync(Path.Combine(_testRoot, "c2.png"));
        Assert.Empty(m1!.Tags);
        Assert.Single(m2!.Tags);
        Assert.Equal("tag2", m2.Tags[0].Name);
    }

    [Fact]
    public async Task UpsertAsync_StaleScanAfterMove_PreservesNewerMetadataAndTargetFilePath()
    {
        var oldPath = Path.Combine(_testRoot, "stale_old.png");
        var newPath = Path.Combine(_testRoot, "stale_new.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000,
            Width = 10,
            Height = 10
        });
        await _metaRepo.SetTagsAsync(id, new List<string> { "persisted_tag" });

        var stale = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(stale);

        // Simulate move in database
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET FilePath = @NewPath WHERE Id = @Id", new { NewPath = newPath, Id = id });
        }

        // Target record is updated with newer content
        var newer = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(newer);
        newer!.FileHash = "NEW";
        newer.FileSize = 200;
        newer.LastWriteTicks = 2000;
        newer.Width = 20;
        newer.Height = 20;
        await _metaRepo.UpsertAsync(newer);

        // Attempt delayed stale scan upsert
        var returnedId = await _metaRepo.UpsertAsync(stale!);
        Assert.Equal(id, returnedId);

        // Assert: destination retains newer data
        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal(newPath, final!.FilePath);
        Assert.Equal("NEW", final.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Equal(20, final.Width);
        Assert.Equal(20, final.Height);
        Assert.Contains(final.Tags, t => t.Name == "persisted_tag");

        // Old path must not exist
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task BulkUpsertAsync_StaleScanAfterMove_PreservesNewerMetadataAndTargetFilePath()
    {
        var oldPath = Path.Combine(_testRoot, "bulk_stale_old.png");
        var newPath = Path.Combine(_testRoot, "bulk_stale_new.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000,
            Width = 10,
            Height = 10
        });
        await _metaRepo.SetTagsAsync(id, new List<string> { "persisted_tag" });

        var stale = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(stale);

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET FilePath = @NewPath WHERE Id = @Id", new { NewPath = newPath, Id = id });
        }

        var newer = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(newer);
        newer!.FileHash = "NEW";
        newer.FileSize = 200;
        newer.LastWriteTicks = 2000;
        newer.Width = 20;
        newer.Height = 20;
        await _metaRepo.UpsertAsync(newer);

        // Attempt delayed stale scan bulk upsert
        await _metaRepo.BulkUpsertAsync(new List<ImageMeta> { stale! });

        // Assert: destination retains newer data
        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal(newPath, final!.FilePath);
        Assert.Equal("NEW", final.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Equal(20, final.Width);
        Assert.Equal(20, final.Height);
        Assert.Contains(final.Tags, t => t.Name == "persisted_tag");

        // Old path must not exist
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task UpsertAsync_SameVersionScanAfterMove_UpdatesHashAndPreservesTargetFilePath()
    {
        var oldPath = Path.Combine(_testRoot, "samever_old.png");
        var newPath = Path.Combine(_testRoot, "samever_new.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = null,
            FileSize = 100,
            LastWriteTicks = 1000
        });

        var meta = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(meta);
        meta!.FileHash = "COMPUTED_HASH";

        // Move happens, same file version
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET FilePath = @NewPath WHERE Id = @Id", new { NewPath = newPath, Id = id });
        }

        // Write scan results for the moved file
        var returnedId = await _metaRepo.UpsertAsync(meta);
        Assert.Equal(id, returnedId);

        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal(newPath, final!.FilePath);
        Assert.Equal("COMPUTED_HASH", final.FileHash);
        Assert.Equal(100, final.FileSize);
        Assert.Equal(1000, final.LastWriteTicks);
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task UpsertAsync_DirectUpdateOnSamePath_AllowsModifyingMetadata()
    {
        var filePath = Path.Combine(_testRoot, "direct_update.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = filePath,
            FileHash = "INITIAL",
            FileSize = 100,
            LastWriteTicks = 1000
        });

        var existing = await _metaRepo.GetByPathAsync(filePath);
        Assert.NotNull(existing);

        // File modified on disk at same path
        existing!.FileHash = "MODIFIED";
        existing.FileSize = 300;
        existing.LastWriteTicks = 3000;

        var returnedId = await _metaRepo.UpsertAsync(existing);
        Assert.Equal(id, returnedId);

        var updated = await _metaRepo.GetByPathAsync(filePath);
        Assert.NotNull(updated);
        Assert.Equal("MODIFIED", updated!.FileHash);
        Assert.Equal(300, updated.FileSize);
        Assert.Equal(3000, updated.LastWriteTicks);
    }

    [Fact]
    public async Task BulkUpsertAsync_SameBatch_NewThenOld_PreservesNewerMetadata()
    {
        var oldPath = Path.Combine(_testRoot, "samebatch_new_old_prev.png");
        var newPath = Path.Combine(_testRoot, "samebatch_new_old_dest.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000,
            Width = 10,
            Height = 10
        });
        await _metaRepo.SetTagsAsync(id, new List<string> { "batch_tag" });

        var old = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(old);

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET FilePath = @NewPath WHERE Id = @Id", new { NewPath = newPath, Id = id });
        }

        var fresh = new ImageMeta
        {
            Id = id,
            FilePath = oldPath,
            FileHash = "NEW",
            FileSize = 200,
            LastWriteTicks = 2000,
            Width = 20,
            Height = 20
        };

        // Act: Batch contains [fresh, old]
        await _metaRepo.BulkUpsertAsync(new List<ImageMeta> { fresh, old! });

        // Assert: destination retains newer data
        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal(newPath, final!.FilePath);
        Assert.Equal("NEW", final.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Equal(20, final.Width);
        Assert.Equal(20, final.Height);
        Assert.Contains(final.Tags, t => t.Name == "batch_tag");
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task BulkUpsertAsync_SameBatch_OldThenNew_UpdatesToNewerMetadata()
    {
        var oldPath = Path.Combine(_testRoot, "samebatch_old_new_prev.png");
        var newPath = Path.Combine(_testRoot, "samebatch_old_new_dest.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000,
            Width = 10,
            Height = 10
        });
        await _metaRepo.SetTagsAsync(id, new List<string> { "batch_tag2" });

        var old = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(old);

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE ImageMeta SET FilePath = @NewPath WHERE Id = @Id", new { NewPath = newPath, Id = id });
        }

        var fresh = new ImageMeta
        {
            Id = id,
            FilePath = oldPath,
            FileHash = "NEW",
            FileSize = 200,
            LastWriteTicks = 2000,
            Width = 20,
            Height = 20
        };

        // Act: Batch contains [old, fresh]
        await _metaRepo.BulkUpsertAsync(new List<ImageMeta> { old!, fresh });

        // Assert: destination updates to newer data
        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal(newPath, final!.FilePath);
        Assert.Equal("NEW", final.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Equal(20, final.Width);
        Assert.Equal(20, final.Height);
        Assert.Contains(final.Tags, t => t.Name == "batch_tag2");
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task UpsertAsync_FreshScanAfterMove_WhenMoveUpdatedRowTimestamp_AcceptsFreshContent()
    {
        var oldPath = Path.Combine(_testRoot, "fresh_move_single_old.png");
        var newPath = Path.Combine(_testRoot, "fresh_move_single_new.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000
        });

        var old = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(old);

        // Move updates FilePath and row UpdatedAt to later
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE ImageMeta SET FilePath = @NewPath, UpdatedAt = @UpdatedAt WHERE Id = @Id",
                new { NewPath = newPath, UpdatedAt = old!.UpdatedAt.AddSeconds(1), Id = id });
        }

        // Fresh scan obtained newer file content with ticks 2000
        var fresh = new ImageMeta
        {
            Id = id,
            FilePath = oldPath,
            FileHash = "NEW",
            FileSize = 200,
            LastWriteTicks = 2000,
            UpdatedAt = old.UpdatedAt // Earlier timestamp from before the move
        };

        var returnedId = await _metaRepo.UpsertAsync(fresh);
        Assert.Equal(id, returnedId);

        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal("NEW", final!.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }

    [Fact]
    public async Task BulkUpsertAsync_FreshScanAfterMove_WhenMoveUpdatedRowTimestamp_AcceptsFreshContent()
    {
        var oldPath = Path.Combine(_testRoot, "fresh_move_bulk_old.png");
        var newPath = Path.Combine(_testRoot, "fresh_move_bulk_new.png");

        var id = await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = oldPath,
            FileHash = "OLD",
            FileSize = 100,
            LastWriteTicks = 1000
        });

        var old = await _metaRepo.GetByPathAsync(oldPath);
        Assert.NotNull(old);

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE ImageMeta SET FilePath = @NewPath, UpdatedAt = @UpdatedAt WHERE Id = @Id",
                new { NewPath = newPath, UpdatedAt = old!.UpdatedAt.AddSeconds(1), Id = id });
        }

        var fresh = new ImageMeta
        {
            Id = id,
            FilePath = oldPath,
            FileHash = "NEW",
            FileSize = 200,
            LastWriteTicks = 2000,
            UpdatedAt = old.UpdatedAt
        };

        await _metaRepo.BulkUpsertAsync(new List<ImageMeta> { fresh });

        var final = await _metaRepo.GetByPathAsync(newPath);
        Assert.NotNull(final);
        Assert.Equal("NEW", final!.FileHash);
        Assert.Equal(200, final.FileSize);
        Assert.Equal(2000, final.LastWriteTicks);
        Assert.Null(await _metaRepo.GetByPathAsync(oldPath));
    }
}
