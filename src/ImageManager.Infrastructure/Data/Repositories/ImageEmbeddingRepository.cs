using Dapper;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public sealed class ImageEmbeddingRepository : IImageEmbeddingRepository
{
    private readonly IDbContextFactory _dbFactory;

    public ImageEmbeddingRepository(IDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
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

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        var inserted = 0;

        foreach (var (imageMetaId, tagName) in matches)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                continue;

            var trimmed = tagName.Trim();
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
}
