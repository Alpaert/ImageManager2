using System.Text;
using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class MediaMoveTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;
    private readonly FolderRepository _folderRepo;
    private readonly ImageEmbeddingRepository _embeddingRepo;
    private readonly TestThumbnailCacheService _thumbCache;

    public MediaMoveTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-MediaMoveTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _metaRepo = new ImageMetaRepository(_dbFactory);
        _folderRepo = new FolderRepository(_dbFactory);
        var suppressionRepo = new CharacterTagSuppressionRepository(_dbFactory);
        _embeddingRepo = new ImageEmbeddingRepository(_dbFactory, suppressionRepo);
        _thumbCache = new TestThumbnailCacheService();
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
        catch
        {
            // Ignore cleanup errors on temp dir
        }
        return Task.CompletedTask;
    }

    private async Task<long> InsertImageAsync(
        string filePath,
        long folderId = 0,
        int rating = 0,
        string? hash = null)
    {
        var meta = new ImageMeta
        {
            FilePath = filePath,
            FileHash = hash ?? Guid.NewGuid().ToString("N"),
            FileSize = 1024,
            Width = 800,
            Height = 600,
            SystemRating = rating,
            LastWriteTicks = DateTime.UtcNow.Ticks,
            FolderId = folderId,
            HashStatus = 0
        };
        var id = await _metaRepo.UpsertAsync(meta);
        if (rating >= 0)
        {
            await _metaRepo.SetSystemRatingByPathAsync(filePath, rating);
        }
        return id;
    }

    private static void CreatePhysicalFile(string path, string content = "TEST_IMAGE_BYTES")
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    [Fact]
    public async Task MoveImageWithMetadata_PreservesIdTagsRatingEmbedding_NoOldPathRemnant()
    {
        // 1. 移动带标签、评分和向量的图片：新路径可查到全部资料，图片 ID 不变，旧路径无残留记录
        var srcDir = Path.Combine(_testRoot, "Src1");
        var destDir = Path.Combine(_testRoot, "Dest1");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var srcFile = Path.Combine(srcDir, "photo.jpg");
        CreatePhysicalFile(srcFile, "PHOTO_CONTENT");

        await _folderRepo.AddAsync(srcDir);
        var srcFolder = await _folderRepo.GetByPathAsync(srcDir);
        Assert.NotNull(srcFolder);

        await _folderRepo.AddAsync(destDir);
        var destFolder = await _folderRepo.GetByPathAsync(destDir);
        Assert.NotNull(destFolder);

        var imageId = await InsertImageAsync(srcFile, folderId: srcFolder!.Id, rating: 3, hash: "HASH_PHOTO_1");

        // Set tags
        await _metaRepo.SetTagsAsync(imageId, ["nature", "scenery"]);
        await _metaRepo.AddAutoTagsAsync(imageId, ["outdoor"]);

        // Insert embedding
        var dummyVector = new float[512];
        dummyVector[0] = 0.5f;
        dummyVector[511] = 0.9f;
        var blob = new byte[512 * 4];
        Buffer.BlockCopy(dummyVector, 0, blob, 0, blob.Length);

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO ImageEmbedding (ImageMetaId, ModelKey, ModelVersion, EmbeddingDim, EmbeddingBlob, FileHash)
                VALUES (@Id, 'CLIP', 'v1', 512, @Blob, 'HASH_PHOTO_1')",
                new { Id = imageId, Blob = blob });
        }

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([srcFile], destDir));

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var expectedTarget = Path.Combine(destDir, "photo.jpg");
        Assert.False(File.Exists(srcFile));
        Assert.True(File.Exists(expectedTarget));

        // Old path should have NO remnant
        var oldMeta = await _metaRepo.GetByPathAsync(srcFile);
        Assert.Null(oldMeta);

        // New path should have all metadata preserved
        var newMeta = await _metaRepo.GetByPathAsync(expectedTarget);
        Assert.NotNull(newMeta);
        Assert.Equal(imageId, newMeta!.Id);
        Assert.Equal(3, newMeta.SystemRating);
        Assert.Equal("HASH_PHOTO_1", newMeta.FileHash);
        Assert.Equal(destFolder!.Id, newMeta.FolderId);

        // Verify tags
        var tags = newMeta.Tags.Select(t => t.Name).ToList();
        Assert.Contains("nature", tags);
        Assert.Contains("scenery", tags);
        Assert.Contains("outdoor", tags);

        // Verify embedding attached to imageId
        using (var conn = _dbFactory.CreateConnection())
        {
            var embCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ImageEmbedding WHERE ImageMetaId = @Id", new { Id = imageId });
            Assert.Equal(1, embCount);
        }

        // Verify thumbnail cache was moved
        Assert.Contains(srcFile, _thumbCache.MovedCaches.Keys);
        Assert.Equal(expectedTarget, _thumbCache.MovedCaches[srcFile]);
    }

    [Fact]
    public async Task MoveToUnmanagedDirectory_DisassociatesFolderId_PreservesMetadata()
    {
        // 2. 移动到未管理目录：不继续关联源目录，已有资料保留
        var srcDir = Path.Combine(_testRoot, "SrcManaged");
        var unmanagedDir = Path.Combine(_testRoot, "UnmanagedDir");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(unmanagedDir);

        await _folderRepo.AddAsync(srcDir);
        var srcFolder = await _folderRepo.GetByPathAsync(srcDir);
        Assert.NotNull(srcFolder);

        var srcFile = Path.Combine(srcDir, "pic.png");
        CreatePhysicalFile(srcFile);

        var imageId = await InsertImageAsync(srcFile, folderId: srcFolder!.Id, rating: 3, hash: "HASH_UNMANAGED_TEST");
        await _metaRepo.SetTagsAsync(imageId, ["tagged_item"]);

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([srcFile], unmanagedDir));

        Assert.Equal(1, result.SuccessCount);

        var targetFile = Path.Combine(unmanagedDir, "pic.png");
        Assert.True(File.Exists(targetFile));

        var meta = await _metaRepo.GetByPathAsync(targetFile);
        Assert.NotNull(meta);
        Assert.Equal(imageId, meta!.Id);
        Assert.Equal(0L, meta.FolderId); // Disassociated from source folder
        Assert.Equal(3, meta.SystemRating);
        Assert.Equal("HASH_UNMANAGED_TEST", meta.FileHash);

        var tags = meta.Tags.Select(t => t.Name).ToList();
        Assert.Single(tags);
        Assert.Equal("tagged_item", tags[0]);
    }

    [Fact]
    public async Task MoveUnindexedFile_MovesOnDisk_DoesNotCreateSpuriousDbRecord()
    {
        // 3. 移动没有数据库记录的文件：文件正常移动，不制造错误关联
        var srcDir = Path.Combine(_testRoot, "SrcUnindexed");
        var destDir = Path.Combine(_testRoot, "DestUnindexed");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var srcFile = Path.Combine(srcDir, "unindexed.jpg");
        CreatePhysicalFile(srcFile, "PURE_FILE_CONTENT");

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([srcFile], destDir));

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var targetFile = Path.Combine(destDir, "unindexed.jpg");
        Assert.True(File.Exists(targetFile));
        Assert.False(File.Exists(srcFile));

        // DB should have NO record
        var meta = await _metaRepo.GetByPathAsync(targetFile);
        Assert.Null(meta);

        // Journal table should be clean
        using var conn = _dbFactory.CreateConnection();
        var journalCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaMoveJournal");
        Assert.Equal(0, journalCount);
    }

    [Fact]
    public async Task TargetFileExists_GeneratesNonConflictingName_DoesNotOverwrite()
    {
        // 4. 目标已存在同名文件：原文件不被覆盖，返回真实的新文件名
        var srcDir = Path.Combine(_testRoot, "SrcCollision");
        var destDir = Path.Combine(_testRoot, "DestCollision");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var srcFile = Path.Combine(srcDir, "duplicate_name.jpg");
        var existingDestFile = Path.Combine(destDir, "duplicate_name.jpg");
        CreatePhysicalFile(srcFile, "NEW_SOURCE_FILE");
        CreatePhysicalFile(existingDestFile, "ORIGINAL_DEST_FILE");

        var imageId = await InsertImageAsync(srcFile);

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([srcFile], destDir));

        Assert.Equal(1, result.SuccessCount);

        // Original file at destination must NOT be overwritten
        Assert.Equal("ORIGINAL_DEST_FILE", File.ReadAllText(existingDestFile));

        // New file generated with non-conflicting name
        var expectedNewTarget = Path.Combine(destDir, "duplicate_name (1).jpg");
        Assert.True(File.Exists(expectedNewTarget));
        Assert.Equal("NEW_SOURCE_FILE", File.ReadAllText(expectedNewTarget));

        // Result reports actual target path
        Assert.Equal(expectedNewTarget, result.Items[0].TargetPath);

        // DB updated to actual target path
        var meta = await _metaRepo.GetByPathAsync(expectedNewTarget);
        Assert.NotNull(meta);
        Assert.Equal(imageId, meta!.Id);
    }

    [Fact]
    public async Task BatchMovePartialFailure_AccurateCountsAndFailedItems()
    {
        // 5. 移动第 N 个文件失败：已完成数量准确，失败项准确，无虚报成功
        var srcDir = Path.Combine(_testRoot, "SrcBatch");
        var destDir = Path.Combine(_testRoot, "DestBatch");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var file1 = Path.Combine(srcDir, "file1.jpg");
        var file2 = Path.Combine(srcDir, "file2.jpg");
        var file3 = Path.Combine(srcDir, "file3.jpg");
        CreatePhysicalFile(file1);
        CreatePhysicalFile(file2);
        CreatePhysicalFile(file3);

        await InsertImageAsync(file1);
        await InsertImageAsync(file2);
        await InsertImageAsync(file3);

        // Inject fault on file2 using custom file system
        var faultFs = new TestFaultInjectingFileSystem(failingMoveSourcePattern: "file2.jpg");
        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache, fileSystem: faultFs);

        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([file1, file2, file3], destDir));

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.HasFailures);

        Assert.Equal(MediaMoveItemStatus.Success, result.Items[0].Status);
        Assert.Equal(MediaMoveItemStatus.Failed, result.Items[1].Status);
        Assert.Equal(MediaMoveItemStatus.Success, result.Items[2].Status);

        Assert.True(File.Exists(Path.Combine(destDir, "file1.jpg")));
        Assert.True(File.Exists(file2)); // file2 remained at source
        Assert.True(File.Exists(Path.Combine(destDir, "file3.jpg")));
    }

    [Fact]
    public async Task DatabaseFailure_RestoresFileToSource_RollsBackDb()
    {
        // 6. 数据库更新失败：文件恢复原位置，数据库没有部分更新
        var srcDir = Path.Combine(_testRoot, "SrcDbFail");
        var destDir = Path.Combine(_testRoot, "DestDbFail");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var file = Path.Combine(srcDir, "important.jpg");
        CreatePhysicalFile(file, "IMPORTANT_DATA");
        var id = await InsertImageAsync(file, rating: 2);

        // Inject DB fault on target
        var moveService = new MediaMoveService(
            _dbFactory,
            _metaRepo,
            _folderRepo,
            _thumbCache,
            dbFaultHook: target => throw new InvalidOperationException("Simulated SQLite write error!"));

        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([file], destDir));

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.CompensationFailedCount);

        // File should be restored to original source location
        Assert.True(File.Exists(file));
        Assert.Equal("IMPORTANT_DATA", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(destDir, "important.jpg")));

        // Database should still have old path record intact
        var meta = await _metaRepo.GetByPathAsync(file);
        Assert.NotNull(meta);
        Assert.Equal(id, meta!.Id);
        Assert.Equal(2, meta.SystemRating);

        // No record at destination
        Assert.Null(await _metaRepo.GetByPathAsync(Path.Combine(destDir, "important.jpg")));
    }

    [Fact]
    public async Task DoubleFailure_DbAndFileRestoreFail_ReportsCompensationFailed_JournalPreserved()
    {
        // 7. 文件恢复也失败：返回待恢复明细，保留可用于恢复的记录
        var srcDir = Path.Combine(_testRoot, "SrcDoubleFail");
        var destDir = Path.Combine(_testRoot, "DestDoubleFail");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var file = Path.Combine(srcDir, "critical.jpg");
        CreatePhysicalFile(file, "CRITICAL_DATA");
        await InsertImageAsync(file);

        // Fault: DB fails AND restore move fails
        var faultFs = new TestFaultInjectingFileSystem();
        var moveService = new MediaMoveService(
            _dbFactory,
            _metaRepo,
            _folderRepo,
            _thumbCache,
            fileSystem: faultFs,
            dbFaultHook: _ =>
            {
                faultFs.FailNextMove = true;
                throw new InvalidOperationException("Simulated SQLite write error!");
            });

        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([file], destDir));

        Assert.Equal(1, result.CompensationFailedCount);
        Assert.Equal(MediaMoveItemStatus.CompensationFailed, result.Items[0].Status);

        // Journal should preserve the failed state for startup recovery
        using var conn = _dbFactory.CreateConnection();
        var journalEntry = await conn.QuerySingleOrDefaultAsync<(string State, string ErrorMessage)>(
            "SELECT State, ErrorMessage FROM MediaMoveJournal WHERE SourcePath = @Path", new { Path = file });
        Assert.NotNull(journalEntry.State);
        Assert.Equal("CompensationFailed", journalEntry.State);
        Assert.Contains("Simulated SQLite write error!", journalEntry.ErrorMessage);
    }

    [Fact]
    public async Task DirectoryMoveDbFailure_RollsBackEntireDirectoryAndDb()
    {
        // 8. 移动目录时某条元数据更新失败：整个数据库事务回滚，不留下半新半旧路径
        var srcDir = Path.Combine(_testRoot, "SrcDirFail");
        var destParent = Path.Combine(_testRoot, "DestParentFail");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destParent);

        var f1 = Path.Combine(srcDir, "sub1.jpg");
        var f2 = Path.Combine(srcDir, "sub2.jpg");
        CreatePhysicalFile(f1, "SUB1");
        CreatePhysicalFile(f2, "SUB2");

        var id1 = await InsertImageAsync(f1);
        var id2 = await InsertImageAsync(f2);

        var moveService = new MediaMoveService(
            _dbFactory,
            _metaRepo,
            _folderRepo,
            _thumbCache,
            dbFaultHook: _ => throw new InvalidOperationException("Simulated directory DB update failure!"));

        var result = await moveService.MoveDirectoryAsync(new DirectoryMoveRequest(srcDir, destParent));

        Assert.Equal(MediaMoveItemStatus.Failed, result.Status);

        // Directory must be restored to source location
        Assert.True(Directory.Exists(srcDir));
        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));

        var targetDir = Path.Combine(destParent, "SrcDirFail");
        Assert.False(Directory.Exists(targetDir));

        // Database must NOT have partial new paths
        var meta1 = await _metaRepo.GetByPathAsync(f1);
        var meta2 = await _metaRepo.GetByPathAsync(f2);
        Assert.NotNull(meta1);
        Assert.NotNull(meta2);
        Assert.Equal(id1, meta1!.Id);
        Assert.Equal(id2, meta2!.Id);
    }

    [Fact]
    public async Task CancellationMidBatch_RetainsCompletedItems_StopsSubsequent()
    {
        // 10. 用户在移动中取消：已完成项保留，当前项收尾，后续项不启动
        var srcDir = Path.Combine(_testRoot, "SrcCancel");
        var destDir = Path.Combine(_testRoot, "DestCancel");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var files = Enumerable.Range(1, 4)
            .Select(i => Path.Combine(srcDir, $"cancel_{i}.jpg"))
            .ToList();
        foreach (var f in files)
        {
            CreatePhysicalFile(f);
            await InsertImageAsync(f);
        }

        var cts = new CancellationTokenSource();
        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);

        var progress = new Progress<MediaMoveProgress>(p =>
        {
            // Cancel after 2nd file is processed
            if (p.Processed >= 2)
            {
                cts.Cancel();
            }
        });

        var result = await moveService.MoveFilesAsync(
            new MediaMoveRequest(files, destDir),
            progress: progress,
            cancellationToken: cts.Token);

        Assert.True(result.SuccessCount >= 2);
        Assert.True(result.CancelledCount >= 1);
        Assert.Equal(4, result.TotalCount);

        // First 2 files must exist at destination
        Assert.True(File.Exists(Path.Combine(destDir, "cancel_1.jpg")));
        Assert.True(File.Exists(Path.Combine(destDir, "cancel_2.jpg")));

        // Last file must still exist at source
        Assert.True(File.Exists(files[3]));
    }

    [Fact]
    public async Task CacheMigrationFailure_MoveStillSucceedsWithConsistentDb()
    {
        // 11. 缓存迁移失败：文件和元数据仍一致，缓存之后可重新生成
        var srcDir = Path.Combine(_testRoot, "SrcCacheFail");
        var destDir = Path.Combine(_testRoot, "DestCacheFail");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var file = Path.Combine(srcDir, "cached.jpg");
        CreatePhysicalFile(file, "IMAGE_DATA");
        var id = await InsertImageAsync(file, rating: 1);

        var failingCache = new TestThumbnailCacheService { ThrowOnMove = true };
        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, failingCache);

        var result = await moveService.MoveFilesAsync(new MediaMoveRequest([file], destDir));

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var targetPath = Path.Combine(destDir, "cached.jpg");
        Assert.True(File.Exists(targetPath));

        var meta = await _metaRepo.GetByPathAsync(targetPath);
        Assert.NotNull(meta);
        Assert.Equal(id, meta!.Id);
        Assert.Equal(1, meta.SystemRating);
    }

    [Fact]
    public async Task CrashRecovery_ResolvesPendingMovesAndIdentifiesCollisions()
    {
        // 12. 在关键步骤强制终止再重启：能识别并处理未完成操作，不覆盖冲突文件
        var srcDir = Path.Combine(_testRoot, "CrashSrc");
        var destDir = Path.Combine(_testRoot, "CrashDest");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        // Case A: File physically moved to target, but DB update crashed before committing
        var fA_Src = Path.Combine(srcDir, "crashA.jpg");
        var fA_Dest = Path.Combine(destDir, "crashA.jpg");
        CreatePhysicalFile(fA_Dest, "FILE_A_CONTENT"); // physically moved
        var idA = await InsertImageAsync(fA_Src, rating: 3);

        // Case B: Crashed before file was moved (file still at source)
        var fB_Src = Path.Combine(srcDir, "crashB.jpg");
        var fB_Dest = Path.Combine(destDir, "crashB.jpg");
        CreatePhysicalFile(fB_Src, "FILE_B_CONTENT"); // still at source
        var idB = await InsertImageAsync(fB_Src, rating: 2);

        // Case C: Conflict - files exist at BOTH source and destination!
        var fC_Src = Path.Combine(srcDir, "crashC.jpg");
        var fC_Dest = Path.Combine(destDir, "crashC.jpg");
        CreatePhysicalFile(fC_Src, "SRC_C");
        CreatePhysicalFile(fC_Dest, "DEST_C");
        await InsertImageAsync(fC_Src);

        // Pre-populate journal simulating crashed state
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO MediaMoveJournal (OperationId, ItemType, SourcePath, TargetPath, TargetFolderId, State)
                VALUES ('OP1', 'File', @SrcA, @DestA, 0, 'FilesystemMoved')",
                new { SrcA = fA_Src, DestA = fA_Dest });

            await conn.ExecuteAsync(@"
                INSERT INTO MediaMoveJournal (OperationId, ItemType, SourcePath, TargetPath, TargetFolderId, State)
                VALUES ('OP2', 'File', @SrcB, @DestB, 0, 'Moving')",
                new { SrcB = fB_Src, DestB = fB_Dest });

            await conn.ExecuteAsync(@"
                INSERT INTO MediaMoveJournal (OperationId, ItemType, SourcePath, TargetPath, TargetFolderId, State)
                VALUES ('OP3', 'File', @SrcC, @DestC, 0, 'FilesystemMoved')",
                new { SrcC = fC_Src, DestC = fC_Dest });
        }

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var recovered = await moveService.RecoverPendingMovesAsync();

        Assert.Equal(3, recovered.Count);

        // Item A: Recovered and completed metadata update to target
        var itemA = recovered.FirstOrDefault(r => r.SourcePath == fA_Src);
        Assert.NotNull(itemA);
        Assert.Equal(MediaMoveItemStatus.Success, itemA!.Status);
        var metaA = await _metaRepo.GetByPathAsync(fA_Dest);
        Assert.NotNull(metaA);
        Assert.Equal(idA, metaA!.Id);

        // Item B: Restored source state, journal entry deleted
        var itemB = recovered.FirstOrDefault(r => r.SourcePath == fB_Src);
        Assert.NotNull(itemB);
        Assert.Equal(MediaMoveItemStatus.Success, itemB!.Status);
        var metaB = await _metaRepo.GetByPathAsync(fB_Src);
        Assert.NotNull(metaB);
        Assert.Equal(idB, metaB!.Id);

        // Item C: Collision detected, did NOT overwrite, flagged as CompensationFailed
        var itemC = recovered.FirstOrDefault(r => r.SourcePath == fC_Src);
        Assert.NotNull(itemC);
        Assert.Equal(MediaMoveItemStatus.CompensationFailed, itemC!.Status);
        Assert.Equal("SRC_C", File.ReadAllText(fC_Src));
        Assert.Equal("DEST_C", File.ReadAllText(fC_Dest));
    }

    [Fact]
    public async Task DirectoryMove_PreservesRegisteredSubfoldersAndFolderIds()
    {
        // 验证移动包含已注册子文件夹的目录时，子文件夹的关联完整保留
        var rootDir = Path.Combine(_testRoot, "DirMoveSrc");
        var subDir = Path.Combine(rootDir, "SubRegistered");
        var targetParent = Path.Combine(_testRoot, "DirMoveDestParent");

        Directory.CreateDirectory(subDir);
        Directory.CreateDirectory(targetParent);

        // Register rootDir and subDir
        await _folderRepo.AddAsync(rootDir);
        await _folderRepo.AddAsync(subDir);

        var rootFolder = await _folderRepo.GetByPathAsync(rootDir);
        var subFolder = await _folderRepo.GetByPathAsync(subDir);
        Assert.NotNull(rootFolder);
        Assert.NotNull(subFolder);

        var rootFile = Path.Combine(rootDir, "root_image.jpg");
        var subFile = Path.Combine(subDir, "sub_image.jpg");
        CreatePhysicalFile(rootFile);
        CreatePhysicalFile(subFile);

        var idRoot = await InsertImageAsync(rootFile, folderId: rootFolder!.Id);
        var idSub = await InsertImageAsync(subFile, folderId: subFolder!.Id);

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);
        var result = await moveService.MoveDirectoryAsync(new DirectoryMoveRequest(rootDir, targetParent));

        Assert.Equal(MediaMoveItemStatus.Success, result.Status);

        var expectedNewRoot = Path.Combine(targetParent, "DirMoveSrc");
        var expectedNewSub = Path.Combine(expectedNewRoot, "SubRegistered");
        var expectedNewRootFile = Path.Combine(expectedNewRoot, "root_image.jpg");
        var expectedNewSubFile = Path.Combine(expectedNewSub, "sub_image.jpg");

        Assert.True(File.Exists(expectedNewRootFile));
        Assert.True(File.Exists(expectedNewSubFile));

        // Folder table updated paths
        var updatedRootFolder = await _folderRepo.GetByPathAsync(expectedNewRoot);
        var updatedSubFolder = await _folderRepo.GetByPathAsync(expectedNewSub);
        Assert.NotNull(updatedRootFolder);
        Assert.NotNull(updatedSubFolder);
        Assert.Equal(rootFolder.Id, updatedRootFolder!.Id);
        Assert.Equal(subFolder.Id, updatedSubFolder!.Id);

        // Subfolder image retains its subFolder.Id
        var metaSub = await _metaRepo.GetByPathAsync(expectedNewSubFile);
        Assert.NotNull(metaSub);
        Assert.Equal(idSub, metaSub!.Id);
        Assert.Equal(subFolder.Id, metaSub.FolderId);

        // Root file retains rootFolder.Id
        var metaRoot = await _metaRepo.GetByPathAsync(expectedNewRootFile);
        Assert.NotNull(metaRoot);
        Assert.Equal(idRoot, metaRoot!.Id);
        Assert.Equal(rootFolder.Id, metaRoot.FolderId);
    }

    [Fact]
    public async Task ConcurrentScanOrHashWithMove_DoesNotRegenerateOldPathOrLoseTags()
    {
        // 9. 哈希或扫描与移动重叠：不重新生成旧路径记录，不丢失标签
        var srcDir = Path.Combine(_testRoot, "SrcConcurrent");
        var destDir = Path.Combine(_testRoot, "DestConcurrent");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(destDir);

        var file = Path.Combine(srcDir, "concurrent.jpg");
        CreatePhysicalFile(file, "CONCURRENT_BYTES");
        var id = await InsertImageAsync(file, rating: 2, hash: "HASH_INITIAL");
        await _metaRepo.SetTagsAsync(id, ["tagA", "tagB"]);

        var moveService = new MediaMoveService(_dbFactory, _metaRepo, _folderRepo, _thumbCache);

        using var scanLoadedOldObject = new ManualResetEventSlim(false);
        var allowStaleWriteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Run simulated concurrent scan/hash: load old object first, pause until move + new update finish, then attempt stale write
        var scanTask = Task.Run(async () =>
        {
            var existing = await _metaRepo.GetByPathAsync(file);
            Assert.NotNull(existing);
            scanLoadedOldObject.Set();

            await allowStaleWriteGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _metaRepo.UpsertAsync(existing!);
        });

        Assert.True(scanLoadedOldObject.Wait(TimeSpan.FromSeconds(5)));

        // Perform move while scan holds stale object reference
        var moveResult = await moveService.MoveFilesAsync(new MediaMoveRequest([file], destDir));
        Assert.Equal(1, moveResult.SuccessCount);

        var targetFile = Path.Combine(destDir, "concurrent.jpg");
        Assert.True(File.Exists(targetFile));
        Assert.False(File.Exists(file));

        // Update target file with newer content in DB
        var movedMeta = await _metaRepo.GetByPathAsync(targetFile);
        Assert.NotNull(movedMeta);
        movedMeta!.FileHash = "HASH_NEWER_CONTENT";
        movedMeta.FileSize = 9999;
        movedMeta.LastWriteTicks += 50000;
        await _metaRepo.UpsertAsync(movedMeta);

        // Now release stale scan write: it must NOT overwrite newer destination metadata
        allowStaleWriteGate.TrySetResult(true);
        await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Old path must NOT be recreated
        var oldMeta = await _metaRepo.GetByPathAsync(file);
        Assert.Null(oldMeta);

        // Destination metadata must preserve ID, tags, and newer content
        var newMeta = await _metaRepo.GetByPathAsync(targetFile);
        Assert.NotNull(newMeta);
        Assert.Equal(id, newMeta!.Id);
        Assert.Equal("HASH_NEWER_CONTENT", newMeta.FileHash);
        Assert.Equal(9999, newMeta.FileSize);

        var tags = newMeta.Tags.Select(t => t.Name).ToList();
        Assert.Contains("tagA", tags);
        Assert.Contains("tagB", tags);
    }
}

internal sealed class TestThumbnailCacheService : IThumbnailCacheService
{
    public string CacheDirectory { get; set; } = string.Empty;
    public bool ThrowOnMove { get; set; }
    public Dictionary<string, string> MovedCaches { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void MoveDiskCache(string oldPath, string newPath)
    {
        if (ThrowOnMove)
            throw new IOException("Simulated thumbnail cache I/O failure!");
        MovedCaches[oldPath] = newPath;
    }

    public void InvalidateThumbnail(string filePath) { }
    public Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default)
        => Task.FromResult<(byte[]? Data, int Width, int Height)>((null, 0, 0));
    public Task ClearAsync() => Task.CompletedTask;
    public long EstimatedMemoryBytes => 0;
    public void Trim(long maxBytes, string? protectedKey = null) { }
    public (int Width, int Height) GetDimensions(string filePath) => (0, 0);
    public (int Width, int Height) GetDimensionsOrUnknown(string filePath) => (0, 0);
    public byte[]? DecodeForHashInput(string filePath, int targetSize = 256) => null;
    public Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase));
}

internal sealed class TestFaultInjectingFileSystem : IFileSystemOperations
{
    private readonly string? _failingMoveSourcePattern;

    public bool FailNextMove { get; set; }

    public TestFaultInjectingFileSystem(string? failingMoveSourcePattern = null)
    {
        _failingMoveSourcePattern = failingMoveSourcePattern;
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);

    public void MoveFile(string source, string destination)
    {
        if (FailNextMove)
        {
            FailNextMove = false;
            throw new IOException($"Simulated filesystem move failure from {source} to {destination}");
        }

        if (_failingMoveSourcePattern != null && source.Contains(_failingMoveSourcePattern, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Simulated filesystem move failure on {source}");
        }

        File.Move(source, destination);
    }

    public void MoveDirectory(string source, string destination)
    {
        Directory.Move(source, destination);
    }
}
