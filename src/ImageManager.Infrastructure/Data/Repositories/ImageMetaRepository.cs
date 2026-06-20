using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data.Repositories;

public class ImageMetaRepository : IImageMetaRepository
{
    private readonly IDbContextFactory _dbFactory;

    public ImageMetaRepository(IDbContextFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<ImageMeta?> GetByIdAsync(long id)
    {
        using var conn = _dbFactory.CreateConnection();
        var meta = await conn.QuerySingleOrDefaultAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE Id = @Id", new { Id = id });
        if (meta != null)
            meta.Tags = await GetTagsForMetaAsync(conn, meta.Id);
        return meta;
    }

    public async Task<ImageMeta?> GetByPathAsync(string filePath)
    {
        using var conn = _dbFactory.CreateConnection();
        var meta = await conn.QuerySingleOrDefaultAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FilePath = filePath });
        if (meta != null)
            meta.Tags = await GetTagsForMetaAsync(conn, meta.Id);
        return meta;
    }

    public async Task<List<ImageMeta>> GetByFolderAsync(string folderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        var metas = (await conn.QueryAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" })).ToList();

        var metaIds = metas.Select(m => m.Id).ToList();
        var allTags = await GetTagMapAsync(conn, metaIds);
        foreach (var meta in metas)
        {
            if (allTags.TryGetValue(meta.Id, out var tags))
                meta.Tags = tags;
            else
                meta.Tags = new List<TagCount>();
        }

        return metas;
    }

    public async Task<List<ImageMeta>> GetByFolderIdAsync(long folderId)
    {
        using var conn = _dbFactory.CreateConnection();
        var metas = (await conn.QueryAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FolderId = @FolderId",
            new { FolderId = folderId })).ToList();

        var metaIds = metas.Select(m => m.Id).ToList();
        var allTags = await GetTagMapAsync(conn, metaIds);
        foreach (var meta in metas)
        {
            if (allTags.TryGetValue(meta.Id, out var tags))
                meta.Tags = tags;
            else
                meta.Tags = new List<TagCount>();
        }

        return metas;
    }

    public async Task<int> CountByFolderIdAsync(long folderId)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ImageMeta WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task SetFolderIdAsync(string filePath, long folderId)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET FolderId = @FolderId WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FolderId = folderId, FilePath = filePath });
    }

    public async Task<int> UnlinkFolderIdAsync(long folderId)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE ImageMeta SET FolderId = 0 WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task<int> UnlinkOrphanFolderIdsAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteAsync(@"
            UPDATE ImageMeta
            SET FolderId = 0
            WHERE FolderId IS NOT NULL
              AND FolderId != 0
              AND NOT EXISTS (
                  SELECT 1 FROM Folder f WHERE f.Id = ImageMeta.FolderId
              )");
    }

    public async Task<List<ImageMeta>> GetAllAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        var metas = (await conn.QueryAsync<ImageMeta>("SELECT * FROM ImageMeta")).ToList();

        // Load tags for all metas (could be optimized but fine for typical usage)
        var allTags = await GetTagMapAsync(conn);
        foreach (var meta in metas)
        {
            if (allTags.TryGetValue(meta.Id, out var tags))
                meta.Tags = tags;
            else
                meta.Tags = new List<TagCount>();
        }

        return metas;
    }

    public async Task<long> UpsertAsync(ImageMeta meta)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        var existing = await conn.QuerySingleOrDefaultAsync<(long Id, long? FolderId, long? ExistingFolderId)>(@"
            SELECT im.Id, im.FolderId, f.Id AS ExistingFolderId
            FROM ImageMeta im
            LEFT JOIN Folder f ON f.Id = im.FolderId
            WHERE im.FilePath = @FilePath COLLATE NOCASE",
            new { meta.FilePath }, txn);

        if (existing != default)
        {
            meta.Id = existing.Id;
            meta.UpdatedAt = DateTime.UtcNow;
            // Preserve existing non-null FolderId (for subfolder files in "全展示" mode)
            if (existing.FolderId.HasValue && existing.FolderId.Value != 0 && existing.ExistingFolderId.HasValue)
                meta.FolderId = existing.FolderId.Value;
            await conn.ExecuteAsync(@"
                UPDATE ImageMeta SET
                    FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                    Width = @Width, Height = @Height,
                    FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                    FolderId = @FolderId, UpdatedAt = @UpdatedAt, HashStatus = @HashStatus
                WHERE Id = @Id", meta, txn);
        }
        else
        {
            meta.CreatedAt = DateTime.UtcNow;
            meta.UpdatedAt = DateTime.UtcNow;
            meta.Id = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO ImageMeta (FilePath, FileHash, PerceptualHash, Width, Height,
                    FileSize, LastWriteTicks, FolderId, CreatedAt, UpdatedAt, HashStatus)
                VALUES (@FilePath, @FileHash, @PerceptualHash, @Width, @Height,
                    @FileSize, @LastWriteTicks, @FolderId, @CreatedAt, @UpdatedAt, @HashStatus);
                SELECT last_insert_rowid();", meta, txn);
        }

        txn.Commit();
        return meta.Id;
    }

    public async Task BulkUpsertAsync(List<ImageMeta> metas)
    {
        if (metas.Count == 0) return;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var conn = _dbFactory.CreateConnection();
                using var txn = conn.BeginTransaction();

                // Single query to find existing paths. Preserve FolderId only if it still points at a Folder row.
                var paths = metas.Select(m => m.FilePath).Distinct().ToList();
                var existing = await conn.QueryAsync<(string FilePath, long Id, long? FolderId, long? ExistingFolderId)>(@"
                    SELECT im.FilePath, im.Id, im.FolderId, f.Id AS ExistingFolderId
                    FROM ImageMeta im
                    LEFT JOIN Folder f ON f.Id = im.FolderId
                    WHERE im.FilePath COLLATE NOCASE IN @Paths",
                    new { Paths = paths }, txn);

                var existingMap = new Dictionary<string, (long Id, long? FolderId, bool FolderExists)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (fp, id, folderId, existingFolderId) in existing)
                    existingMap[fp] = (id, folderId, existingFolderId.HasValue);

                var now = DateTime.UtcNow;
                foreach (var meta in metas)
                {
                    if (existingMap.TryGetValue(meta.FilePath, out var existingData))
                    {
                        meta.Id = existingData.Id;
                        meta.UpdatedAt = now;
                        // Preserve existing non-null FolderId (for subfolder files in "全展示" mode)
                        if (existingData.FolderId.HasValue && existingData.FolderId.Value != 0 && existingData.FolderExists)
                            meta.FolderId = existingData.FolderId.Value;
                        await conn.ExecuteAsync(@"
                            UPDATE ImageMeta SET
                                FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                                Width = @Width, Height = @Height,
                                FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                                FolderId = @FolderId, UpdatedAt = @UpdatedAt, HashStatus = @HashStatus
                            WHERE Id = @Id", meta, txn);
                    }
                    else
                    {
                        meta.CreatedAt = now;
                        meta.UpdatedAt = now;
                        meta.Id = await conn.ExecuteScalarAsync<long>(@"
                            INSERT INTO ImageMeta (FilePath, FileHash, PerceptualHash, Width, Height,
                                FileSize, LastWriteTicks, FolderId, CreatedAt, UpdatedAt, HashStatus)
                            VALUES (@FilePath, @FileHash, @PerceptualHash, @Width, @Height,
                                @FileSize, @LastWriteTicks, @FolderId, @CreatedAt, @UpdatedAt, @HashStatus);
                            SELECT last_insert_rowid();", meta, txn);
                    }
                }

                txn.Commit();
                return; // success
            }
            catch (SqliteException ex) when (ex.Message.Contains("busy") || ex.Message.Contains("locked"))
            {
                if (attempt >= 3) throw;
                AppLogger.Warn($"BulkUpsert 失败 ({attempt}/3): {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)));
            }
        }
    }

    public async Task<int> DeleteAsync(long id)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteAsync("DELETE FROM ImageMeta WHERE Id = @Id", new { Id = id });
    }

    public async Task<int> DeleteByPathAsync(string filePath)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FilePath = filePath });
    }

    public async Task<int> DeleteByFolderAsync(string folderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        return await conn.ExecuteAsync(
            "DELETE FROM ImageMeta WHERE FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" });
    }

    public async Task SetTagsAsync(long imageId, List<string> tags)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        // Clear existing tags
        await conn.ExecuteAsync("DELETE FROM ImageTag WHERE ImageMetaId = @ImageId",
            new { ImageId = imageId }, txn);

        // Insert new tags
        foreach (var tagName in tags)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var trimmed = tagName.Trim();

            var tagId = await conn.ExecuteScalarAsync<long?>(@"
                INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
                SELECT Id FROM Tag WHERE Name = @Name;",
                new { Name = trimmed }, txn);

            if (tagId.HasValue)
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId) VALUES (@ImageId, @TagId)",
                    new { ImageId = imageId, TagId = tagId.Value }, txn);
            }
        }

        txn.Commit();
    }

    public async Task AddAutoTagsAsync(long imageId, List<string> tagNames)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        foreach (var tagName in tagNames)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var trimmed = tagName.Trim();

            var tagId = await conn.ExecuteScalarAsync<long?>(@"
                INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
                SELECT Id FROM Tag WHERE Name = @Name;",
                new { Name = trimmed }, txn);

            if (tagId.HasValue)
            {
                await conn.ExecuteAsync(@"
                    INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId, Source)
                    VALUES (@ImageId, @TagId, 'AutoTag')",
                    new { ImageId = imageId, TagId = tagId.Value }, txn);
            }
        }

        txn.Commit();
    }

    public async Task ReplaceAutoTagAsync(long imageId, string englishTagName, long chineseTagId)
    {
        using var conn = _dbFactory.CreateConnection();
        // Step 1: Upsert Chinese tag — UPDATE handles same-name, INSERT handles different-name
        await conn.ExecuteAsync(@"
            UPDATE ImageTag SET Source = 'AutoTagConfirmed'
            WHERE ImageMetaId = @ImageId AND TagId = @ChineseId",
            new { ImageId = imageId, ChineseId = chineseTagId });

        await conn.ExecuteAsync(@"
            INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId, Source)
            VALUES (@ImageId, @ChineseId, 'AutoTagConfirmed')",
            new { ImageId = imageId, ChineseId = chineseTagId });

        // Step 2: Remove old English AutoTag (skip if same TagId as Chinese to avoid deleting the upsert)
        await conn.ExecuteAsync(@"
            DELETE FROM ImageTag
            WHERE ImageMetaId = @ImageId
              AND TagId IN (SELECT Id FROM Tag WHERE Name = @EnglishName COLLATE NOCASE)
              AND Source = 'AutoTag'
              AND TagId != @ChineseId",
            new { ImageId = imageId, EnglishName = englishTagName.Trim(), ChineseId = chineseTagId });
    }

    public async Task ReplaceAutoTagsBatchAsync(List<(long ImageId, string EnglishName, long ChineseId)> replacements)
    {
        if (replacements.Count == 0) return;
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var (imageId, englishName, chineseId) in replacements)
        {
            var name = englishName.Trim();
            await conn.ExecuteAsync(@"
                UPDATE ImageTag SET Source = 'AutoTagConfirmed'
                WHERE ImageMetaId = @ImageId AND TagId = @ChineseId",
                new { ImageId = imageId, ChineseId = chineseId }, txn);

            await conn.ExecuteAsync(@"
                INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId, Source)
                VALUES (@ImageId, @ChineseId, 'AutoTagConfirmed')",
                new { ImageId = imageId, ChineseId = chineseId }, txn);

            await conn.ExecuteAsync(@"
                DELETE FROM ImageTag
                WHERE ImageMetaId = @ImageId
                  AND TagId IN (SELECT Id FROM Tag WHERE Name = @EnglishName COLLATE NOCASE)
                  AND Source = 'AutoTag'
                  AND TagId != @ChineseId",
                new { ImageId = imageId, EnglishName = name, ChineseId = chineseId }, txn);
        }
        txn.Commit();
    }

    public async Task DeleteAutoTagFromImageAsync(long imageId, string tagName)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            DELETE FROM ImageTag
            WHERE ImageMetaId = @ImageId
              AND TagId IN (SELECT Id FROM Tag WHERE Name = @TagName COLLATE NOCASE)
              AND Source = 'AutoTag'",
            new { ImageId = imageId, TagName = tagName.Trim() });
    }

    /// <summary>删除文件夹下所有自动标签（Source IN ('AutoTag','AutoTagConfirmed')），一条 SQL，批量高效</summary>
    public async Task<int> DeleteAllAutoTagsByFolderAsync(string folderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        return await conn.ExecuteAsync(@"
            DELETE FROM ImageTag
            WHERE ImageMetaId IN (SELECT Id FROM ImageMeta WHERE FilePath LIKE @Prefix)
              AND Source IN ('AutoTag', 'AutoTagConfirmed')",
            new { Prefix = normalized + "%" });
    }

    public async Task<List<TagCount>> GetTagCountsAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        var results = await conn.QueryAsync<TagCount>(@"
            SELECT t.Name, COUNT(it.ImageMetaId) as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            GROUP BY t.Id, t.Name
            ORDER BY Count DESC, t.Name");
        return results.ToList();
    }

    public async Task<Dictionary<string, List<string>>> GetTagMapByFolderAsync(string folderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        var rows = await conn.QueryAsync<(string FilePath, string TagName)>(@"
            SELECT im.FilePath, t.Name
            FROM ImageMeta im
            LEFT JOIN ImageTag it ON im.Id = it.ImageMetaId
            LEFT JOIN Tag t ON it.TagId = t.Id
            WHERE im.FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" });

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, tagName) in rows)
        {
            if (!result.TryGetValue(filePath, out var tags))
            {
                tags = new List<string>();
                result[filePath] = tags;
            }
            if (tagName != null)
                tags.Add(tagName);
        }
        return result;
    }

    public async Task<Dictionary<string, List<string>>> GetTagMapByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        // Process in chunks to avoid overly large IN clauses
        foreach (var chunk in filePaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, string TagName)>(@"
                SELECT im.FilePath, t.Name
                FROM ImageMeta im
                LEFT JOIN ImageTag it ON im.Id = it.ImageMetaId
                LEFT JOIN Tag t ON it.TagId = t.Id
                WHERE im.FilePath IN @Paths",
                new { Paths = chunk });

            foreach (var (filePath, tagName) in rows)
            {
                if (!result.TryGetValue(filePath, out var tags))
                {
                    tags = new List<string>();
                    result[filePath] = tags;
                }
                if (tagName != null)
                    tags.Add(tagName);
            }
        }
        return result;
    }

    public async Task<List<string>> GetFilePathsByTagAsync(string tagName)
    {
        using var conn = _dbFactory.CreateConnection();
        var results = await conn.QueryAsync<string>(@"
            SELECT DISTINCT im.FilePath
            FROM ImageMeta im
            INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
            INNER JOIN Tag t ON it.TagId = t.Id
            WHERE t.Name = @TagName COLLATE NOCASE",
            new { TagName = tagName });
        return results.ToList();
    }

    public async Task<List<string>> GetFilePathsByTagsAsync(List<string> tagNames, bool requireAll)
    {
        using var conn = _dbFactory.CreateConnection();
        if (requireAll)
        {
            // AND: files that have ALL specified tags
            var results = await conn.QueryAsync<string>(@"
                SELECT im.FilePath
                FROM ImageMeta im
                INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                INNER JOIN Tag t ON it.TagId = t.Id
                WHERE t.Name IN @TagNames COLLATE NOCASE
                GROUP BY im.FilePath
                HAVING COUNT(DISTINCT t.Name) = @TagCount",
                new { TagNames = tagNames, TagCount = tagNames.Count });
            return results.ToList();
        }
        else
        {
            // OR: files that have ANY of the specified tags
            var results = await conn.QueryAsync<string>(@"
                SELECT DISTINCT im.FilePath
                FROM ImageMeta im
                INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                INNER JOIN Tag t ON it.TagId = t.Id
                WHERE t.Name IN @TagNames COLLATE NOCASE",
                new { TagNames = tagNames });
            return results.ToList();
        }
    }

    public async Task<List<string>> GetFilePathsByTagsExcludingAsync(
        List<string> includeTags, bool requireAll, List<string> excludeTags)
    {
        using var conn = _dbFactory.CreateConnection();
        // NOT IN subquery to exclude files with any of the exclude tags
        if (requireAll)
        {
            var results = await conn.QueryAsync<string>(@"
                SELECT im.FilePath
                FROM ImageMeta im
                INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                INNER JOIN Tag t ON it.TagId = t.Id
                WHERE t.Name IN @IncludeTags COLLATE NOCASE
                  AND im.FilePath NOT IN (
                    SELECT DISTINCT im2.FilePath
                    FROM ImageMeta im2
                    INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                    INNER JOIN Tag t2 ON it2.TagId = t2.Id
                    WHERE t2.Name IN @ExcludeTags COLLATE NOCASE
                  )
                GROUP BY im.FilePath
                HAVING COUNT(DISTINCT t.Name) = @IncludeCount",
                new { IncludeTags = includeTags, ExcludeTags = excludeTags, IncludeCount = includeTags.Count });
            return results.ToList();
        }
        else
        {
            var results = await conn.QueryAsync<string>(@"
                SELECT DISTINCT im.FilePath
                FROM ImageMeta im
                INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                INNER JOIN Tag t ON it.TagId = t.Id
                WHERE t.Name IN @IncludeTags COLLATE NOCASE
                  AND im.FilePath NOT IN (
                    SELECT DISTINCT im2.FilePath
                    FROM ImageMeta im2
                    INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                    INNER JOIN Tag t2 ON it2.TagId = t2.Id
                    WHERE t2.Name IN @ExcludeTags COLLATE NOCASE
                  )",
                new { IncludeTags = includeTags, ExcludeTags = excludeTags });
            return results.ToList();
        }
    }

    public async Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null)
    {
        using var conn = _dbFactory.CreateConnection();
        // baseTags: match files via AND or OR. eachTags: at least one required.
        // AND: GROUP BY + HAVING COUNT = baseCount. OR: just DISTINCT.
        var sql = @"
            SELECT im.FilePath
            FROM ImageMeta im
            INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
            INNER JOIN Tag t ON it.TagId = t.Id
            WHERE t.Name IN @BaseTags COLLATE NOCASE
              AND im.FilePath IN (
                SELECT DISTINCT im2.FilePath
                FROM ImageMeta im2
                INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                INNER JOIN Tag t2 ON it2.TagId = t2.Id
                WHERE t2.Name IN @EachTags COLLATE NOCASE
              )";

        if (excludeTags is { Count: > 0 })
        {
            sql += @"
              AND im.FilePath NOT IN (
                SELECT DISTINCT im3.FilePath
                FROM ImageMeta im3
                INNER JOIN ImageTag it3 ON im3.Id = it3.ImageMetaId
                INNER JOIN Tag t3 ON it3.TagId = t3.Id
                WHERE t3.Name IN @ExcludeTags COLLATE NOCASE
              )";
        }

        if (requireAllBase)
            sql += "\n            GROUP BY im.FilePath\n            HAVING COUNT(DISTINCT t.Name) = @BaseCount";

        var results = await conn.QueryAsync<string>(sql,
            new { BaseTags = baseTags, EachTags = eachTags, ExcludeTags = excludeTags ?? new List<string>(), BaseCount = baseTags.Count });
        return results.ToList();
    }

    public async Task<List<string>> GetFilePathsExcludingTagsAsync(List<string> excludeTags, bool requireAll)
    {
        using var conn = _dbFactory.CreateConnection();
        if (requireAll)
        {
            // Exclude files that have ALL specified tags
            var sql = @"
                SELECT im.FilePath
                FROM ImageMeta im
                WHERE im.FilePath NOT IN (
                    SELECT im2.FilePath
                    FROM ImageMeta im2
                    INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                    INNER JOIN Tag t2 ON it2.TagId = t2.Id
                    WHERE t2.Name IN @ExcludeTags COLLATE NOCASE
                    GROUP BY im2.FilePath
                    HAVING COUNT(DISTINCT t2.Name) = @TagCount
                )";
            var result = await conn.QueryAsync<string>(sql,
                new { ExcludeTags = excludeTags, TagCount = excludeTags.Count });
            return result.AsList();
        }
        else
        {
            // Exclude files that have ANY of the specified tags
            var sql = @"
                SELECT im.FilePath
                FROM ImageMeta im
                WHERE im.FilePath NOT IN (
                    SELECT DISTINCT im2.FilePath
                    FROM ImageMeta im2
                    INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                    INNER JOIN Tag t2 ON it2.TagId = t2.Id
                    WHERE t2.Name IN @ExcludeTags COLLATE NOCASE
                )";
            var result = await conn.QueryAsync<string>(sql,
                new { ExcludeTags = excludeTags });
            return result.AsList();
        }
    }

    public async Task<List<string>> GetFilePathsWithNoTagsAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        var sql = @"
            SELECT im.FilePath
            FROM ImageMeta im
            WHERE im.Id NOT IN (SELECT DISTINCT ImageMetaId FROM ImageTag)
            ORDER BY im.FilePath";
        var result = await conn.QueryAsync<string>(sql);
        return result.AsList();
    }

    public async Task<List<TagCount>> GetCoOccurringTagsAsync(List<string> filePaths, List<string>? excludeNames = null, string? nameFilter = null)
    {
        if (filePaths.Count == 0) return new List<TagCount>();
        using var conn = _dbFactory.CreateConnection();
        var sql = @"
            SELECT t.Name, COUNT(DISTINCT it.ImageMetaId) as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            INNER JOIN ImageMeta im ON it.ImageMetaId = im.Id
            WHERE im.FilePath IN @FilePaths";
        if (excludeNames is { Count: > 0 })
            sql += "\n              AND t.Name NOT IN @ExcludeNames COLLATE NOCASE";
        if (!string.IsNullOrEmpty(nameFilter))
            sql += "\n              AND t.Name LIKE @NameFilter COLLATE NOCASE";
        sql += "\n            GROUP BY t.Id, t.Name\n            ORDER BY Count DESC, t.Name";

        var results = await conn.QueryAsync<TagCount>(sql,
            new { FilePaths = filePaths, ExcludeNames = excludeNames ?? new List<string>(), NameFilter = $"%{nameFilter}%" });
        return results.ToList();
    }

    public async Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in filePaths.Chunk(900))
        {
            var rows = await conn.QueryAsync<(string FilePath, string PerceptualHash)>(@"
                SELECT FilePath, PerceptualHash FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk });

            foreach (var (path, hash) in rows)
                if (!string.IsNullOrEmpty(hash))
                    result[path] = hash;
        }
        return result;
    }

    public async Task<Dictionary<string, (int Width, int Height)>> GetDimensionsByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in filePaths.Chunk(900))
        {
            var rows = await conn.QueryAsync<(string FilePath, int Width, int Height)>(@"
                SELECT FilePath, Width, Height FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths AND Width > 0",
                new { Paths = chunk });
            foreach (var (path, w, h) in rows)
                result[path] = (w, h);
        }
        return result;
    }

    public async Task<Dictionary<string, string>> GetFileHashesByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;
        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in filePaths.Chunk(900))
        {
            var rows = await conn.QueryAsync<(string FilePath, string FileHash)>(@"
                SELECT FilePath, FileHash FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths AND FileHash IS NOT NULL",
                new { Paths = chunk });
            foreach (var (path, hash) in rows)
            {
                if (!string.IsNullOrEmpty(hash))
                    result[path] = hash;
            }
        }
        return result;
    }

    public async Task<HashSet<string>> GetHashedPathsAsync(List<string> filePaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;
        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in filePaths.Chunk(900))
        {
            var rows = await conn.QueryAsync<string>(@"
                SELECT FilePath FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths AND HashStatus IN (1, -1)",
                new { Paths = chunk });
            foreach (var path in rows)
                result.Add(path);
        }
        return result;
    }

    public async Task ResetHashStatusAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync("UPDATE ImageMeta SET HashStatus = 0");
    }

    public async Task<List<string>> ResetFailedHashStatusByFolderAsync(string folderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        var paths = (await conn.QueryAsync<string>(@"
            SELECT FilePath FROM ImageMeta
            WHERE HashStatus = -1
              AND FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" }, txn)).ToList();

        if (paths.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var chunk in paths.Chunk(900))
            {
                await conn.ExecuteAsync(@"
                    UPDATE ImageMeta
                    SET HashStatus = 0,
                        FileHash = NULL,
                        PerceptualHash = NULL,
                        UpdatedAt = @Now
                    WHERE HashStatus = -1
                      AND FilePath COLLATE NOCASE IN @Paths",
                    new { Paths = chunk, Now = now }, txn);
            }
        }

        txn.Commit();
        return paths;
    }

    public async Task<ImageMeta?> GetByFileHashAsync(string fileHash)
    {
        if (string.IsNullOrEmpty(fileHash)) return null;
        using var conn = _dbFactory.CreateConnection();
        var meta = await conn.QueryFirstOrDefaultAsync<ImageMeta>(@"
            SELECT * FROM ImageMeta WHERE FileHash = @Hash LIMIT 1",
            new { Hash = fileHash });
        if (meta != null)
            meta.Tags = await GetTagsForMetaAsync(conn, meta.Id);
        return meta;
    }

    public async Task UpdateFilePathAsync(long id, string newPath, long newFolderId)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ImageMeta SET FilePath = @Path, FolderId = @FolderId, UpdatedAt = @Now
            WHERE Id = @Id",
            new { Path = newPath, FolderId = newFolderId, Now = DateTime.UtcNow, Id = id });
    }

    public async Task<Dictionary<string, (long Id, int Status)>> GetStatusMapByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
        if (filePaths.Count == 0) return result;
        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in filePaths.Chunk(900))
        {
            var rows = await conn.QueryAsync<(long Id, string FilePath, int Status)>(
                "SELECT Id, FilePath, AutoTagStatus AS Status FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk });
            foreach (var row in rows)
                result[row.FilePath] = (row.Id, row.Status);
        }
        return result;
    }

    public async Task SetAutoTagStatusByPathAsync(string filePath, int status)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET AutoTagStatus = @Status WHERE FilePath = @Path COLLATE NOCASE",
            new { Status = status, Path = filePath });
    }

    public async Task SetAutoTagStatusBatchAsync(List<string> filePaths, int status)
    {
        if (filePaths.Count == 0) return;
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET AutoTagStatus = @Status WHERE FilePath IN @Paths",
            new { Status = status, Paths = filePaths });
    }

    public async Task<List<ImageMeta>> GetAllUnlinkedAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        return (await conn.QueryAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FolderId IS NULL OR FolderId = 0")).ToList();
    }

    private async Task<List<TagCount>> GetTagsForMetaAsync(SqliteConnection conn, long metaId)
    {
        var tags = await conn.QueryAsync<TagCount>(@"
            SELECT t.Name, 0 as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            WHERE it.ImageMetaId = @MetaId
            ORDER BY t.Name", new { MetaId = metaId });
        return tags.ToList();
    }

    private async Task<Dictionary<long, List<TagCount>>> GetTagMapAsync(SqliteConnection conn, List<long>? imageIds = null)
    {
        string sql;
        object param;
        if (imageIds != null && imageIds.Count > 0)
        {
            sql = @"SELECT it.ImageMetaId, t.Name
                    FROM Tag t INNER JOIN ImageTag it ON t.Id = it.TagId
                    WHERE it.ImageMetaId IN @Ids ORDER BY t.Name";
            param = new { Ids = imageIds };
        }
        else
        {
            sql = @"SELECT it.ImageMetaId, t.Name
                    FROM Tag t INNER JOIN ImageTag it ON t.Id = it.TagId
                    ORDER BY t.Name";
            param = new { };
        }
        var rows = await conn.QueryAsync<(long ImageMetaId, string Name)>(sql, param);

        var map = new Dictionary<long, List<TagCount>>();
        foreach (var (metaId, name) in rows)
        {
            if (!map.TryGetValue(metaId, out var list))
            {
                list = new List<TagCount>();
                map[metaId] = list;
            }
            list.Add(new TagCount { Name = name });
        }
        return map;
    }

    public async Task<Dictionary<string, long>> GetIdsByPathsAsync(List<string> filePaths)
    {
        if (filePaths.Count == 0) return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var conn = _dbFactory.CreateConnection();
        var rows = await conn.QueryAsync<(string FilePath, long Id)>(
            "SELECT FilePath, Id FROM ImageMeta WHERE FilePath IN @Paths",
            new { Paths = filePaths });
        return rows.ToDictionary(r => r.FilePath, r => r.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddTagToImagesAsync(List<long> imageIds, string tag)
    {
        if (imageIds.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;
        var trimmed = tag.Trim();
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        var tagId = await conn.ExecuteScalarAsync<long>(@"
            INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
            SELECT Id FROM Tag WHERE Name = @Name;",
            new { Name = trimmed }, txn);

        // Batch insert ImageTag rows — Dapper doesn't support multi-row VALUES with parameters
        // so we use a single INSERT with a parameterized IN clause via a temp table or direct loop
        foreach (var imageId in imageIds)
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId) VALUES (@ImageId, @TagId)",
                new { ImageId = imageId, TagId = tagId }, txn);
        }

        txn.Commit();
    }

    public async Task RemoveTagFromImagesAsync(List<long> imageIds, string tag)
    {
        if (imageIds.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            DELETE FROM ImageTag
            WHERE ImageMetaId IN @Ids
              AND TagId = (SELECT Id FROM Tag WHERE Name = @TagName COLLATE NOCASE)",
            new { Ids = imageIds, TagName = tag.Trim() });
    }

    public async Task ClearTagsAndStatusBatchAsync(List<string> filePaths)
    {
        if (filePaths.Count == 0) return;
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        await conn.ExecuteAsync(
            "DELETE FROM ImageTag WHERE ImageMetaId IN (SELECT Id FROM ImageMeta WHERE FilePath IN @Paths)",
            new { Paths = filePaths }, txn);
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET AutoTagStatus = 0 WHERE FilePath IN @Paths",
            new { Paths = filePaths }, txn);
        txn.Commit();
    }

    public async Task ClearTagsFromImagesAsync(List<long> imageIds)
    {
        if (imageIds.Count == 0) return;
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM ImageTag WHERE ImageMetaId IN @Ids",
            new { Ids = imageIds });
    }
}
