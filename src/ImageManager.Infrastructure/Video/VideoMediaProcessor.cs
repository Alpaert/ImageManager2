using ImageManager.Common.Constants;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Video;

/// <summary>视频媒体处理器</summary>
public class VideoMediaProcessor : IMediaProcessor
{
    public bool CanHandle(string extension)
        => FileTypeConstants.VideoExtensions.Contains(extension);

    public async Task<MediaResult?> ExtractThumbnailAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct)
    {
        var result = await VideoThumbnailGenerator.GenerateAsync(filePath, decodeWidth, ct);
        if (result == null) return null;

        return new MediaResult
        {
            Data = result.ThumbnailData ?? Array.Empty<byte>(),
            Width = result.Width,
            Height = result.Height,
            Duration = result.Duration,
            Timestamp = result.ThumbnailTimestamp
        };
    }

    public (int Width, int Height) GetDimensions(string filePath)
    {
        // 使用带超时的同步等待，避免无限期阻塞线程
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
}
