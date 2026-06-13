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
        // 使用 Task.Run 避免死锁
        return Task.Run(async () =>
        {
            var metadata = await VideoMetadataExtractor.ExtractMetadataAsync(filePath, CancellationToken.None);
            return metadata.HasValue ? (metadata.Value.Width, metadata.Value.Height) : (0, 0);
        }).GetAwaiter().GetResult();
    }
}
