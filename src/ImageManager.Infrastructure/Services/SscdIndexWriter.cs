using Dapper;
using ImageManager.Infrastructure.Data;

namespace ImageManager.Infrastructure.Services;

public sealed record SscdIndexWrite(
    long ImageMetaId, string FilePath, long FileSize, long LastWriteTicks, float[] Embedding,
    string? ExpectedHash = null);

public sealed class SscdIndexWriter(IDbContextFactory dbFactory)
{
    private sealed record SourceRow(string FilePath, long FileSize, long LastWriteTicks, string? FileHash);

    internal async Task<(string FilePath, long FileSize, long LastWriteTicks, string? FileHash)?> GetSourceAsync(
        long id, CancellationToken ct)
    {
        using var connection = dbFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SourceRow>(new CommandDefinition("""
            SELECT FilePath, FileSize, LastWriteTicks, FileHash FROM ImageMeta WHERE Id = @Id
            """, new { Id = id }, cancellationToken: ct));
        return row is null ? null : (row.FilePath, row.FileSize, row.LastWriteTicks, row.FileHash);
    }

    internal async Task<HashSet<long>> GetValidIdsAsync(CancellationToken ct = default)
    {
        using var connection = dbFactory.CreateConnection();
        var ids = await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT im.Id
            FROM ImageEmbedding ie
            INNER JOIN ImageMeta im ON im.Id = ie.ImageMetaId
            WHERE ie.ModelKey = @ModelKey AND ie.ModelVersion = @ModelVersion
              AND ie.SourceFileSize = im.FileSize AND ie.SourceLastWriteTicks = im.LastWriteTicks
              AND ie.FileHash IS im.FileHash COLLATE NOCASE
              AND ie.EmbeddingDim = @Dimension AND length(ie.EmbeddingBlob) = @ByteCount
            """, new
        {
            SscdEmbeddingService.ModelKey, SscdEmbeddingService.ModelVersion, SscdEmbeddingService.Dimension,
            ByteCount = SscdEmbeddingService.Dimension * sizeof(float)
        }, cancellationToken: ct));
        return ids.ToHashSet();
    }

    public Task<int> WriteAsync(IReadOnlyList<SscdIndexWrite> batch, CancellationToken ct = default)
        => WriteAsync(batch, replaceExisting: false, ct);

    internal async Task<int> WriteAsync(
        IReadOnlyList<SscdIndexWrite> batch, bool replaceExisting, CancellationToken ct)
    {
        if (batch.Count == 0)
            return 0;

        using var connection = dbFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var sources = new List<FileStream>(batch.Count);
        try
        {
            var written = 0;
            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (!RegionSearchService.IsUsableVector(item.Embedding, SscdEmbeddingService.Dimension))
                    continue;

                FileStream? source = null;
                try
                {
                    source = File.Open(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length != item.FileSize ||
                        File.GetLastWriteTimeUtc(item.FilePath).Ticks != item.LastWriteTicks)
                        continue;

                    // Retain the read lock until SQLite publishes the batch.
                    sources.Add(source);
                    source = null;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                finally { source?.Dispose(); }

                var blob = new byte[item.Embedding.Length * sizeof(float)];
                Buffer.BlockCopy(item.Embedding, 0, blob, 0, blob.Length);
                written += await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ImageEmbedding
                        (ImageMetaId, ModelKey, ModelVersion, EmbeddingDim, EmbeddingBlob,
                         FileHash, SourceFileSize, SourceLastWriteTicks, UpdatedAt)
                    SELECT Id, @ModelKey, @ModelVersion, @Dimension, @Blob,
                           FileHash, FileSize, LastWriteTicks, @UpdatedAt
                    FROM ImageMeta
                    WHERE Id = @ImageMetaId AND FilePath = @FilePath COLLATE NOCASE
                      AND FileSize = @FileSize AND LastWriteTicks = @LastWriteTicks
                      AND FileHash IS @ExpectedHash COLLATE NOCASE
                    ON CONFLICT(ImageMetaId, ModelKey, ModelVersion) DO UPDATE SET
                        EmbeddingDim = excluded.EmbeddingDim,
                        EmbeddingBlob = excluded.EmbeddingBlob,
                        FileHash = excluded.FileHash,
                        SourceFileSize = excluded.SourceFileSize,
                        SourceLastWriteTicks = excluded.SourceLastWriteTicks,
                        UpdatedAt = excluded.UpdatedAt
                    WHERE @ReplaceExisting = 1
                       OR ImageEmbedding.SourceFileSize IS NOT excluded.SourceFileSize
                       OR ImageEmbedding.SourceLastWriteTicks IS NOT excluded.SourceLastWriteTicks
                       OR ImageEmbedding.FileHash IS NOT excluded.FileHash COLLATE NOCASE
                       OR ImageEmbedding.EmbeddingDim <> excluded.EmbeddingDim
                       OR length(ImageEmbedding.EmbeddingBlob) <> length(excluded.EmbeddingBlob)
                    """, new
                {
                    item.ImageMetaId, item.FilePath, item.FileSize, item.LastWriteTicks, item.ExpectedHash,
                    SscdEmbeddingService.ModelKey, SscdEmbeddingService.ModelVersion,
                    SscdEmbeddingService.Dimension, Blob = blob, UpdatedAt = DateTime.UtcNow,
                    ReplaceExisting = replaceExisting
                }, transaction, cancellationToken: ct));
            }

            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return written;
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
        }
    }
}
