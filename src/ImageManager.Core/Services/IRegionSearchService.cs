using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IRegionSearchService
{
    // Optional bounded model keep-alive. Creating a session must not load a model.
    // Disposing it must not synchronously release native inference resources.
    IDisposable? BeginSession() => null;

    // Coordinates refer to the first frame's raw raster. A null region uses the whole image.
    Task<RegionSearchResponse> SearchAsync(
        string imagePath,
        ImageRegion? region,
        IReadOnlyCollection<string> candidates,
        RegionSearchMode mode,
        int limit,
        CancellationToken ct = default);
}
