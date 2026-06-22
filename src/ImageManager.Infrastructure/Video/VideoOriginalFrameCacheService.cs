using System.Collections.Concurrent;
using System.Security.Cryptography;
using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Video;

public sealed record VideoOriginalFrameResult(
    bool Success,
    bool AlreadyExisted,
    string? Path,
    string? Error);

public sealed class VideoOriginalFrameCacheService
{
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Lazy<Task<VideoOriginalFrameResult>>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public VideoOriginalFrameCacheService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    public string GetOriginalFramePath(string filePath)
    {
        var folderHash = GetFolderHash(filePath);
        var fileHash = GetFileHash(filePath);
        return System.IO.Path.Combine(_cacheDirectory, "video_originals", folderHash, fileHash + ".jpg");
    }

    public bool Exists(string filePath)
    {
        return File.Exists(GetOriginalFramePath(filePath));
    }

    public async Task<VideoOriginalFrameResult> EnsureAsync(
        string filePath,
        CancellationToken ct = default,
        bool cancelExtractionOnCancellation = false)
    {
        var originalPath = GetOriginalFramePath(filePath);
        if (File.Exists(originalPath))
            return new VideoOriginalFrameResult(true, true, originalPath, null);

        var key = NormalizeKey(filePath);
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<VideoOriginalFrameResult>>(
                () => EnsureCoreAsync(
                    filePath,
                    originalPath,
                    cancelExtractionOnCancellation ? ct : CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var task = lazy.Value;
        _ = task.ContinueWith(
            _ => _inflight.TryRemove(
                new KeyValuePair<string, Lazy<Task<VideoOriginalFrameResult>>>(key, lazy)),
            TaskScheduler.Default);

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static string NormalizeKey(string filePath)
    {
        try { return System.IO.Path.GetFullPath(filePath); }
        catch { return filePath; }
    }

    private static async Task<VideoOriginalFrameResult> EnsureCoreAsync(
        string filePath,
        string originalPath,
        CancellationToken ct)
    {
        if (File.Exists(originalPath))
            return new VideoOriginalFrameResult(true, true, originalPath, null);

        if (!File.Exists(filePath))
            return new VideoOriginalFrameResult(false, false, originalPath, "source file does not exist");

        var tempPath = originalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var originalFrame = await VideoThumbnailGenerator.ExtractOriginalFrameAsync(filePath, ct)
                .ConfigureAwait(false);
            if (originalFrame is not { Length: > 0 })
                return new VideoOriginalFrameResult(false, false, originalPath, "ffmpeg did not return frame data");

            var dir = System.IO.Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllBytesAsync(tempPath, originalFrame, ct).ConfigureAwait(false);

            if (File.Exists(originalPath))
            {
                TryDelete(tempPath);
                return new VideoOriginalFrameResult(true, true, originalPath, null);
            }

            File.Move(tempPath, originalPath);
            PerfLogger.Log($"[VideoCache] FFMPEG EXTRACT saved original {System.IO.Path.GetFileName(filePath)}");
            return new VideoOriginalFrameResult(true, false, originalPath, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            TryDelete(originalPath);
            AppLogger.Warn($"[VideoOriginalFrame] failed file={filePath} error={ex.Message}");
            return new VideoOriginalFrameResult(false, false, originalPath, ex.Message);
        }
    }

    private static string GetFolderHash(string filePath)
    {
        var dir = System.IO.Path.GetDirectoryName(filePath) ?? "_root";
        var hashBytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..8];
    }

    private static string GetFileHash(string filePath)
    {
        var hashBytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
