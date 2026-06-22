using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Video;

public class VideoMediaProcessor : IMediaProcessor
{
    private readonly VideoOriginalFrameCacheService _originalFrames;

    public VideoMediaProcessor(VideoOriginalFrameCacheService originalFrames)
    {
        _originalFrames = originalFrames;
    }

    public bool CanHandle(string extension)
        => FileTypeConstants.VideoExtensions.Contains(extension);

    public async Task<MediaResult?> ExtractThumbnailAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct)
    {
        var originalPath = GetOriginalFramePath(filePath);
        if (File.Exists(originalPath))
        {
            Common.Helpers.PerfLogger.Log($"[VideoCache] ORIGINAL FRAME HIT {Path.GetFileName(filePath)}");
            var thumbnailData = ThumbnailGenerator.Generate(originalPath, decodeWidth);
            if (thumbnailData != null)
            {
                var (w, h) = ThumbnailGenerator.GetDimensions(originalPath);
                return new MediaResult { Data = thumbnailData, Width = w, Height = h };
            }
        }

        if (!File.Exists(filePath))
            return null;

        var originalFrame = await _originalFrames.EnsureAsync(filePath, ct);
        if (originalFrame.Success && originalFrame.Path is not null && File.Exists(originalFrame.Path))
        {
            var thumbnailData = ThumbnailGenerator.Generate(originalFrame.Path, decodeWidth);
            if (thumbnailData != null)
            {
                var (w, h) = ThumbnailGenerator.GetDimensions(originalFrame.Path);
                return new MediaResult { Data = thumbnailData, Width = w, Height = h };
            }
        }

        Common.Helpers.PerfLogger.Log($"[VideoCache] FFMPEG EXTRACT fallback scaled {Path.GetFileName(filePath)}");
        var result = await VideoThumbnailGenerator.GenerateAsync(filePath, decodeWidth, ct);
        if (result == null) return null;

        return new MediaResult
        {
            Data = result.ThumbnailData ?? Array.Empty<byte>(),
            Width = result.Width,
            Height = result.Height
        };
    }

    public (int Width, int Height) GetDimensions(string filePath)
    {
        var cachedPath = GetOriginalFramePath(filePath);
        if (File.Exists(cachedPath))
            return ThumbnailGenerator.GetDimensions(cachedPath);

        if (!File.Exists(filePath))
            return (1920, 1080);

        try
        {
            var task = VideoThumbnailGenerator.GetDimensionsAsync(filePath);
            if (!task.Wait(TimeSpan.FromSeconds(10)))
                return (1920, 1080);
            return task.Result;
        }
        catch
        {
            return (1920, 1080);
        }
    }

    internal string GetOriginalFramePath(string filePath)
    {
        return _originalFrames.GetOriginalFramePath(filePath);
    }
}
