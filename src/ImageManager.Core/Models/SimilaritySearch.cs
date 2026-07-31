namespace ImageManager.Core.Models;

public enum SimilaritySearchMode
{
    Perceptual = 0,
    Semantic = 1,
    Atmosphere = 2,
    Color = 3
}

public enum PerceptualSearchResultMode
{
    Jump = 0,
    Ranked = 1
}

public enum SimilarityMatchKind
{
    Standard = 0,
    PerceptualStrict = 1,
    PerceptualFallback = 2
}

public sealed record SimilaritySearchResult(
    string FilePath,
    float Score,
    SimilarityMatchKind MatchKind = SimilarityMatchKind.Standard);

public enum VectorIndexKind
{
    Semantic = 1,
    Atmosphere = 2,
    Color = 3
}

public sealed record VectorIndexScope(string? FolderPath, bool IncludeSubfolders = true)
{
    public static VectorIndexScope All { get; } = new(null, true);
    public bool IsAll => string.IsNullOrWhiteSpace(FolderPath);
}

public sealed record VectorIndexProgress(
    VectorIndexKind Kind,
    int Total,
    int Processed,
    int Generated,
    int Skipped,
    int Failed,
    string? CurrentFile,
    bool IsPaused,
    string? Error);

public sealed record VectorIndexStatus(
    VectorIndexKind Kind,
    int TotalImages,
    int IndexedImages,
    int MissingOrStaleImages);
