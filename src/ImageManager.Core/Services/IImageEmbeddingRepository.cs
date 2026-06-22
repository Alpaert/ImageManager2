namespace ImageManager.Core.Services;

public sealed record ImageEmbeddingRecord(
    long ImageMetaId,
    string FilePath,
    string? FileHash,
    float[] Embedding);

public interface IImageEmbeddingRepository
{
    Task UpsertBatchAsync(
        IReadOnlyList<(long ImageMetaId, string? FileHash, float[] Embedding)> embeddings,
        string modelKey,
        string modelVersion);

    Task UpsertAsync(
        long imageMetaId,
        string? fileHash,
        float[] embedding,
        string modelKey,
        string modelVersion);

    Task<List<ImageEmbeddingRecord>> GetByFolderPrefixAsync(
        string folderPath,
        string modelKey,
        string modelVersion);

    Task<int> AddCharacterEmbeddingTagsAsync(
        IReadOnlyList<(long ImageMetaId, string TagName)> matches,
        string source);
}
