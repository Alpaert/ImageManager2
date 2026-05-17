namespace ImageManager.Core.Services;

public interface ISimilarImageService
{
    Task<List<string>> FindSimilarAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        int threshold = 5,
        CancellationToken ct = default);
}
