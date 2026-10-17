namespace ImageManager.Core.Models;

public record CharacterMatchRequest(
    string FolderPath,
    bool Recursive = true,
    double MatchThreshold = 0.35,
    int MaxMatchesPerImage = 1,
    int BatchSize = 100);

public record CharacterMatchProgress(
    int ProcessedCount,
    int TotalCount,
    int MatchedCount,
    int MissingVectorCount,
    int WrittenTagsCount,
    string Stage,
    long OperationId = 0);

public record CharacterMatchResult(
    int TotalImages,
    int WithEmbeddings,
    int MissingEmbeddings,
    int MatchedCount,
    int WrittenTagsCount,
    bool Completed,
    bool Cancelled,
    string? ErrorMessage = null);
