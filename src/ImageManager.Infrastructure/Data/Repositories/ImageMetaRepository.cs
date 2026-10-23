using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data.Helpers;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ImageManager.Infrastructure.Data.Repositories;

public class ImageMetaRepository : IImageMetaRepository
{
    private readonly IDbContextFactory _dbFactory;
    private readonly IHashDataVersionTracker? _versionTracker;

    public ImageMetaRepository(IDbContextFactory dbFactory, IHashDataVersionTracker? versionTracker = null)
    {
        _dbFactory = dbFactory;
        _versionTracker = versionTracker;
    }

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

    public async Task<List<ImageMeta>> GetByFolderAsync(string folderPath, bool recursive = true)
    {
        using var conn = _dbFactory.CreateConnection();
        var (predicate, paramObj) = SqlPathHelper.CreateFolderPredicate("FilePath", folderPath, recursive);
        var metas = (await conn.QueryAsync<ImageMeta>(
            $"SELECT * FROM ImageMeta WHERE {predicate}",
            paramObj)).ToList();

        if (metas.Count == 0) return metas;

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

    public async Task<IReadOnlyList<ImageSortMetadata>> GetSortMetadataByFolderAsync(
        string folderPath,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = PathHelper.NormalizeFolderPath(folderPath);
        var relativePath = "substr(FilePath, length(@Prefix) + 1)";
        var directFilesPredicate = recursive
            ? string.Empty
            : $" AND instr({relativePath}, @DirectorySeparator) = 0"
                + $" AND instr({relativePath}, @AlternateDirectorySeparator) = 0";

        using var conn = _dbFactory.CreateConnection();
        var command = new CommandDefinition($@"
            SELECT FilePath, FileSize, LastWriteTicks, Width, Height
            FROM ImageMeta
            WHERE substr(FilePath, 1, length(@Prefix)) COLLATE NOCASE = @Prefix
              {directFilesPredicate}",
            new
            {
                Prefix = normalized,
                DirectorySeparator = Path.DirectorySeparatorChar.ToString(),
                AlternateDirectorySeparator = Path.AltDirectorySeparatorChar.ToString()
            },
            cancellationToken: cancellationToken);

        var rows = await conn.QueryAsync<ImageSortMetadata>(command);
        cancellationToken.ThrowIfCancellationRequested();
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ImageSortMetadata>> GetSortMetadataByPathsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (filePaths.Count == 0)
            return [];

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0)
            return [];

        using var conn = _dbFactory.CreateConnection();
        var result = new List<ImageSortMetadata>();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = new CommandDefinition(@"
                SELECT FilePath, FileSize, LastWriteTicks, Width, Height
                FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk },
                cancellationToken: cancellationToken);
            var rows = await conn.QueryAsync<ImageSortMetadata>(command);
            result.AddRange(rows);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
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

    public async Task<List<ImageMeta>> GetHashMetadataByPathsAsync(IReadOnlyCollection<string> filePaths)
    {
        if (filePaths.Count == 0) return [];

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return [];

        using var conn = _dbFactory.CreateConnection();
        var result = new List<ImageMeta>();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<ImageMeta>(@"
                SELECT Id, FilePath, FileHash, PerceptualHash, Width, Height,
                       FileSize, LastWriteTicks, FolderId, HashStatus
                FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths", new { Paths = chunk });
            result.AddRange(rows);
        }
        return result;
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
        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);
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
        else if (meta.Id > 0 && await conn.QuerySingleOrDefaultAsync<(long Id, string FilePath, long? FolderId, long LastWriteTicks, long FileSize, DateTime? UpdatedAt)>(
            "SELECT Id, FilePath, FolderId, LastWriteTicks, FileSize, UpdatedAt FROM ImageMeta WHERE Id = @Id", new { meta.Id }, txn) is var byId && byId != default)
        {
            // Record was concurrently moved/renamed (its FilePath in DB is now byId.FilePath)
            meta.Id = byId.Id;
            bool isStale = (byId.LastWriteTicks > meta.LastWriteTicks)
                || (byId.LastWriteTicks == meta.LastWriteTicks && byId.FileSize != meta.FileSize && byId.FileSize > 0);

            if (!isStale)
            {
                meta.UpdatedAt = DateTime.UtcNow;
                if (byId.FolderId.HasValue)
                    meta.FolderId = byId.FolderId.Value;
                await conn.ExecuteAsync(@"
                    UPDATE ImageMeta SET
                        FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                        Width = @Width, Height = @Height,
                        FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                        FolderId = @FolderId, UpdatedAt = @UpdatedAt, HashStatus = @HashStatus
                    WHERE Id = @Id", meta, txn);
            }
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
        _versionTracker?.IncrementVersion();
        return meta.Id;
    }

    public async Task BulkUpsertAsync(List<ImageMeta> metas)
    {
        if (metas.Count == 0) return;

        // Keep background batches bounded, including the final drain of a hash job.
        if (metas.Count > 100)
        {
            foreach (var batch in metas.Chunk(100))
                await BulkUpsertAsync(batch.ToList());
            return;
        }

        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);

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

                var unmatchedMetasWithId = metas
                    .Where(m => !existingMap.ContainsKey(m.FilePath) && m.Id > 0)
                    .Select(m => m.Id)
                    .Distinct()
                    .ToList();

                var existingByIdMap = new Dictionary<long, (string FilePath, long? FolderId, long LastWriteTicks, long FileSize, DateTime? UpdatedAt)>();
                if (unmatchedMetasWithId.Count > 0)
                {
                    var existingById = await conn.QueryAsync<(long Id, string FilePath, long? FolderId, long LastWriteTicks, long FileSize, DateTime? UpdatedAt)>(@"
                        SELECT Id, FilePath, FolderId, LastWriteTicks, FileSize, UpdatedAt FROM ImageMeta WHERE Id IN @Ids",
                        new { Ids = unmatchedMetasWithId }, txn);
                    foreach (var row in existingById)
                        existingByIdMap[row.Id] = (row.FilePath, row.FolderId, row.LastWriteTicks, row.FileSize, row.UpdatedAt);
                }

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
                        existingByIdMap[meta.Id] = (meta.FilePath, meta.FolderId, meta.LastWriteTicks, meta.FileSize, now);
                    }
                    else if (meta.Id > 0 && existingByIdMap.TryGetValue(meta.Id, out var byId))
                    {
                        // Concurrently moved/renamed: update existing record by Id, preserving its new FilePath unless incoming data is stale
                        meta.Id = meta.Id;
                        bool isStale = (byId.LastWriteTicks > meta.LastWriteTicks)
                            || (byId.LastWriteTicks == meta.LastWriteTicks && byId.FileSize != meta.FileSize && byId.FileSize > 0);

                        if (!isStale)
                        {
                            meta.UpdatedAt = now;
                            if (byId.FolderId.HasValue)
                                meta.FolderId = byId.FolderId.Value;
                            await conn.ExecuteAsync(@"
                                UPDATE ImageMeta SET
                                    FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                                    Width = @Width, Height = @Height,
                                    FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                                    FolderId = @FolderId, UpdatedAt = @UpdatedAt, HashStatus = @HashStatus
                                WHERE Id = @Id", meta, txn);
                            existingByIdMap[meta.Id] = (byId.FilePath, byId.FolderId, meta.LastWriteTicks, meta.FileSize, now);
                        existingMap[byId.FilePath] = (meta.Id, byId.FolderId, true);
                        }
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
                        existingMap[meta.FilePath] = (meta.Id, meta.FolderId, meta.FolderId.HasValue);
                        existingByIdMap[meta.Id] = (meta.FilePath, meta.FolderId, meta.LastWriteTicks, meta.FileSize, now);
                    }
                }

                txn.Commit();
                _versionTracker?.IncrementVersion();
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
        var affected = await conn.ExecuteAsync("DELETE FROM ImageMeta WHERE Id = @Id", new { Id = id });
        if (affected > 0) _versionTracker?.IncrementVersion();
        return affected;
    }

    public async Task<int> DeleteByPathAsync(string filePath)
    {
        using var conn = _dbFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FilePath = filePath });
        if (affected > 0) _versionTracker?.IncrementVersion();
        return affected;
    }

    public async Task<int> DeleteByFolderAsync(string folderPath, bool recursive = true)
    {
        using var conn = _dbFactory.CreateConnection();
        var (predicate, paramObj) = SqlPathHelper.CreateFolderPredicate("FilePath", folderPath, recursive);
        var affected = await conn.ExecuteAsync(
            $"DELETE FROM ImageMeta WHERE {predicate}",
            paramObj);
        if (affected > 0) _versionTracker?.IncrementVersion();
        return affected;
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
    public async Task<int> DeleteAllAutoTagsByFolderAsync(string folderPath, bool recursive = true)
    {
        using var conn = _dbFactory.CreateConnection();
        var (predicate, paramObj) = SqlPathHelper.CreateFolderPredicate("FilePath", folderPath, recursive);
        return await conn.ExecuteAsync($@"
            DELETE FROM ImageTag
            WHERE ImageMetaId IN (SELECT Id FROM ImageMeta WHERE {predicate})
              AND Source IN ('AutoTag', 'AutoTagConfirmed')",
            paramObj);
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

    public async Task<Dictionary<string, List<string>>> GetTagMapByFolderAsync(string folderPath, bool recursive = true)
    {
        using var conn = _dbFactory.CreateConnection();
        var (predicate, paramObj) = SqlPathHelper.CreateFolderPredicate("im.FilePath", folderPath, recursive);
        var rows = await conn.QueryAsync<(string FilePath, string TagName)>($@"
            SELECT im.FilePath, t.Name
            FROM ImageMeta im
            LEFT JOIN ImageTag it ON im.Id = it.ImageMetaId
            LEFT JOIN Tag t ON it.TagId = t.Id
            WHERE {predicate}",
            paramObj);

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

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        // Process in chunks to avoid overly large IN clauses
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, string TagName)>(@"
                SELECT im.FilePath, t.Name
                FROM ImageMeta im
                LEFT JOIN ImageTag it ON im.Id = it.ImageMetaId
                LEFT JOIN Tag t ON it.TagId = t.Id
                WHERE im.FilePath COLLATE NOCASE IN @Paths",
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
        if (tagNames == null || tagNames.Count == 0)
            return [];

        var distinctTags = tagNames
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctTags.Count == 0)
            return [];

        using var conn = _dbFactory.CreateConnection();
        if (distinctTags.Count <= 500)
        {
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
                    new { TagNames = distinctTags, TagCount = distinctTags.Count });
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
                    new { TagNames = distinctTags });
                return results.ToList();
            }
        }
        else
        {
            var tempTable = $"temp.TagInc_{Guid.NewGuid():N}";
            try
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {tempTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctTags.Chunk(500))
                {
                    await conn.ExecuteAsync(
                        $"INSERT OR IGNORE INTO {tempTable} (Name) VALUES (@Name)",
                        chunk.Select(n => new { Name = n }));
                }

                if (requireAll)
                {
                    var results = await conn.QueryAsync<string>($@"
                        SELECT im.FilePath
                        FROM ImageMeta im
                        INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                        INNER JOIN Tag t ON it.TagId = t.Id
                        INNER JOIN {tempTable} inc ON t.Name = inc.Name COLLATE NOCASE
                        GROUP BY im.FilePath
                        HAVING COUNT(DISTINCT t.Name) = @TagCount",
                        new { TagCount = distinctTags.Count });
                    return results.ToList();
                }
                else
                {
                    var results = await conn.QueryAsync<string>($@"
                        SELECT DISTINCT im.FilePath
                        FROM ImageMeta im
                        INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                        INNER JOIN Tag t ON it.TagId = t.Id
                        INNER JOIN {tempTable} inc ON t.Name = inc.Name COLLATE NOCASE");
                    return results.ToList();
                }
            }
            finally
            {
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {tempTable};"); } catch { }
            }
        }
    }

    public async Task<List<string>> GetFilePathsByTagsExcludingAsync(
        List<string> includeTags, bool requireAll, List<string> excludeTags)
    {
        if (includeTags == null || includeTags.Count == 0)
            return [];

        var distinctInclude = includeTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctInclude.Count == 0)
            return [];

        var distinctExclude = (excludeTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctExclude.Count == 0)
            return await GetFilePathsByTagsAsync(distinctInclude, requireAll);

        using var conn = _dbFactory.CreateConnection();
        if (distinctInclude.Count <= 500 && distinctExclude.Count <= 500)
        {
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
                    new { IncludeTags = distinctInclude, ExcludeTags = distinctExclude, IncludeCount = distinctInclude.Count });
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
                    new { IncludeTags = distinctInclude, ExcludeTags = distinctExclude });
                return results.ToList();
            }
        }
        else
        {
            var incTable = distinctInclude.Count > 500 ? $"temp.TagInc_{Guid.NewGuid():N}" : null;
            var excTable = distinctExclude.Count > 500 ? $"temp.TagExc_{Guid.NewGuid():N}" : null;
            try
            {
                if (incTable != null)
                {
                    await conn.ExecuteAsync($"CREATE TEMP TABLE {incTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                    foreach (var chunk in distinctInclude.Chunk(500))
                    {
                        await conn.ExecuteAsync(
                            $"INSERT OR IGNORE INTO {incTable} (Name) VALUES (@Name)",
                            chunk.Select(n => new { Name = n }));
                    }
                }

                if (excTable != null)
                {
                    await conn.ExecuteAsync($"CREATE TEMP TABLE {excTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                    foreach (var chunk in distinctExclude.Chunk(500))
                    {
                        await conn.ExecuteAsync(
                            $"INSERT OR IGNORE INTO {excTable} (Name) VALUES (@Name)",
                            chunk.Select(n => new { Name = n }));
                    }
                }

                var incJoin = incTable != null
                    ? $"INNER JOIN {incTable} inc ON t.Name = inc.Name COLLATE NOCASE"
                    : "WHERE t.Name IN @IncludeTags COLLATE NOCASE";
                var excFilter = excTable != null
                    ? $"INNER JOIN {excTable} exc ON t2.Name = exc.Name COLLATE NOCASE"
                    : "WHERE t2.Name IN @ExcludeTags COLLATE NOCASE";

                var incWhereOrAnd = incTable != null ? "WHERE" : "AND";

                var sql = $@"
                    SELECT {(requireAll ? "im.FilePath" : "DISTINCT im.FilePath")}
                    FROM ImageMeta im
                    INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                    INNER JOIN Tag t ON it.TagId = t.Id
                    {incJoin}
                    {incWhereOrAnd} im.FilePath NOT IN (
                        SELECT DISTINCT im2.FilePath
                        FROM ImageMeta im2
                        INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                        INNER JOIN Tag t2 ON it2.TagId = t2.Id
                        {excFilter}
                    )";

                if (requireAll)
                {
                    sql += @"
                    GROUP BY im.FilePath
                    HAVING COUNT(DISTINCT t.Name) = @IncludeCount";
                }

                var results = await conn.QueryAsync<string>(sql, new
                {
                    IncludeTags = distinctInclude,
                    ExcludeTags = distinctExclude,
                    IncludeCount = distinctInclude.Count
                });
                return results.ToList();
            }
            finally
            {
                if (incTable != null)
                    try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {incTable};"); } catch { }
                if (excTable != null)
                    try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {excTable};"); } catch { }
            }
        }
    }

    public async Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null)
    {
        if (baseTags == null || baseTags.Count == 0 || eachTags == null || eachTags.Count == 0)
            return [];

        var distinctBase = baseTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var distinctEach = eachTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var distinctExclude = (excludeTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctBase.Count == 0 || distinctEach.Count == 0)
            return [];

        using var conn = _dbFactory.CreateConnection();
        var baseTable = distinctBase.Count > 500 ? $"temp.TagBase_{Guid.NewGuid():N}" : null;
        var eachTable = distinctEach.Count > 500 ? $"temp.TagEach_{Guid.NewGuid():N}" : null;
        var excTable = distinctExclude.Count > 500 ? $"temp.TagExc_{Guid.NewGuid():N}" : null;

        try
        {
            if (baseTable != null)
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {baseTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctBase.Chunk(500))
                    await conn.ExecuteAsync($"INSERT OR IGNORE INTO {baseTable} (Name) VALUES (@Name)", chunk.Select(n => new { Name = n }));
            }
            if (eachTable != null)
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {eachTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctEach.Chunk(500))
                    await conn.ExecuteAsync($"INSERT OR IGNORE INTO {eachTable} (Name) VALUES (@Name)", chunk.Select(n => new { Name = n }));
            }
            if (excTable != null)
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {excTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctExclude.Chunk(500))
                    await conn.ExecuteAsync($"INSERT OR IGNORE INTO {excTable} (Name) VALUES (@Name)", chunk.Select(n => new { Name = n }));
            }

            var baseJoin = baseTable != null
                ? $"INNER JOIN {baseTable} b ON t.Name = b.Name COLLATE NOCASE"
                : "WHERE t.Name IN @BaseTags COLLATE NOCASE";
            var eachFilter = eachTable != null
                ? $"INNER JOIN {eachTable} e ON t2.Name = e.Name COLLATE NOCASE"
                : "WHERE t2.Name IN @EachTags COLLATE NOCASE";
            var excFilter = excTable != null
                ? $"INNER JOIN {excTable} ex ON t3.Name = ex.Name COLLATE NOCASE"
                : "WHERE t3.Name IN @ExcludeTags COLLATE NOCASE";

            var baseWhereOrAnd = baseTable != null ? "WHERE" : "AND";

            var sql = $@"
                SELECT im.FilePath
                FROM ImageMeta im
                INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
                INNER JOIN Tag t ON it.TagId = t.Id
                {baseJoin}
                {baseWhereOrAnd} im.FilePath IN (
                    SELECT DISTINCT im2.FilePath
                    FROM ImageMeta im2
                    INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                    INNER JOIN Tag t2 ON it2.TagId = t2.Id
                    {eachFilter}
                )";

            if (distinctExclude.Count > 0)
            {
                sql += $@"
                  AND im.FilePath NOT IN (
                    SELECT DISTINCT im3.FilePath
                    FROM ImageMeta im3
                    INNER JOIN ImageTag it3 ON im3.Id = it3.ImageMetaId
                    INNER JOIN Tag t3 ON it3.TagId = t3.Id
                    {excFilter}
                  )";
            }

            if (requireAllBase)
                sql += "\nGROUP BY im.FilePath\nHAVING COUNT(DISTINCT t.Name) = @BaseCount";

            var results = await conn.QueryAsync<string>(sql, new
            {
                BaseTags = distinctBase,
                EachTags = distinctEach,
                ExcludeTags = distinctExclude,
                BaseCount = distinctBase.Count
            });
            return results.ToList();
        }
        finally
        {
            if (baseTable != null)
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {baseTable};"); } catch { }
            if (eachTable != null)
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {eachTable};"); } catch { }
            if (excTable != null)
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {excTable};"); } catch { }
        }
    }

    public async Task<List<string>> GetFilePathsExcludingTagsAsync(List<string> excludeTags, bool requireAll)
    {
        var distinctExclude = (excludeTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var conn = _dbFactory.CreateConnection();
        if (distinctExclude.Count == 0)
        {
            var rows = await conn.QueryAsync<string>("SELECT FilePath FROM ImageMeta ORDER BY FilePath");
            return rows.AsList();
        }

        if (distinctExclude.Count <= 500)
        {
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
                    new { ExcludeTags = distinctExclude, TagCount = distinctExclude.Count });
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
                    new { ExcludeTags = distinctExclude });
                return result.AsList();
            }
        }
        else
        {
            var excTable = $"temp.TagExc_{Guid.NewGuid():N}";
            try
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {excTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctExclude.Chunk(500))
                    await conn.ExecuteAsync($"INSERT OR IGNORE INTO {excTable} (Name) VALUES (@Name)", chunk.Select(n => new { Name = n }));

                if (requireAll)
                {
                    var sql = $@"
                        SELECT im.FilePath
                        FROM ImageMeta im
                        WHERE im.FilePath NOT IN (
                            SELECT im2.FilePath
                            FROM ImageMeta im2
                            INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                            INNER JOIN Tag t2 ON it2.TagId = t2.Id
                            INNER JOIN {excTable} exc ON t2.Name = exc.Name COLLATE NOCASE
                            GROUP BY im2.FilePath
                            HAVING COUNT(DISTINCT t2.Name) = @TagCount
                        )";
                    var result = await conn.QueryAsync<string>(sql, new { TagCount = distinctExclude.Count });
                    return result.AsList();
                }
                else
                {
                    var sql = $@"
                        SELECT im.FilePath
                        FROM ImageMeta im
                        WHERE im.FilePath NOT IN (
                            SELECT DISTINCT im2.FilePath
                            FROM ImageMeta im2
                            INNER JOIN ImageTag it2 ON im2.Id = it2.ImageMetaId
                            INNER JOIN Tag t2 ON it2.TagId = t2.Id
                            INNER JOIN {excTable} exc ON t2.Name = exc.Name COLLATE NOCASE
                        )";
                    var result = await conn.QueryAsync<string>(sql);
                    return result.AsList();
                }
            }
            finally
            {
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {excTable};"); } catch { }
            }
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
        if (filePaths == null || filePaths.Count == 0) return new List<TagCount>();

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return new List<TagCount>();

        var distinctExclude = (excludeNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var conn = _dbFactory.CreateConnection();
        var scopeTable = $"temp.CoTagScope_{Guid.NewGuid():N}";
        var excTable = distinctExclude.Count > 500 ? $"temp.CoTagExc_{Guid.NewGuid():N}" : null;

        try
        {
            await conn.ExecuteAsync($"CREATE TEMP TABLE {scopeTable.Replace("temp.", "")} (ImageMetaId INTEGER PRIMARY KEY) WITHOUT ROWID;");

            foreach (var chunk in distinctPaths.Chunk(500))
            {
                await conn.ExecuteAsync($@"
                    INSERT OR IGNORE INTO {scopeTable} (ImageMetaId)
                    SELECT Id FROM ImageMeta
                    WHERE FilePath COLLATE NOCASE IN @Paths",
                    new { Paths = chunk });
            }

            if (excTable != null)
            {
                await conn.ExecuteAsync($"CREATE TEMP TABLE {excTable.Replace("temp.", "")} (Name TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;");
                foreach (var chunk in distinctExclude.Chunk(500))
                {
                    await conn.ExecuteAsync(
                        $"INSERT OR IGNORE INTO {excTable} (Name) VALUES (@Name)",
                        chunk.Select(n => new { Name = n }));
                }
            }

            var sql = $@"
                SELECT t.Name, COUNT(DISTINCT it.ImageMetaId) as Count
                FROM Tag t
                INNER JOIN ImageTag it ON t.Id = it.TagId
                INNER JOIN {scopeTable} scope ON it.ImageMetaId = scope.ImageMetaId";

            var hasExclude = distinctExclude.Count > 0;
            if (hasExclude)
            {
                if (excTable != null)
                    sql += $"\nWHERE t.Name NOT IN (SELECT Name FROM {excTable})";
                else
                    sql += "\nWHERE t.Name NOT IN @ExcludeNames COLLATE NOCASE";
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                var prefix = hasExclude ? "AND" : "WHERE";
                sql += $"\n{prefix} t.Name LIKE @NameFilter ESCAPE '\\' COLLATE NOCASE";
            }

            sql += "\nGROUP BY t.Id, t.Name\nORDER BY Count DESC, t.Name COLLATE NOCASE";

            var escapedFilter = !string.IsNullOrEmpty(nameFilter)
                ? SqlPathHelper.EscapeLikePattern(nameFilter.Trim())
                : null;

            var results = await conn.QueryAsync<TagCount>(sql, new
            {
                ExcludeNames = distinctExclude,
                NameFilter = escapedFilter != null ? $"%{escapedFilter}%" : null
            });
            return results.ToList();
        }
        finally
        {
            try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {scopeTable};"); } catch { }
            if (excTable != null)
                try { await conn.ExecuteAsync($"DROP TABLE IF EXISTS {excTable};"); } catch { }
        }
    }

    public async Task<ICoTagQuerySession> CreateCoTagQuerySessionAsync(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string>? excludeNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var connection = _dbFactory.CreateConnection();
        try
        {
            const string createScopeSql = """
                DROP TABLE IF EXISTS temp.CoTagSearchScope;

                CREATE TEMP TABLE CoTagSearchScope (
                    ImageMetaId INTEGER PRIMARY KEY
                ) WITHOUT ROWID;

                INSERT INTO CoTagSearchScope (ImageMetaId)
                SELECT im.Id
                FROM ImageMeta im
                INNER JOIN json_each(@FilePathsJson) requested
                    ON im.FilePath = requested.value COLLATE NOCASE;
                """;

            var filePathsJson = JsonSerializer.Serialize(filePaths);
            await connection.ExecuteAsync(new CommandDefinition(
                createScopeSql,
                new { FilePathsJson = filePathsJson },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return new CoTagQuerySession(connection, excludeNames);
        }
        catch
        {
            await DropCoTagSearchScopeAsync(connection).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class CoTagQuerySession : ICoTagQuerySession
    {
        private readonly SqliteConnection _connection;
        private readonly IReadOnlyCollection<string> _excludeNames;
        private bool _disposed;

        public CoTagQuerySession(
            SqliteConnection connection,
            IReadOnlyCollection<string>? excludeNames)
        {
            _connection = connection;
            _excludeNames = excludeNames?.ToArray() ?? Array.Empty<string>();
        }

        public async Task<IReadOnlyList<TagCount>> QueryAsync(
            string nameFilter,
            int limit = 300,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(nameFilter) || limit <= 0)
                return Array.Empty<TagCount>();

            var escapedFilter = EscapeLikePattern(nameFilter.Trim());
            var sql = """
                SELECT t.Name, COUNT(*) AS Count
                FROM Tag t
                CROSS JOIN ImageTag it INDEXED BY idx_imagetag_tag
                CROSS JOIN CoTagSearchScope scope
                WHERE t.Name LIKE @ContainsPattern ESCAPE '\' COLLATE NOCASE
                  AND it.TagId = t.Id
                  AND scope.ImageMetaId = it.ImageMetaId
                """;

            if (_excludeNames.Count > 0)
                sql += "\n  AND t.Name NOT IN @ExcludeNames COLLATE NOCASE";

            sql += """

                GROUP BY t.Id, t.Name
                ORDER BY
                    CASE WHEN t.Name LIKE @PrefixPattern ESCAPE '\' COLLATE NOCASE THEN 0 ELSE 1 END,
                    Count DESC,
                    t.Name COLLATE NOCASE
                LIMIT @Limit
                """;

            var command = new CommandDefinition(
                sql,
                new
                {
                    ContainsPattern = $"%{escapedFilter}%",
                    PrefixPattern = $"{escapedFilter}%",
                    ExcludeNames = _excludeNames,
                    Limit = limit
                },
                cancellationToken: cancellationToken);
            var results = await _connection.QueryAsync<TagCount>(command).ConfigureAwait(false);
            return results.AsList();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                await DropCoTagSearchScopeAsync(_connection).ConfigureAwait(false);
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static string EscapeLikePattern(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static async Task DropCoTagSearchScopeAsync(SqliteConnection connection)
    {
        try
        {
            await connection.ExecuteAsync("DROP TABLE IF EXISTS temp.CoTagSearchScope;")
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Disposing the connection remains the final cleanup path when an
            // interrupted command leaves it unusable.
        }
    }

    public async Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, string PerceptualHash)>(@"
                SELECT FilePath, PerceptualHash FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths
                  AND HashStatus = 1",
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
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, int Width, int Height)>(@"
                SELECT FilePath, Width, Height FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths AND Width > 0 AND Height > 0",
                new { Paths = chunk });
            foreach (var (path, w, h) in rows)
                result[path] = (w, h);
        }
        return result;
    }

    public async Task UpdateDimensionsByPathsAsync(IReadOnlyDictionary<string, (int Width, int Height)> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        var valid = dimensions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key)
                && entry.Value.Width > 0 && entry.Value.Height > 0)
            .ToArray();
        if (valid.Length == 0) return;

        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);
        using var conn = _dbFactory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        foreach (var entry in valid)
        {
            await conn.ExecuteAsync(@"
                UPDATE ImageMeta
                SET Width = @Width, Height = @Height, UpdatedAt = @UpdatedAt
                WHERE FilePath = @FilePath COLLATE NOCASE",
                new
                {
                    FilePath = entry.Key,
                    entry.Value.Width,
                    entry.Value.Height,
                    UpdatedAt = DateTime.UtcNow
                }, transaction);
        }
        transaction.Commit();
    }

    public async Task<Dictionary<string, int>> GetSystemRatingsByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, int SystemRating)>(@"
                SELECT im.FilePath,
                    CASE
                        WHEN im.SystemRating BETWEEN 0 AND 3 THEN im.SystemRating
                        WHEN EXISTS (
                            SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                            WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('general', '全年龄')
                        ) THEN 0
                        WHEN EXISTS (
                            SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                            WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('sensitive', '敏感')
                        ) THEN 1
                        WHEN EXISTS (
                            SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                            WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('questionable', '大尺度')
                        ) THEN 2
                        WHEN EXISTS (
                            SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                            WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('explicit', 'r18', 'r-18')
                        ) THEN 3
                        ELSE -1
                    END AS SystemRating
                FROM ImageMeta im WHERE im.FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk });
            foreach (var (path, rating) in rows)
                result[path] = rating;
        }
        return result;
    }

    public async Task SetSystemRatingByPathAsync(string filePath, int rating)
    {
        if (rating is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(rating));

        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ImageMeta
            SET SystemRating = @Rating, UpdatedAt = @UpdatedAt
            WHERE FilePath COLLATE NOCASE = @FilePath",
            new { FilePath = filePath, Rating = rating, UpdatedAt = DateTime.UtcNow });
    }

    public async Task<Dictionary<string, string>> GetFileHashesByPathsAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
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
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<string>(@"
                SELECT FilePath FROM ImageMeta
                WHERE FilePath COLLATE NOCASE IN @Paths
                  AND HashStatus = 1
                  AND PerceptualHash IS NOT NULL
                  AND length(PerceptualHash) = 642",
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
        _versionTracker?.IncrementVersion();
    }

    public async Task<List<string>> ResetFailedHashStatusByFolderAsync(string folderPath, bool recursive = true)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var (predicate, paramObj) = SqlPathHelper.CreateFolderPredicate("FilePath", folderPath, recursive);
        var paths = (await conn.QueryAsync<string>($@"
            SELECT FilePath FROM ImageMeta
            WHERE HashStatus = -1
              AND {predicate}",
            paramObj, txn)).ToList();

        if (paths.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var chunk in paths.Chunk(500))
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
        if (paths.Count > 0)
            _versionTracker?.IncrementVersion();
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

    public async Task<ImageHashUpdateBatchResult> UpdateHashBatchAsync(IReadOnlyList<ImageHashUpdateItem> items)
    {
        if (items.Count == 0)
            return new ImageHashUpdateBatchResult(0, 0, Array.Empty<long>());

        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var conn = _dbFactory.CreateConnection();
                using var txn = conn.BeginTransaction();
                var now = DateTime.UtcNow;

                int updatedCount = 0;
                var staleIds = new List<long>();

                foreach (var item in items)
                {
                    int affected = await conn.ExecuteAsync(@"
                        UPDATE ImageMeta SET
                            FileHash = COALESCE(@FileHash, FileHash),
                            PerceptualHash = @PerceptualHash,
                            Width = COALESCE(@Width, Width),
                            Height = COALESCE(@Height, Height),
                            HashStatus = @HashStatus,
                            UpdatedAt = @Now
                        WHERE Id = @Id
                          AND FilePath = @ExpectedPath COLLATE NOCASE
                          AND LastWriteTicks = @ExpectedLastWriteTicks
                          AND FileSize = @ExpectedFileSize",
                        new
                        {
                            Id = item.Id,
                            ExpectedPath = item.ExpectedPath,
                            ExpectedLastWriteTicks = item.ExpectedLastWriteTicks,
                            ExpectedFileSize = item.ExpectedFileSize,
                            FileHash = item.FileHash,
                            PerceptualHash = item.PerceptualHash,
                            Width = item.Width,
                            Height = item.Height,
                            HashStatus = item.HashStatus,
                            Now = now
                        }, txn);

                    if (affected > 0)
                    {
                        updatedCount++;
                    }
                    else
                    {
                        staleIds.Add(item.Id);
                    }
                }

                txn.Commit();
                if (updatedCount > 0)
                    _versionTracker?.IncrementVersion();

                return new ImageHashUpdateBatchResult(updatedCount, staleIds.Count, staleIds);
            }
            catch (SqliteException ex) when (ex.Message.Contains("busy") || ex.Message.Contains("locked"))
            {
                if (attempt >= 3) throw;
                AppLogger.Warn($"UpdateHashBatchAsync locked/busy attempt {attempt}/3: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)));
            }
        }
    }

    public async Task<int> RegisterNewFilesBatchAsync(IReadOnlyList<ImageMeta> newFiles)
    {
        if (newFiles.Count == 0) return 0;

        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var conn = _dbFactory.CreateConnection();
                using var txn = conn.BeginTransaction();
                var now = DateTime.UtcNow;
                int registered = 0;

                foreach (var meta in newFiles)
                {
                    // Check file still exists on disk before registering
                    if (!File.Exists(meta.FilePath))
                        continue;

                    // Ensure record doesn't already exist under this path
                    var exists = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
                        new { FilePath = meta.FilePath }, txn);

                    if (exists == 0)
                    {
                        meta.CreatedAt = now;
                        meta.UpdatedAt = now;
                        meta.Id = await conn.ExecuteScalarAsync<long>(@"
                            INSERT INTO ImageMeta (FilePath, FileHash, PerceptualHash, Width, Height,
                                FileSize, LastWriteTicks, FolderId, CreatedAt, UpdatedAt, HashStatus)
                            VALUES (@FilePath, @FileHash, @PerceptualHash, @Width, @Height,
                                @FileSize, @LastWriteTicks, @FolderId, @CreatedAt, @UpdatedAt, @HashStatus);
                            SELECT last_insert_rowid();", meta, txn);
                        registered++;
                    }
                }

                txn.Commit();
                if (registered > 0)
                    _versionTracker?.IncrementVersion();

                return registered;
            }
            catch (SqliteException ex) when (ex.Message.Contains("busy") || ex.Message.Contains("locked"))
            {
                if (attempt >= 3) throw;
                AppLogger.Warn($"RegisterNewFilesBatchAsync locked/busy attempt {attempt}/3: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)));
            }
        }
    }

    public async Task UpdateFilePathAsync(long id, string newPath, long newFolderId)
    {
        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory);
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ImageMeta SET FilePath = @Path, FolderId = @FolderId, UpdatedAt = @Now
            WHERE Id = @Id",
            new { Path = newPath, FolderId = newFolderId, Now = DateTime.UtcNow, Id = id });
        _versionTracker?.IncrementVersion();
    }

    public async Task<Dictionary<string, (long Id, int Status)>> GetStatusMapByPathsAsync(List<string> filePaths,
        CancellationToken cancellationToken = default, Action<int>? progress = null)
    {
        var result = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        var completed = 0;
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await conn.QueryAsync<(long Id, string FilePath, int Status)>(new CommandDefinition(
                "SELECT Id, FilePath, AutoTagStatus AS Status FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk }, cancellationToken: cancellationToken));
            foreach (var row in rows)
                result[row.FilePath] = (row.Id, row.Status);
            completed += chunk.Length;
            progress?.Invoke(completed);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<Dictionary<string, (long Id, int Status)>> RegisterAutoTagFilesAsync(
        IReadOnlyList<AutoTagFileRegistration> files, CancellationToken cancellationToken = default)
    {
        if (files.Count > 100) throw new ArgumentOutOfRangeException(nameof(files), "Registration batches must not exceed 100 files.");
        cancellationToken.ThrowIfCancellationRequested();
        if (files.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        using var writer = await MetadataWriteCoordinator.EnterAsync(_dbFactory, cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Bound both connection setup and transaction lock waits.
                using var conn = _dbFactory.CreateConnection(commandTimeout: 1);
                using var txn = conn.BeginTransaction();
                var result = new Dictionary<string, (long Id, int Status)>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var meta = file.Metadata;
                    var existing = await conn.QueryFirstOrDefaultAsync<(long Id, int Status)>(new CommandDefinition(
                        "SELECT Id, AutoTagStatus FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE ORDER BY Id LIMIT 1",
                        new { meta.FilePath }, txn, cancellationToken: cancellationToken));
                    if (existing.Id > 0)
                    {
                        result[meta.FilePath] = existing;
                        continue; // A folder scan may have registered this file while it was being hashed.
                    }

                    if (file.MovedImageId.HasValue && file.PreviousPath != null)
                    {
                        var moved = await conn.ExecuteAsync(new CommandDefinition(@"
                            UPDATE ImageMeta SET FilePath = @FilePath, FolderId = @FolderId, UpdatedAt = @Now
                            WHERE Id = @Id AND FilePath = @PreviousPath AND FileHash = @FileHash",
                            new { meta.FilePath, meta.FolderId, meta.FileHash, Id = file.MovedImageId.Value,
                                file.PreviousPath, Now = DateTime.UtcNow }, txn, cancellationToken: cancellationToken));
                        if (moved > 0)
                        {
                            var status = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                                "SELECT AutoTagStatus FROM ImageMeta WHERE Id = @Id",
                                new { Id = file.MovedImageId.Value }, txn, cancellationToken: cancellationToken));
                            result[meta.FilePath] = (file.MovedImageId.Value, status);
                            continue;
                        }
                    }

                    var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
                        INSERT INTO ImageMeta (FilePath, FileHash, FileSize, LastWriteTicks, FolderId, HashStatus)
                        VALUES (@FilePath, @FileHash, @FileSize, @LastWriteTicks, @FolderId, 0);
                        SELECT last_insert_rowid();", meta, txn, cancellationToken: cancellationToken));
                    result[meta.FilePath] = (id, 0);
                }
                cancellationToken.ThrowIfCancellationRequested();
                txn.Commit();
                return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 3)
            {
                AppLogger.Warn($"AutoTag.Register locked attempt={attempt}/3 files={files.Count}");
                await Task.Delay(100 * attempt, cancellationToken);
            }
        }
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
        if (filePaths == null || filePaths.Count == 0) return;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            await conn.ExecuteAsync(
                "UPDATE ImageMeta SET AutoTagStatus = @Status WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Status = status, Paths = chunk }, txn);
        }
        txn.Commit();
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
        var map = new Dictionary<long, List<TagCount>>();
        if (imageIds != null)
        {
            if (imageIds.Count == 0)
                return map;

            var distinctIds = imageIds.Distinct().ToList();
            foreach (var chunk in distinctIds.Chunk(500))
            {
                var rows = await conn.QueryAsync<(long ImageMetaId, string Name)>(@"
                    SELECT it.ImageMetaId, t.Name
                    FROM Tag t INNER JOIN ImageTag it ON t.Id = it.TagId
                    WHERE it.ImageMetaId IN @Ids ORDER BY t.Name",
                    new { Ids = chunk });

                foreach (var (metaId, name) in rows)
                {
                    if (!map.TryGetValue(metaId, out var list))
                    {
                        list = new List<TagCount>();
                        map[metaId] = list;
                    }
                    list.Add(new TagCount { Name = name });
                }
            }
            return map;
        }

        // imageIds == null: query all tags for full database
        var allRows = await conn.QueryAsync<(long ImageMetaId, string Name)>(@"
            SELECT it.ImageMetaId, t.Name
            FROM Tag t INNER JOIN ImageTag it ON t.Id = it.TagId
            ORDER BY t.Name");

        foreach (var (metaId, name) in allRows)
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
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (filePaths == null || filePaths.Count == 0) return result;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in distinctPaths.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string FilePath, long Id)>(
                "SELECT FilePath, Id FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk });
            foreach (var (path, id) in rows)
                result[path] = id;
        }
        return result;
    }

    public async Task AddTagToImagesAsync(List<long> imageIds, string tag)
    {
        if (imageIds == null || imageIds.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;
        var distinctIds = imageIds.Distinct().ToList();
        if (distinctIds.Count == 0) return;

        var trimmed = tag.Trim();
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        var tagId = await conn.ExecuteScalarAsync<long>(@"
            INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
            SELECT Id FROM Tag WHERE Name = @Name;",
            new { Name = trimmed }, txn);

        foreach (var chunk in distinctIds.Chunk(500))
        {
            foreach (var imageId in chunk)
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId) VALUES (@ImageId, @TagId)",
                    new { ImageId = imageId, TagId = tagId }, txn);
            }
        }

        txn.Commit();
    }

    public async Task RemoveTagFromImagesAsync(List<long> imageIds, string tag)
    {
        if (imageIds == null || imageIds.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;
        var distinctIds = imageIds.Distinct().ToList();
        if (distinctIds.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        var tagId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM Tag WHERE Name = @TagName COLLATE NOCASE",
            new { TagName = tag.Trim() }, txn);

        if (tagId.HasValue)
        {
            foreach (var chunk in distinctIds.Chunk(500))
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM ImageTag
                    WHERE ImageMetaId IN @Ids AND TagId = @TagId",
                    new { Ids = chunk, TagId = tagId.Value }, txn);
            }
        }

        txn.Commit();
    }

    public async Task ClearTagsAndStatusBatchAsync(List<string> filePaths)
    {
        if (filePaths == null || filePaths.Count == 0) return;

        var distinctPaths = filePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        foreach (var chunk in distinctPaths.Chunk(500))
        {
            await conn.ExecuteAsync(
                "DELETE FROM ImageTag WHERE ImageMetaId IN (SELECT Id FROM ImageMeta WHERE FilePath COLLATE NOCASE IN @Paths)",
                new { Paths = chunk }, txn);
            await conn.ExecuteAsync(
                "UPDATE ImageMeta SET AutoTagStatus = 0 WHERE FilePath COLLATE NOCASE IN @Paths",
                new { Paths = chunk }, txn);
        }

        txn.Commit();
    }

    public async Task ClearTagsFromImagesAsync(List<long> imageIds)
    {
        if (imageIds == null || imageIds.Count == 0) return;
        var distinctIds = imageIds.Distinct().ToList();
        if (distinctIds.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var chunk in distinctIds.Chunk(500))
        {
            await conn.ExecuteAsync(
                "DELETE FROM ImageTag WHERE ImageMetaId IN @Ids",
                new { Ids = chunk }, txn);
        }
        txn.Commit();
    }
}
