using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public sealed class ImageEmbeddingRepository : IImageEmbeddingRepository
{
    private readonly IDbContextFactory _dbFactory;
    private readonly ICharacterTagSuppressionRepository _suppressionRepo;

    public ImageEmbeddingRepository(
        IDbContextFactory dbFactory,
        ICharacterTagSuppressionRepository suppressionRepo)
    {
        _dbFactory = dbFactory;
        _suppressionRepo = suppressionRepo;
    }

    public async Task UpsertBatchAsync(
        IReadOnlyList<(long ImageMetaId, string? FileHash, float[] Embedding)> embeddings,
        string modelKey,
        string modelVersion)
    {
        if (embeddings.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        foreach (var item in embeddings)
        {
            if (item.Embedding.Length == 0)
                continue;

            await conn.ExecuteAsync(@"
                INSERT INTO ImageEmbedding
                    (ImageMetaId, ModelKey, ModelVersion, EmbeddingDim, EmbeddingBlob, FileHash, UpdatedAt)
                VALUES
                    (@ImageMetaId, @ModelKey, @ModelVersion, @EmbeddingDim, @EmbeddingBlob, @FileHash, @UpdatedAt)
                ON CONFLICT(ImageMetaId, ModelKey, ModelVersion) DO UPDATE SET
                    EmbeddingDim = excluded.EmbeddingDim,
                    EmbeddingBlob = excluded.EmbeddingBlob,
                    FileHash = excluded.FileHash,
                    UpdatedAt = excluded.UpdatedAt",
                new
                {
                    item.ImageMetaId,
                    ModelKey = modelKey,
                    ModelVersion = modelVersion,
                    EmbeddingDim = item.Embedding.Length,
                    EmbeddingBlob = ToBytes(item.Embedding),
                    item.FileHash,
                    UpdatedAt = now
                },
                txn);
        }

        txn.Commit();
    }

    public Task UpsertAsync(
        long imageMetaId,
        string? fileHash,
        float[] embedding,
        string modelKey,
        string modelVersion)
    {
        return UpsertBatchAsync(
            new[] { (imageMetaId, fileHash, embedding) },
            modelKey,
            modelVersion);
    }

    public async Task UpsertSearchBatchAsync(
        IReadOnlyList<SearchEmbeddingWrite> embeddings,
        string modelKey,
        string modelVersion)
    {
        if (embeddings.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        foreach (var item in embeddings)
        {
            if (item.Embedding.Length == 0)
                continue;

            await conn.ExecuteAsync(@"
                INSERT INTO ImageEmbedding
                    (ImageMetaId, ModelKey, ModelVersion, EmbeddingDim, EmbeddingBlob,
                     SourceFileSize, SourceLastWriteTicks, UpdatedAt)
                VALUES
                    (@ImageMetaId, @ModelKey, @ModelVersion, @EmbeddingDim, @EmbeddingBlob,
                     @SourceFileSize, @SourceLastWriteTicks, @UpdatedAt)
                ON CONFLICT(ImageMetaId, ModelKey, ModelVersion) DO UPDATE SET
                    EmbeddingDim = excluded.EmbeddingDim,
                    EmbeddingBlob = excluded.EmbeddingBlob,
                    SourceFileSize = excluded.SourceFileSize,
                    SourceLastWriteTicks = excluded.SourceLastWriteTicks,
                    UpdatedAt = excluded.UpdatedAt",
                new
                {
                    item.ImageMetaId,
                    ModelKey = modelKey,
                    ModelVersion = modelVersion,
                    EmbeddingDim = item.Embedding.Length,
                    EmbeddingBlob = ToBytes(item.Embedding),
                    item.SourceFileSize,
                    item.SourceLastWriteTicks,
                    UpdatedAt = now
                },
                txn);
        }

        txn.Commit();
    }

    public async Task<List<ImageEmbeddingRecord>> GetValidSearchEmbeddingsByPathsAsync(
        string modelKey,
        string modelVersion,
        IReadOnlyCollection<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count == 0)
            return [];

        using var conn = _dbFactory.CreateConnection();
        var command = new CommandDefinition(@"
            SELECT ie.ImageMetaId, im.FilePath, ie.FileHash, ie.EmbeddingDim, ie.EmbeddingBlob
            FROM ImageEmbedding ie
            INNER JOIN ImageMeta im ON im.Id = ie.ImageMetaId
            WHERE ie.ModelKey = @ModelKey
              AND ie.ModelVersion = @ModelVersion
              AND ie.SourceFileSize = im.FileSize
              AND ie.SourceLastWriteTicks = im.LastWriteTicks
              AND im.FilePath IN @FilePaths",
            new { ModelKey = modelKey, ModelVersion = modelVersion, FilePaths = filePaths },
            cancellationToken: ct);
        var rows = await conn.QueryAsync<(
            long ImageMetaId,
            string FilePath,
            string? FileHash,
            int EmbeddingDim,
            byte[] EmbeddingBlob)>(command);

        var result = new List<ImageEmbeddingRecord>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = FromBytes(row.EmbeddingBlob, row.EmbeddingDim);
            if (embedding.Length > 0)
                result.Add(new ImageEmbeddingRecord(row.ImageMetaId, row.FilePath, row.FileHash, embedding));
        }

        return result;
    }

    public async Task<List<SearchIndexCandidate>> GetSearchIndexCandidatesAsync(VectorIndexScope scope)
    {
        using var conn = _dbFactory.CreateConnection();
        var sql = @"
            SELECT Id AS ImageMetaId, FilePath, FileSize, LastWriteTicks
            FROM ImageMeta
            WHERE FilePath IS NOT NULL";
        object parameters = new { };
        if (!scope.IsAll)
        {
            var root = Path.GetFullPath(scope.FolderPath!)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = EscapeLike(root + Path.DirectorySeparatorChar) + "%";
            sql += " AND FilePath LIKE @Prefix ESCAPE '\\' COLLATE NOCASE";
            parameters = new { Prefix = prefix };
        }
        sql += " ORDER BY Id";
        var rows = await conn.QueryAsync<SearchIndexCandidate>(sql, parameters);
        return rows.ToList();
    }

    public async Task<int> GetValidSearchEmbeddingCountAsync(
        string modelKey,
        string modelVersion)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM ImageEmbedding ie
            INNER JOIN ImageMeta im ON im.Id = ie.ImageMetaId
            WHERE ie.ModelKey = @ModelKey
              AND ie.ModelVersion = @ModelVersion
              AND ie.SourceFileSize = im.FileSize
              AND ie.SourceLastWriteTicks = im.LastWriteTicks",
            new { ModelKey = modelKey, ModelVersion = modelVersion });
    }

    public async Task<HashSet<long>> GetValidSearchEmbeddingIdsAsync(
        string modelKey,
        string modelVersion)
    {
        using var conn = _dbFactory.CreateConnection();
        var rows = await conn.QueryAsync<long>(@"
            SELECT ie.ImageMetaId
            FROM ImageEmbedding ie
            INNER JOIN ImageMeta im ON im.Id = ie.ImageMetaId
            WHERE ie.ModelKey = @ModelKey
              AND ie.ModelVersion = @ModelVersion
              AND ie.SourceFileSize = im.FileSize
              AND ie.SourceLastWriteTicks = im.LastWriteTicks",
            new { ModelKey = modelKey, ModelVersion = modelVersion });
        return rows.ToHashSet();
    }

    public async Task DeleteModelAsync(string modelKey, string modelVersion)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            DELETE FROM ImageEmbedding
            WHERE ModelKey = @ModelKey AND ModelVersion = @ModelVersion",
            new { ModelKey = modelKey, ModelVersion = modelVersion });
    }

    public async Task DeleteModelForImagesAsync(
        string modelKey,
        string modelVersion,
        IReadOnlyCollection<long> imageMetaIds)
    {
        if (imageMetaIds.Count == 0)
            return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var chunk in imageMetaIds.Chunk(500))
        {
            await conn.ExecuteAsync(@"
                DELETE FROM ImageEmbedding
                WHERE ModelKey = @ModelKey
                  AND ModelVersion = @ModelVersion
                  AND ImageMetaId IN @ImageMetaIds",
                new { ModelKey = modelKey, ModelVersion = modelVersion, ImageMetaIds = chunk },
                txn);
        }
        txn.Commit();
    }

    public async Task<List<ImageEmbeddingRecord>> GetByFolderPrefixAsync(
        string folderPath,
        string modelKey,
        string modelVersion)
    {
        using var conn = _dbFactory.CreateConnection();
        var normalized = Common.Helpers.PathHelper.NormalizeFolderPath(folderPath);
        var rows = await conn.QueryAsync<(long ImageMetaId, string FilePath, string? FileHash, int EmbeddingDim, byte[] EmbeddingBlob)>(@"
            SELECT ie.ImageMetaId, im.FilePath, ie.FileHash, ie.EmbeddingDim, ie.EmbeddingBlob
            FROM ImageEmbedding ie
            INNER JOIN ImageMeta im ON im.Id = ie.ImageMetaId
            WHERE ie.ModelKey = @ModelKey
              AND ie.ModelVersion = @ModelVersion
              AND (im.FilePath = @Root OR im.FilePath LIKE @Prefix)",
            new
            {
                ModelKey = modelKey,
                ModelVersion = modelVersion,
                Root = folderPath,
                Prefix = normalized + "%"
            });

        var result = new List<ImageEmbeddingRecord>();
        foreach (var row in rows)
        {
            var embedding = FromBytes(row.EmbeddingBlob, row.EmbeddingDim);
            if (embedding.Length > 0)
                result.Add(new ImageEmbeddingRecord(row.ImageMetaId, row.FilePath, row.FileHash, embedding));
        }

        return result;
    }

    public async Task<int> AddCharacterEmbeddingTagsAsync(
        IReadOnlyList<(long ImageMetaId, string TagName)> matches,
        string source)
    {
        if (matches.Count == 0) return 0;

        var suppressedByImage = await _suppressionRepo.GetSuppressedTagsAsync(
            matches.Select(m => m.ImageMetaId));

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var inserted = 0;

        foreach (var (imageMetaId, tagName) in matches)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                continue;

            var trimmed = tagName.Trim();
            if (suppressedByImage.TryGetValue(imageMetaId, out var suppressed) &&
                suppressed.Contains(trimmed))
                continue;

            var tagId = await conn.ExecuteScalarAsync<long?>(@"
                INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
                SELECT Id FROM Tag WHERE Name = @Name;",
                new { Name = trimmed },
                txn);

            if (!tagId.HasValue)
                continue;

            var changed = await conn.ExecuteAsync(@"
                INSERT OR IGNORE INTO ImageTag (ImageMetaId, TagId, Source)
                VALUES (@ImageMetaId, @TagId, @Source)",
                new { ImageMetaId = imageMetaId, TagId = tagId.Value, Source = source },
                txn);
            inserted += changed;
        }

        txn.Commit();
        return inserted;
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes, int dim)
    {
        if (bytes.Length != dim * sizeof(float))
            return Array.Empty<float>();

        var values = new float[dim];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
