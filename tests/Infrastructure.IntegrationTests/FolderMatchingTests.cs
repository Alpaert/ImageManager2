using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class FolderMatchingTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;
    private readonly FolderRepository _folderRepo;
    private readonly ImageEmbeddingRepository _embeddingRepo;

    public FolderMatchingTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-FolderMatchingTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _metaRepo = new ImageMetaRepository(_dbFactory);
        _folderRepo = new FolderRepository(_dbFactory);
        var suppressionRepo = new CharacterTagSuppressionRepository(_dbFactory);
        _embeddingRepo = new ImageEmbeddingRepository(_dbFactory, suppressionRepo);
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

    private async Task<long> InsertImageAsync(string filePath, int hashStatus = 0, long folderId = 0)
    {
        var meta = new ImageMeta
        {
            FilePath = filePath,
            FileHash = Guid.NewGuid().ToString("N"),
            FileSize = 1024,
            Width = 800,
            Height = 600,
            LastWriteTicks = DateTime.UtcNow.Ticks,
            FolderId = folderId,
            HashStatus = hashStatus
        };
        return await _metaRepo.UpsertAsync(meta);
    }

    [Fact]
    public async Task UnderscoreMatching_WhenQueryingA_B_DoesNotAffectOrReturnAXB()
    {
        // 1. 同时存在 a_b、axb：操作 a_b 时，axb 的记录完全不变
        var dirAB = Path.Combine(_testRoot, "a_b");
        var dirAXB = Path.Combine(_testRoot, "axb");
        Directory.CreateDirectory(dirAB);
        Directory.CreateDirectory(dirAXB);

        var fileAB1 = Path.Combine(dirAB, "photo1.jpg");
        var fileAB2 = Path.Combine(dirAB, "photo2.jpg");
        var fileAXB1 = Path.Combine(dirAXB, "photo1.jpg");
        var fileAXB2 = Path.Combine(dirAXB, "photo2.jpg");

        var idAB1 = await InsertImageAsync(fileAB1, hashStatus: -1);
        var idAB2 = await InsertImageAsync(fileAB2, hashStatus: -1);
        var idAXB1 = await InsertImageAsync(fileAXB1, hashStatus: -1);
        var idAXB2 = await InsertImageAsync(fileAXB2, hashStatus: -1);

        // Add manual tags and auto tags
        await _metaRepo.SetTagsAsync(idAB1, ["portrait"]);
        await _metaRepo.AddAutoTagsAsync(idAB1, ["cat", "cute"]);
        await _metaRepo.SetTagsAsync(idAXB1, ["landscape"]);
        await _metaRepo.AddAutoTagsAsync(idAXB1, ["dog", "wild"]);

        // Query a_b
        var resultAB = await _metaRepo.GetByFolderAsync(dirAB, recursive: true);
        var pathsAB = resultAB.Select(m => m.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(pathsAB.SetEquals([fileAB1, fileAB2]));
        Assert.DoesNotContain(fileAXB1, pathsAB);
        Assert.DoesNotContain(fileAXB2, pathsAB);

        // Reset failed hash on a_b only
        var resetPaths = await _metaRepo.ResetFailedHashStatusByFolderAsync(dirAB, recursive: true);
        Assert.True(resetPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([fileAB1, fileAB2]));

        // Verify axb still has hashStatus = -1
        var metaAXB1 = await _metaRepo.GetByIdAsync(idAXB1);
        var metaAXB2 = await _metaRepo.GetByIdAsync(idAXB2);
        Assert.NotNull(metaAXB1);
        Assert.NotNull(metaAXB2);
        Assert.Equal(-1, metaAXB1.HashStatus);
        Assert.Equal(-1, metaAXB2.HashStatus);

        // Clear auto tags on a_b only
        await _metaRepo.DeleteAllAutoTagsByFolderAsync(dirAB, recursive: true);
        var tagsAXB1 = await _metaRepo.GetByIdAsync(idAXB1);
        Assert.NotNull(tagsAXB1);
        var tagNamesAXB = tagsAXB1.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dog", tagNamesAXB);
        Assert.Contains("wild", tagNamesAXB);
        Assert.Contains("landscape", tagNamesAXB);

        // Delete a_b
        await _metaRepo.DeleteByFolderAsync(dirAB, recursive: true);
        var remainingAXB = await _metaRepo.GetByFolderAsync(dirAXB, recursive: true);
        Assert.Equal(2, remainingAXB.Count);
    }

    [Fact]
    public async Task PercentSignMatching_WhenQuerying100PercentDone_DoesNotAffectDecoyFolder()
    {
        // 2. 同时存在 100%完成、100其他完成：% 按目录名中的普通字符处理
        var dirPercent = Path.Combine(_testRoot, "100%完成");
        var dirOther = Path.Combine(_testRoot, "100其他完成");
        Directory.CreateDirectory(dirPercent);
        Directory.CreateDirectory(dirOther);

        var filePercent1 = Path.Combine(dirPercent, "pic1.jpg");
        var filePercent2 = Path.Combine(dirPercent, "sub", "pic2.jpg");
        var fileOther1 = Path.Combine(dirOther, "pic1.jpg");
        var fileOther2 = Path.Combine(dirOther, "sub", "pic2.jpg");

        var idPercent1 = await InsertImageAsync(filePercent1, hashStatus: -1);
        var idPercent2 = await InsertImageAsync(filePercent2, hashStatus: -1);
        var idOther1 = await InsertImageAsync(fileOther1, hashStatus: -1);
        var idOther2 = await InsertImageAsync(fileOther2, hashStatus: -1);

        // Query recursive
        var resPercent = await _metaRepo.GetByFolderAsync(dirPercent, recursive: true);
        var pathsPercent = resPercent.Select(m => m.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(pathsPercent.SetEquals([filePercent1, filePercent2]));
        Assert.DoesNotContain(fileOther1, pathsPercent);
        Assert.DoesNotContain(fileOther2, pathsPercent);

        // Delete by folder on 100%完成
        await _metaRepo.DeleteByFolderAsync(dirPercent, recursive: true);

        // Check 100%完成 is gone, but 100其他完成 is untouched
        var afterPercent = await _metaRepo.GetByFolderAsync(dirPercent, recursive: true);
        Assert.Empty(afterPercent);

        var afterOther = await _metaRepo.GetByFolderAsync(dirOther, recursive: true);
        Assert.Equal(2, afterOther.Count);
        var otherPaths = afterOther.Select(m => m.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(otherPaths.SetEquals([fileOther1, fileOther2]));
    }

    [Fact]
    public async Task PrefixSiblingMatching_OperatingOnA_DoesNotAffectAB()
    {
        // 3. 同时存在 A、AB：操作 A 不影响 AB
        var dirA = Path.Combine(_testRoot, "A");
        var dirAB = Path.Combine(_testRoot, "AB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirAB);

        var fileA = Path.Combine(dirA, "img.jpg");
        var fileAB = Path.Combine(dirAB, "img.jpg");

        var idA = await InsertImageAsync(fileA, hashStatus: -1);
        var idAB = await InsertImageAsync(fileAB, hashStatus: -1);

        await _metaRepo.AddAutoTagsAsync(idA, ["tagA"]);
        await _metaRepo.AddAutoTagsAsync(idAB, ["tagAB"]);

        // Query A
        var resultA = await _metaRepo.GetByFolderAsync(dirA, recursive: true);
        Assert.Single(resultA);
        Assert.Equal(fileA, resultA[0].FilePath, ignoreCase: true);

        // Clear auto tags in A
        await _metaRepo.DeleteAllAutoTagsByFolderAsync(dirA, recursive: true);

        // Verify AB tag still intact
        var metaAB = await _metaRepo.GetByIdAsync(idAB);
        Assert.NotNull(metaAB);
        Assert.Contains(metaAB.Tags, t => string.Equals(t.Name, "tagAB", StringComparison.OrdinalIgnoreCase));

        // Delete A
        await _metaRepo.DeleteByFolderAsync(dirA, recursive: true);

        // AB still exists
        var resultAB = await _metaRepo.GetByFolderAsync(dirAB, recursive: true);
        Assert.Single(resultAB);
        Assert.Equal(fileAB, resultAB[0].FilePath, ignoreCase: true);
    }

    [Fact]
    public async Task SpecialCharacters_ChineseSpacesSingleQuotes_QueryAndMutateCorrectly()
    {
        // 4. 目录包含中文、空格、单引号：查询和修改正常，参数始终通过绑定传入
        var specialDir = Path.Combine(_testRoot, "相册 'special 2026' 目录");
        Directory.CreateDirectory(specialDir);

        var file1 = Path.Combine(specialDir, "photo 1.jpg");
        var file2 = Path.Combine(specialDir, "photo 2.jpg");

        var id1 = await InsertImageAsync(file1, hashStatus: -1);
        var id2 = await InsertImageAsync(file2, hashStatus: -1);

        await _metaRepo.SetTagsAsync(id1, ["手工 'tag'"]);
        await _metaRepo.AddAutoTagsAsync(id1, ["风景 '1'"]);

        // Query by folder
        var items = await _metaRepo.GetByFolderAsync(specialDir, recursive: true);
        Assert.Equal(2, items.Count);
        var paths = items.Select(m => m.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(paths.SetEquals([file1, file2]));

        // Tag map
        var tagMap = await _metaRepo.GetTagMapByFolderAsync(specialDir, recursive: true);
        Assert.True(tagMap.ContainsKey(file1));
        Assert.Contains("风景 '1'", tagMap[file1]);
        Assert.Contains("手工 'tag'", tagMap[file1]);

        // Reset failed hash
        var reset = await _metaRepo.ResetFailedHashStatusByFolderAsync(specialDir, recursive: true);
        Assert.Equal(2, reset.Count);

        var m1 = await _metaRepo.GetByIdAsync(id1);
        Assert.NotNull(m1);
        Assert.Equal(0, m1.HashStatus);

        // Clear auto tags
        await _metaRepo.DeleteAllAutoTagsByFolderAsync(specialDir, recursive: true);
        var m1After = await _metaRepo.GetByIdAsync(id1);
        Assert.NotNull(m1After);
        Assert.DoesNotContain(m1After.Tags, t => t.Name == "风景 '1'");
        Assert.Contains(m1After.Tags, t => t.Name == "手工 'tag'");
    }

    [Fact]
    public async Task HierarchyAndRecursion_DirectVsRecursive_HonorsContracts()
    {
        // 5. 有子目录及更深层文件：非递归与递归结果分别符合约定
        var rootDir = Path.Combine(_testRoot, "Hierarchy");
        var subDir = Path.Combine(rootDir, "Sub");
        var deepDir = Path.Combine(subDir, "Deep");
        Directory.CreateDirectory(deepDir);

        var directFile = Path.Combine(rootDir, "direct.jpg");
        var subFile = Path.Combine(subDir, "sub.jpg");
        var deepFile = Path.Combine(deepDir, "deep.jpg");

        var idDirect = await InsertImageAsync(directFile, hashStatus: -1);
        var idSub = await InsertImageAsync(subFile, hashStatus: -1);
        var idDeep = await InsertImageAsync(deepFile, hashStatus: -1);

        await _metaRepo.AddAutoTagsAsync(idDirect, ["directTag"]);
        await _metaRepo.AddAutoTagsAsync(idSub, ["subTag"]);
        await _metaRepo.AddAutoTagsAsync(idDeep, ["deepTag"]);

        // Non-recursive query: direct only
        var directResults = await _metaRepo.GetByFolderAsync(rootDir, recursive: false);
        Assert.Single(directResults);
        Assert.Equal(directFile, directResults[0].FilePath, ignoreCase: true);

        // Recursive query: all 3
        var recursiveResults = await _metaRepo.GetByFolderAsync(rootDir, recursive: true);
        Assert.Equal(3, recursiveResults.Count);
        var recPaths = recursiveResults.Select(r => r.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(recPaths.SetEquals([directFile, subFile, deepFile]));

        // Non-recursive tag map
        var directTagMap = await _metaRepo.GetTagMapByFolderAsync(rootDir, recursive: false);
        Assert.Single(directTagMap);
        Assert.True(directTagMap.ContainsKey(directFile));

        // Non-recursive clear auto tags: only directFile auto tags are removed
        await _metaRepo.DeleteAllAutoTagsByFolderAsync(rootDir, recursive: false);
        var mDirect = await _metaRepo.GetByIdAsync(idDirect);
        var mSub = await _metaRepo.GetByIdAsync(idSub);
        var mDeep = await _metaRepo.GetByIdAsync(idDeep);
        Assert.NotNull(mDirect);
        Assert.NotNull(mSub);
        Assert.NotNull(mDeep);
        Assert.Empty(mDirect.Tags);
        Assert.Contains(mSub.Tags, t => t.Name == "subTag");
        Assert.Contains(mDeep.Tags, t => t.Name == "deepTag");

        // Non-recursive reset hash status: only directFile is reset
        var resetDirect = await _metaRepo.ResetFailedHashStatusByFolderAsync(rootDir, recursive: false);
        Assert.Single(resetDirect);
        Assert.Equal(directFile, resetDirect[0], ignoreCase: true);
        mDirect = await _metaRepo.GetByIdAsync(idDirect);
        mSub = await _metaRepo.GetByIdAsync(idSub);
        Assert.NotNull(mDirect);
        Assert.NotNull(mSub);
        Assert.Equal(0, mDirect.HashStatus);
        Assert.Equal(-1, mSub.HashStatus);

        // Non-recursive delete: only directFile is deleted
        await _metaRepo.DeleteByFolderAsync(rootDir, recursive: false);
        var afterDirectDelete = await _metaRepo.GetByFolderAsync(rootDir, recursive: true);
        Assert.Equal(2, afterDirectDelete.Count);
        Assert.DoesNotContain(afterDirectDelete, m => string.Equals(m.FilePath, directFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TrailingSeparator_WithOrWithout_YieldsIdenticalScope()
    {
        // 6. 输入带或不带尾部分隔符：得到相同的目录范围
        var folder = Path.Combine(_testRoot, "SeparatorFolder");
        Directory.CreateDirectory(folder);

        var file = Path.Combine(folder, "sample.jpg");
        await InsertImageAsync(file);

        var folderWithoutTrailing = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderWithTrailing = folderWithoutTrailing + Path.DirectorySeparatorChar;

        var res1 = await _metaRepo.GetByFolderAsync(folderWithoutTrailing, recursive: true);
        var res2 = await _metaRepo.GetByFolderAsync(folderWithTrailing, recursive: true);

        Assert.Single(res1);
        Assert.Single(res2);
        Assert.Equal(res1[0].FilePath, res2[0].FilePath, ignoreCase: true);

        var map1 = await _metaRepo.GetTagMapByFolderAsync(folderWithoutTrailing, recursive: true);
        var map2 = await _metaRepo.GetTagMapByFolderAsync(folderWithTrailing, recursive: true);
        Assert.Equal(map1.Count, map2.Count);
    }

    [Fact]
    public async Task ClearAutoTags_DeletesAutoOnly_PreservesManualTags()
    {
        // 7. 清空自动标签：自动标签删除，手工标签保留，其他目录不变
        var targetDir = Path.Combine(_testRoot, "ClearTarget");
        var otherDir = Path.Combine(_testRoot, "ClearOther");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(otherDir);

        var targetFile = Path.Combine(targetDir, "pic.jpg");
        var otherFile = Path.Combine(otherDir, "pic.jpg");

        var idTarget = await InsertImageAsync(targetFile);
        var idOther = await InsertImageAsync(otherFile);

        // Add manual tags and auto tags to target
        await _metaRepo.SetTagsAsync(idTarget, ["manual_1", "manual_2"]);
        await _metaRepo.AddAutoTagsAsync(idTarget, ["auto_1", "auto_2"]);

        // Add manual tags and auto tags to other
        await _metaRepo.SetTagsAsync(idOther, ["manual_other"]);
        await _metaRepo.AddAutoTagsAsync(idOther, ["auto_1"]);

        // Delete auto tags for target folder
        var affected = await _metaRepo.DeleteAllAutoTagsByFolderAsync(targetDir, recursive: true);
        Assert.Equal(2, affected);

        // Check target: auto tags deleted, manual tags preserved
        var metaTarget = await _metaRepo.GetByIdAsync(idTarget);
        Assert.NotNull(metaTarget);
        var targetTags = metaTarget.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto_1", targetTags);
        Assert.DoesNotContain("auto_2", targetTags);
        Assert.Contains("manual_1", targetTags);
        Assert.Contains("manual_2", targetTags);

        // Check other: untouched
        var metaOther = await _metaRepo.GetByIdAsync(idOther);
        Assert.NotNull(metaOther);
        var otherTags = metaOther.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("auto_1", otherTags);
        Assert.Contains("manual_other", otherTags);
    }

    [Fact]
    public async Task ResetHashStatus_UpdatesOnlyMatchingFailedRecords()
    {
        // 8. 重置哈希状态：仅更新指定范围内符合条件的记录
        var targetDir = Path.Combine(_testRoot, "HashTarget");
        var otherDir = Path.Combine(_testRoot, "HashOther");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(otherDir);

        var targetFailed = Path.Combine(targetDir, "failed.jpg");
        var targetSuccess = Path.Combine(targetDir, "success.jpg");
        var otherFailed = Path.Combine(otherDir, "failed.jpg");

        var idTargetFailed = await InsertImageAsync(targetFailed, hashStatus: -1);
        var idTargetSuccess = await InsertImageAsync(targetSuccess, hashStatus: 1);
        var idOtherFailed = await InsertImageAsync(otherFailed, hashStatus: -1);

        var reset = await _metaRepo.ResetFailedHashStatusByFolderAsync(targetDir, recursive: true);
        Assert.Single(reset);
        Assert.Equal(targetFailed, reset[0], ignoreCase: true);

        // Assert targetFailed is now 0
        var m1 = await _metaRepo.GetByIdAsync(idTargetFailed);
        Assert.NotNull(m1);
        Assert.Equal(0, m1.HashStatus);

        // Assert targetSuccess is STILL 1
        var m2 = await _metaRepo.GetByIdAsync(idTargetSuccess);
        Assert.NotNull(m2);
        Assert.Equal(1, m2.HashStatus);

        // Assert otherFailed is STILL -1
        var m3 = await _metaRepo.GetByIdAsync(idOtherFailed);
        Assert.NotNull(m3);
        Assert.Equal(-1, m3.HashStatus);
    }

    [Fact]
    public async Task RelocateFolder_ReplacesPrefixAndPreservesRelativePath_HandlesWildcards()
    {
        // 9. 修改目录路径：只替换真实目录前缀，保留后面的相对路径，支持特殊字符
        var oldFolder = Path.Combine(_testRoot, "Old_100%_Dir");
        var newFolder = Path.Combine(_testRoot, "New_Folder");
        var decoyFolder = Path.Combine(_testRoot, "Old_100ABC_Dir");
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        Directory.CreateDirectory(decoyFolder);

        // Insert Folder record
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO Folder (Id, Path) VALUES (@Id, @Path)",
                new { Id = 100L, Path = oldFolder });
            await conn.ExecuteAsync("INSERT INTO Folder (Id, Path) VALUES (@Id, @Path)",
                new { Id = 200L, Path = decoyFolder });
        }

        var oldDirect = Path.Combine(oldFolder, "file1.jpg");
        var oldNested = Path.Combine(oldFolder, "sub", "file2.jpg");
        var decoyFile = Path.Combine(decoyFolder, "file1.jpg");

        var id1 = await InsertImageAsync(oldDirect, folderId: 100L);
        var id2 = await InsertImageAsync(oldNested, folderId: 100L);
        var idDecoy = await InsertImageAsync(decoyFile, folderId: 200L);

        // Relocate
        await _folderRepo.RelocateFolderAsync(100L, newFolder);

        // Verify ImageMeta records
        var m1 = await _metaRepo.GetByIdAsync(id1);
        var m2 = await _metaRepo.GetByIdAsync(id2);
        var mDecoy = await _metaRepo.GetByIdAsync(idDecoy);

        Assert.NotNull(m1);
        Assert.NotNull(m2);
        Assert.NotNull(mDecoy);

        var expected1 = Path.Combine(newFolder, "file1.jpg");
        var expected2 = Path.Combine(newFolder, "sub", "file2.jpg");

        Assert.Equal(expected1, m1.FilePath, ignoreCase: true);
        Assert.Equal(expected2, m2.FilePath, ignoreCase: true);
        Assert.Equal(decoyFile, mDecoy.FilePath, ignoreCase: true);
    }

    [Fact]
    public async Task EmptyOrNonexistentFolder_ReturnsZeroResults_DoesNotExpandScope()
    {
        // 10. 空目录、不存在的目录：返回空结果或影响零行，不扩大查询范围
        var emptyFolder = Path.Combine(_testRoot, "EmptyDir");
        var nonExistentFolder = Path.Combine(_testRoot, "DoesNotExist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyFolder);

        // Also add an existing file in another directory to ensure it is NOT returned
        var existingDir = Path.Combine(_testRoot, "Existing");
        Directory.CreateDirectory(existingDir);
        await InsertImageAsync(Path.Combine(existingDir, "pic.jpg"));

        // Query empty folder
        var emptyRes = await _metaRepo.GetByFolderAsync(emptyFolder, recursive: true);
        Assert.Empty(emptyRes);

        var emptyMap = await _metaRepo.GetTagMapByFolderAsync(emptyFolder, recursive: true);
        Assert.Empty(emptyMap);

        var emptyReset = await _metaRepo.ResetFailedHashStatusByFolderAsync(emptyFolder, recursive: true);
        Assert.Empty(emptyReset);

        var emptyDeleted = await _metaRepo.DeleteByFolderAsync(emptyFolder, recursive: true);
        Assert.Equal(0, emptyDeleted);

        var emptyTagsCleared = await _metaRepo.DeleteAllAutoTagsByFolderAsync(emptyFolder, recursive: true);
        Assert.Equal(0, emptyTagsCleared);

        // Query non-existent folder
        var nonExistentRes = await _metaRepo.GetByFolderAsync(nonExistentFolder, recursive: true);
        Assert.Empty(nonExistentRes);

        var nonExistentMap = await _metaRepo.GetTagMapByFolderAsync(nonExistentFolder, recursive: true);
        Assert.Empty(nonExistentMap);

        var nonExistentDeleted = await _metaRepo.DeleteByFolderAsync(nonExistentFolder, recursive: true);
        Assert.Equal(0, nonExistentDeleted);
    }

    [Fact]
    public async Task EmbeddingRepository_GetSearchIndexCandidates_RespectsRecursionAndWildcards()
    {
        var targetDir = Path.Combine(_testRoot, "Emb_%_Target");
        var subDir = Path.Combine(targetDir, "Sub");
        var decoyDir = Path.Combine(_testRoot, "Emb_XYZ_Target");
        Directory.CreateDirectory(subDir);
        Directory.CreateDirectory(decoyDir);

        var directFile = Path.Combine(targetDir, "direct.jpg");
        var subFile = Path.Combine(subDir, "sub.jpg");
        var decoyFile = Path.Combine(decoyDir, "decoy.jpg");

        await InsertImageAsync(directFile);
        await InsertImageAsync(subFile);
        await InsertImageAsync(decoyFile);

        // Recursive candidates
        var recScope = new VectorIndexScope(targetDir, IncludeSubfolders: true);
        var recCandidates = await _embeddingRepo.GetSearchIndexCandidatesAsync(recScope);
        var recPaths = recCandidates.Select(c => c.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(recPaths.SetEquals([directFile, subFile]));

        // Direct candidates only
        var directScope = new VectorIndexScope(targetDir, IncludeSubfolders: false);
        var directCandidates = await _embeddingRepo.GetSearchIndexCandidatesAsync(directScope);
        Assert.Single(directCandidates);
        Assert.Equal(directFile, directCandidates[0].FilePath, ignoreCase: true);
    }

    [Fact]
    public async Task EmbeddingRepository_GetByFolderPrefix_EscapesWildcards()
    {
        var targetDir = Path.Combine(_testRoot, "Emb_%_Prefix");
        var decoyDir = Path.Combine(_testRoot, "Emb_ABC_Prefix");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(decoyDir);

        var targetFile = Path.Combine(targetDir, "image.jpg");
        var decoyFile = Path.Combine(decoyDir, "image.jpg");

        var targetId = await InsertImageAsync(targetFile);
        var decoyId = await InsertImageAsync(decoyFile);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        await _embeddingRepo.UpsertBatchAsync([
            (targetId, "hash1", embedding),
            (decoyId, "hash2", embedding)
        ], "testModel", "v1");

        var records = await _embeddingRepo.GetByFolderPrefixAsync(targetDir, "testModel", "v1");
        Assert.Single(records);
        Assert.Equal(targetFile, records[0].FilePath, ignoreCase: true);
    }
}
