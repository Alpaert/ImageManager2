using System.Security.Cryptography;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Video;

/// <summary>视频媒体处理器 — 原分辨率帧缓存 + SkiaSharp 缩放</summary>
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
        var originalPath = await EnsureOriginalFrameCachedAsync(filePath, ct);
        if (originalPath == null)
        {
            // fallback: 旧逻辑直接生成
            var result = await VideoThumbnailGenerator.GenerateAsync(filePath, decodeWidth, ct);
            if (result == null) return null;
            return new MediaResult
            {
                Data = result.ThumbnailData ?? Array.Empty<byte>(),
                Width = result.Width,
                Height = result.Height
            };
        }

        var thumbnailData = ThumbnailGenerator.Generate(originalPath, decodeWidth);
        if (thumbnailData == null) return null;

        var (w, h) = ThumbnailGenerator.GetDimensions(originalPath);
        return new MediaResult { Data = thumbnailData, Width = w, Height = h };
    }

    public (int Width, int Height) GetDimensions(string filePath)
    {
        // 优先从缓存的原分辨率帧读取
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

    private async Task<string?> EnsureOriginalFrameCachedAsync(string filePath, CancellationToken ct)
    {
        var cachePath = GetOriginalFramePath(filePath);
        if (File.Exists(cachePath))
            return cachePath;

        var frameData = await VideoThumbnailGenerator.ExtractOriginalFrameAsync(filePath, ct);
        if (frameData == null || frameData.Length == 0)
            return null;

        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(cachePath, frameData, ct);
            return cachePath;
        }
        catch
        {
            return null;
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