namespace ImageManager.Core.Services;

// ==================== Enums ====================

public enum TagMode
{
    SingleModel = 0,  // PixAI 单模型全量打标
    Ensemble = 1       // 三模型专家流水线（WD Rating + PixAI general/character + Camie artist/copyright）
}

public enum SystemRating
{
    Unknown = -1,
    General = 0,
    Sensitive = 1,
    Questionable = 2,
    Explicit = 3
}

// ==================== Result Records ====================

public readonly record struct EnsembleResult(
    SystemRating Rating,
    List<TagPrediction> MergedTags,
    Dictionary<string, List<TagPrediction>>? SourceTags
)
{
    public string? ArtistName { get; init; }
    public double ArtistConfidence { get; init; }
}

public readonly record struct ModelStatus(
    string ModelName,
    bool IsLoaded,
    string Version,
    int TagCount
);

// ==================== Merge Config ====================

public class MergeConfig
{
    public int MaxTags { get; set; } = 75;
    public Dictionary<string, double> ModelThresholds { get; set; } = new()
    {
        ["pixai"] = 0.30
    };
    /// <summary>画师嵌入匹配余弦相似度阈值（默认 0.35）</summary>
    public double ArtistMatchThreshold { get; set; } = 0.35;
}

// ==================== Interface ====================

public interface IEnsembleTagService : IAutoTagService
{
    TagMode Mode { get; }
    Task<EnsembleResult> PredictWithSourcesAsync(string imagePath);
    Task<SystemRating> PredictRatingAsync(string imagePath);
    IReadOnlyList<ModelStatus> GetModelStatuses();
}
