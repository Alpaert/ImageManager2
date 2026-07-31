using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public sealed record ImageEmbeddingRecord(
    long ImageMetaId,
    string FilePath,
    string? FileHash,
    float[] Embedding);

public sealed record SearchIndexCandidate(
    long ImageMetaId,
    string FilePath,
    long FileSize,
    long LastWriteTicks);

public sealed record SearchEmbeddingWrite(
    long ImageMetaId,
    long SourceFileSize,
    long SourceLastWriteTicks,
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

    Task UpsertSearchBatchAsync(
        IReadOnlyList<SearchEmbeddingWrite> embeddings,
        string modelKey,
        string modelVersion);

    Task<List<ImageEmbeddingRecord>> GetValidSearchEmbeddingsByPathsAsync(
        string modelKey,
        string modelVersion,
        IReadOnlyCollection<string> filePaths,
        CancellationToken ct = default);

    Task<List<SearchIndexCandidate>> GetSearchIndexCandidatesAsync(VectorIndexScope scope);

    Task<int> GetValidSearchEmbeddingCountAsync(
        string modelKey,
        string modelVersion);

    Task<HashSet<long>> GetValidSearchEmbeddingIdsAsync(
        string modelKey,
        string modelVersion);

    Task DeleteModelAsync(string modelKey, string modelVersion);

    Task DeleteModelForImagesAsync(
        string modelKey,
        string modelVersion,
        IReadOnlyCollection<long> imageMetaIds);

    Task<int> AddCharacterEmbeddingTagsAsync(
        IReadOnlyList<(long ImageMetaId, string TagName)> matches,
        string source);
}
