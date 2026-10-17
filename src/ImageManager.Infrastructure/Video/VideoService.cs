using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Video;

public class VideoService : IVideoService
{
    public async Task<byte[]?> ExtractThumbnailAsync(string filePath, int maxWidth)
    {
        var result = await VideoThumbnailGenerator.GenerateAsync(filePath, maxWidth);
        return result?.ThumbnailData;
    }

    public (int Width, int Height) GetVideoDimensions(string filePath)
    {
        var task = VideoMetadataExtractor.ExtractMetadataAsync(filePath);
        var meta = task.GetAwaiter().GetResult();
        return meta.HasValue ? (meta.Value.Width, meta.Value.Height) : (0, 0);
    }

    public Task<(int Width, int Height)?> TryGetDisplayDimensionsAsync(string filePath, CancellationToken ct = default)
        => VideoThumbnailGenerator.TryGetDisplayDimensionsAsync(filePath, ct);
}
