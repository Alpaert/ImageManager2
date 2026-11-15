using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;

namespace ImageManager.Infrastructure.Video;

public class VideoMediaProcessor(VideoOriginalFrameCacheService originalFrames) : IMediaProcessor
{
    public bool CanHandle(string extension) => FileTypeConstants.VideoExtensions.Contains(extension);
    public async Task<MediaResult?> ExtractThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct)
    {
        var frame = await originalFrames.LoadFrameAsync(filePath, ct).ConfigureAwait(false);
        if (frame.Entry?.Data == null) return null;
        var bytes = ThumbnailCacheService.SafeResize(frame.Entry.Data, decodeWidth);
        return bytes == null ? null : new MediaResult { Data = bytes, Width = frame.Entry.Width, Height = frame.Entry.Height };
    }
    // A synchronous request must never orphan an async FFmpeg probe.
    public (int Width, int Height) GetDimensions(string filePath) => originalFrames.TryGetDimensions(filePath);
    internal string GetOriginalFramePath(string filePath) => originalFrames.GetOriginalFramePath(filePath);
}
