using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ISimilarImageService
{
    Task<List<SimilaritySearchResult>> SearchByImageAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        int limit = 50,
        CancellationToken ct = default);

    Task<List<SimilaritySearchResult>> SearchByTextAsync(
        string query,
        IEnumerable<string> candidates,
        int limit = 50,
        CancellationToken ct = default);

}
