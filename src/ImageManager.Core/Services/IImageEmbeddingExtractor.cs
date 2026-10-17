namespace ImageManager.Core.Services;

/// <summary>
/// Abstraction for model-based image embedding extraction.
/// </summary>
public interface IImageEmbeddingExtractor
{
    bool IsModelLoaded { get; }
    Task LoadModelAsync(string modelPath, CancellationToken ct = default);
    Task<float[]?> GetEmbeddingAsync(string imagePath);
    Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> imagePaths);
}
