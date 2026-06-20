using System.Security.Cryptography;
using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Video;

public class VideoMediaProcessor : IMediaProcessor
{
    private readonly string _cacheDirectory;

    public VideoMediaProcessor(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
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

        var originalFrame = await VideoThumbnailGenerator.ExtractOriginalFrameAsync(filePath, ct);
        if (originalFrame is { Length: > 0 })
        {
            try
            {
                var dir = Path.GetDirectoryName(originalPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(originalPath, originalFrame, ct);
                Common.Helpers.PerfLogger.Log($"[VideoCache] FFMPEG EXTRACT saved original {Path.GetFileName(filePath)}");

                var thumbnailData = ThumbnailGenerator.Generate(originalPath, decodeWidth);
                if (thumbnailData != null)
                {
                    var (w, h) = ThumbnailGenerator.GetDimensions(originalPath);
                    return new MediaResult { Data = thumbnailData, Width = w, Height = h };
                }
            }
            catch
            {
                try { if (File.Exists(originalPath)) File.Delete(originalPath); } catch { }
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
        var folderHash = GetFolderHash(filePath);
        var fileHash = GetFileHash(filePath);
        return Path.Combine(_cacheDirectory, "video_originals", folderHash, fileHash + ".jpg");
    }

    private static string GetFolderHash(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "_root";
        var hashBytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..8];
    }

    private static string GetFileHash(string filePath)
    {
        var hashBytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
