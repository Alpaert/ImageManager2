using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

/// <summary>
/// Service contract for constructing artist or character embedding libraries
/// with temporary storage isolation, atomic replacement, and cancellation support.
/// </summary>
public interface ILibraryBuilderService
{
    Task<LibraryBuildResult> BuildLibraryAsync(
        LibraryBuildRequest request,
        IProgress<LibraryBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
