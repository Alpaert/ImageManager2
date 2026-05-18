using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data.Repositories;

public class ImageMetaRepository : IImageMetaRepository
{
    private readonly AppDbContext _db;

    public ImageMetaRepository(AppDbContext db) => _db = db;

    public async Task<ImageMeta?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateConnection();
        var meta = await conn.QuerySingleOrDefaultAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE Id = @Id", new { Id = id });
        if (meta != null)
            meta.Tags = await GetTagsForMetaAsync(conn, meta.Id);
        return meta;
    }

    public async Task<ImageMeta?> GetByPathAsync(string filePath)
    {
        using var conn = _db.CreateConnection();
        var meta = await conn.QuerySingleOrDefaultAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FilePath = filePath });
        if (meta != null)
            meta.Tags = await GetTagsForMetaAsync(conn, meta.Id);
        return meta;
    }

    public async Task<List<ImageMeta>> GetByFolderAsync(string folderPath)
    {
        using var conn = _db.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        var metas = (await conn.QueryAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" })).ToList();

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

    public async Task<List<ImageMeta>> GetByFolderIdAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        var metas = (await conn.QueryAsync<ImageMeta>(
            "SELECT * FROM ImageMeta WHERE FolderId = @FolderId",
            new { FolderId = folderId })).ToList();

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

    public async Task<int> CountByFolderIdAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ImageMeta WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task SetFolderIdAsync(string filePath, long folderId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET FolderId = @FolderId WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FolderId = folderId, FilePath = filePath });
    }

    public async Task<List<ImageMeta>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
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
        using var conn = _db.CreateConnection();
        using var txn = conn.BeginTransaction();

        var existing = await conn.QuerySingleOrDefaultAsync<ImageMeta>(
            "SELECT Id FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { meta.FilePath }, txn);

        if (existing != null)
        {
            meta.Id = existing.Id;
            meta.UpdatedAt = DateTime.UtcNow;
            await conn.ExecuteAsync(@"
                UPDATE ImageMeta SET
                    FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                    Width = @Width, Height = @Height,
                    FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                    FolderId = @FolderId, UpdatedAt = @UpdatedAt
                WHERE Id = @Id", meta, txn);
        }
        else
        {
            meta.CreatedAt = DateTime.UtcNow;
            meta.UpdatedAt = DateTime.UtcNow;
            meta.Id = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO ImageMeta (FilePath, FileHash, PerceptualHash, Width, Height,
                    FileSize, LastWriteTicks, FolderId, CreatedAt, UpdatedAt)
                VALUES (@FilePath, @FileHash, @PerceptualHash, @Width, @Height,
                    @FileSize, @LastWriteTicks, @FolderId, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();", meta, txn);
        }

        txn.Commit();
        return meta.Id;
    }

    public async Task BulkUpsertAsync(List<ImageMeta> metas)
    {
        if (metas.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var txn = conn.BeginTransaction();

        // Single query to find existing paths
        var paths = metas.Select(m => m.FilePath).Distinct().ToList();
        var existing = await conn.QueryAsync<(string FilePath, long Id)>(
            "SELECT FilePath, Id FROM ImageMeta WHERE FilePath IN @Paths",
            new { Paths = paths }, txn);

        var existingMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fp, id) in existing)
            existingMap[fp] = id;

        var now = DateTime.UtcNow;
        foreach (var meta in metas)
        {
            if (existingMap.TryGetValue(meta.FilePath, out var id))
            {
                meta.Id = id;
                meta.UpdatedAt = now;
                await conn.ExecuteAsync(@"
                    UPDATE ImageMeta SET
                        FileHash = @FileHash, PerceptualHash = @PerceptualHash,
                        Width = @Width, Height = @Height,
                        FileSize = @FileSize, LastWriteTicks = @LastWriteTicks,
                        FolderId = @FolderId, UpdatedAt = @UpdatedAt
                    WHERE Id = @Id", meta, txn);
            }
            else
            {
                meta.CreatedAt = now;
                meta.UpdatedAt = now;
                meta.Id = await conn.ExecuteScalarAsync<long>(@"
                    INSERT INTO ImageMeta (FilePath, FileHash, PerceptualHash, Width, Height,
                        FileSize, LastWriteTicks, FolderId, CreatedAt, UpdatedAt)
                    VALUES (@FilePath, @FileHash, @PerceptualHash, @Width, @Height,
                        @FileSize, @LastWriteTicks, @FolderId, @CreatedAt, @UpdatedAt);
                    SELECT last_insert_rowid();", meta, txn);
            }
        }

        txn.Commit();
    }

    public async Task<int> DeleteAsync(long id)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteAsync("DELETE FROM ImageMeta WHERE Id = @Id", new { Id = id });
    }

    public async Task<int> DeleteByPathAsync(string filePath)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM ImageMeta WHERE FilePath = @FilePath COLLATE NOCASE",
            new { FilePath = filePath });
    }

    public async Task<int> DeleteByFolderAsync(string folderPath)
    {
        using var conn = _db.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        return await conn.ExecuteAsync(
            "DELETE FROM ImageMeta WHERE FilePath LIKE @Prefix",
            new { Prefix = normalized + "%" });
    }

    public async Task SetTagsAsync(long imageId, List<string> tags)
    {
        using var conn = _db.CreateConnection();
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

    public async Task<List<TagCount>> GetTagCountsAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<TagCount>(@"
            SELECT t.Name, COUNT(it.ImageMetaId) as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            GROUP BY t.Id, t.Name
            ORDER BY Count DESC, t.Name");
        return results.ToList();
    }

    public async Task<List<string>> GetFilePathsByTagAsync(string tagName)
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<string>(@"
            SELECT DISTINCT im.FilePath
            FROM ImageMeta im
            INNER JOIN ImageTag it ON im.Id = it.ImageMetaId
            INNER JOIN Tag t ON it.TagId = t.Id
            WHERE t.Name = @TagName COLLATE NOCASE
            ORDER BY im.FilePath",
            new { TagName = tagName });
        return results.ToList();
    }

    public async Task<List<string>> GetFilePathsByTagsAsync(List<string> tagNames, bool requireAll)
    {
        using var conn = _db.CreateConnection();
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
                HAVING COUNT(DISTINCT t.Name) = @TagCount
                ORDER BY im.FilePath",
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
                WHERE t.Name IN @TagNames COLLATE NOCASE
                ORDER BY im.FilePath",
                new { TagNames = tagNames });
            return results.ToList();
        }
    }

    public async Task<List<string>> GetFilePathsByTagsExcludingAsync(
        List<string> includeTags, bool requireAll, List<string> excludeTags)
    {
        using var conn = _db.CreateConnection();
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
                HAVING COUNT(DISTINCT t.Name) = @IncludeCount
                ORDER BY im.FilePath",
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
                  )
                ORDER BY im.FilePath",
                new { IncludeTags = includeTags, ExcludeTags = excludeTags });
            return results.ToList();
        }
    }

    public async Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null)
    {
        using var conn = _db.CreateConnection();
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

        sql += "\n            ORDER BY im.FilePath";

        var results = await conn.QueryAsync<string>(sql,
            new { BaseTags = baseTags, EachTags = eachTags, ExcludeTags = excludeTags ?? new List<string>(), BaseCount = baseTags.Count });
        return results.ToList();
    }

    public async Task<List<string>> GetFilePathsWithNoTagsAsync()
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT im.FilePath
            FROM ImageMeta im
            WHERE im.Id NOT IN (SELECT DISTINCT ImageMetaId FROM ImageTag)
            ORDER BY im.FilePath";
        var result = await conn.QueryAsync<string>(sql);
        return result.AsList();
    }

    public async Task<List<TagCount>> GetCoOccurringTagsAsync(List<string> filePaths, List<string>? excludeNames = null)
    {
        if (filePaths.Count == 0) return new List<TagCount>();
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT t.Name, COUNT(DISTINCT it.ImageMetaId) as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            INNER JOIN ImageMeta im ON it.ImageMetaId = im.Id
            WHERE im.FilePath IN @FilePaths";
        if (excludeNames is { Count: > 0 })
            sql += "\n              AND t.Name NOT IN @ExcludeNames COLLATE NOCASE";
        sql += "\n            GROUP BY t.Id, t.Name\n            ORDER BY Count DESC, t.Name";

        var results = await conn.QueryAsync<TagCount>(sql,
            new { FilePaths = filePaths, ExcludeNames = excludeNames ?? new List<string>() });
        return results.ToList();
    }

    public async Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths)
    {
        if (filePaths.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<(string FilePath, string PerceptualHash)>(@"
            SELECT FilePath, PerceptualHash FROM ImageMeta WHERE FilePath IN @Paths",
            new { Paths = filePaths });

        return rows
            .Where(r => !string.IsNullOrEmpty(r.PerceptualHash))
            .ToDictionary(r => r.FilePath, r => r.PerceptualHash, StringComparer.OrdinalIgnoreCase);
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

    private async Task<Dictionary<long, List<TagCount>>> GetTagMapAsync(SqliteConnection conn)
    {
        var rows = await conn.QueryAsync<(long ImageMetaId, string Name)>(@"
            SELECT it.ImageMetaId, t.Name
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            ORDER BY t.Name");

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
}
