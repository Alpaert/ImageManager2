namespace ImageManager.Core.Services;

public readonly record struct MediaThumbnailPrewarmProgress(
    int Completed,
    int Total,
    int Cached,
    int Generated,
    int NoPreview,
    int SkippedVideos,
    int Failed,
    string? CurrentFileName);

public readonly record struct MediaThumbnailPrewarmResult(
    int Total,
    int Completed,
    int Cached,
    int Generated,
    int NoPreview,
    int SkippedVideos,
    int Failed);

/// <summary>
/// Warms media thumbnails for an already-resolved file set. Implementations must
/// never extract a missing video original frame as part of this operation.
/// </summary>
public interface IMediaThumbnailPrewarmService
{
    Task<MediaThumbnailPrewarmResult> PrewarmAsync(
        IReadOnlyCollection<string> filePaths,
        int decodeWidth,
        IProgress<MediaThumbnailPrewarmProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
