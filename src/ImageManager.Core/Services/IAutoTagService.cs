namespace ImageManager.Core.Services;

public readonly record struct AutoTagProgress(int Processed, int Total, string StatusText);

public readonly record struct TagPrediction(string TagName, double Confidence);

public interface IAutoTagService
{
    event Action<AutoTagProgress>? ProgressChanged;
    bool IsModelLoaded { get; }
    Task LoadModelAsync(string modelPath);
    Task<List<TagPrediction>> PredictAsync(string imagePath);
}
