using System.Collections.Concurrent;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Video;

namespace ImageManager.Infrastructure.Caching;

public class ThumbnailCacheService : IThumbnailCacheService
{
    private class CacheEntry
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long SizeBytes { get; set; }
        public DateTime LastAccessUtc { get; set; }
        public int DecodeWidth { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiskThumbnailCache _diskCache;
    private readonly IMediaProcessorFactory _factory;
    private long _totalBytes;
    private const long MaxMemoryBytes = 50 * 1024 * 1024; // 50 MB

    public long EstimatedMemoryBytes => Interlocked.Read(ref _totalBytes);
    private string _cacheDirectory = @"C:\ImageManagerCache";
    public string CacheDirectory
    {
        get => _cacheDirectory;
        set
        {
            _cacheDirectory = value;
            _diskCache.CacheDirectory = value;
        }
    }
    private int _decodeWidth = 200;
    public int DecodeWidth
    {
        get => _decodeWidth;
        set
        {
            _decodeWidth = value;
            _diskCache.DecodeWidth = value;
        }
    }

    public ThumbnailCacheService(
        IMediaProcessorFactory factory,
        string cacheRoot = @"C:\ImageManagerCache",
        int decodeWidth = 200)
    {
        _factory = factory;
        _diskCache = new DiskThumbnailCache(cacheRoot, decodeWidth);
        DecodeWidth = decodeWidth;
        _cacheDirectory = cacheRoot;
    }

    public void SwitchCacheDirectory(string newPath)
    {
        var oldPath = _cacheDirectory;
        if (string.Equals(oldPath.TrimEnd('\\', '/'), newPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;

        CacheDirectory = newPath;

        // Async-clean old thumbnail cache directory (best-effort)
        _ = Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(oldPath))
                {
                    foreach (var dir in Directory.EnumerateDirectories(oldPath, "w*"))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch { }
        });
    }

    public async Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return (null, 0, 0);

        var isVideo = FileTypeConstants.IsVideoFile(filePath);

        if (decodeWidth != DecodeWidth)
        {
            DecodeWidth = decodeWidth;
            _diskCache.DecodeWidth = decodeWidth;
        }

        // 1. Memory cache (must match decode width)
        if (_cache.TryGetValue(filePath, out var entry) && entry.Data != null
            && entry.DecodeWidth == decodeWidth)
        {
            entry.LastAccessUtc = DateTime.UtcNow;
            return (entry.Data, entry.Width, entry.Height);
        }

        // 2. Disk cache
        var cached = _diskCache.Load(filePath);
        if (cached != null)
        {
            if (isVideo) PerfLogger.Log($"[Cache] DISK HIT {Path.GetFileName(filePath)}");
            // 优先从磁盘 meta 读取尺寸（JSON sidecar，零成本）
            var meta = _diskCache.LoadMeta(filePath);
            if (meta.HasValue)
            {
                AddToMemory(filePath, cached, decodeWidth, meta.Value.Width, meta.Value.Height);
                return (cached, meta.Value.Width, meta.Value.Height);
            }

            // 从缓存的 JPEG 头解析宽高（微秒级，完全避免 ffmpeg）
            var (w, h) = ParseJpegDimensions(cached);
            if (w > 0 && h > 0)
            {
                _diskCache.SaveMeta(filePath, w, h);
                AddToMemory(filePath, cached, decodeWidth, w, h);
                return (cached, w, h);
            }

            // JPEG 解析失败（极少见）→ 回退到 GetDimensions（图片快，视频可能慢）
            var processor = _factory.GetProcessor(filePath);
            (w, h) = processor.GetDimensions(filePath);
            _diskCache.SaveMeta(filePath, w, h);
            AddToMemory(filePath, cached, decodeWidth, w, h);
            return (cached, w, h);
        }

        // 3. Generate thumbnail
        if (isVideo) PerfLogger.Log($"[Cache] GENERATE start {Path.GetFileName(filePath)}");
        var processorGen = _factory.GetProcessor(filePath);
        var result = await processorGen.ExtractThumbnailAsync(filePath, decodeWidth, CancellationToken.None);

        if (result != null && result.Data.Length > 0)
        {
            AddToMemory(filePath, result.Data, decodeWidth, result.Width, result.Height);
            _diskCache.Save(filePath, result.Data);
            _diskCache.SaveMeta(filePath, result.Width, result.Height);
            return (result.Data, result.Width, result.Height);
        }

        return (null, 0, 0);
    }

    public Task ClearAsync()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _totalBytes, 0);
        return Task.CompletedTask;
    }

    public void Trim(long maxBytes, string? protectedKey = null)
    {
        if (Interlocked.Read(ref _totalBytes) <= maxBytes)
            return;

        var sorted = _cache
            .Where(kv => kv.Value != null)
            .OrderBy(kv => kv.Value!.LastAccessUtc)
            .ToList();

        foreach (var kv in sorted)
        {
            if (!string.IsNullOrEmpty(protectedKey) &&
                string.Equals(kv.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_cache.TryRemove(kv.Key, out var entry))
            {
                if (entry != null)
                    Interlocked.Add(ref _totalBytes, -entry.SizeBytes);
            }
            else
            {
                // Value was null �?force remove
                _cache.TryRemove(kv.Key, out _);
            }

            if (Interlocked.Read(ref _totalBytes) <= maxBytes)
                break;
        }

        if (Interlocked.Read(ref _totalBytes) < 0)
            Interlocked.Exchange(ref _totalBytes, 0);
    }

    private void AddToMemory(string filePath, byte[] data, int decodeWidth, int width, int height)
    {
        var entry = new CacheEntry
        {
            Data = data,
            SizeBytes = data.Length,
            LastAccessUtc = DateTime.UtcNow,
            DecodeWidth = decodeWidth,
            Width = width,
            Height = height
        };

        if (_cache.TryGetValue(filePath, out var oldEntry))
            Interlocked.Add(ref _totalBytes, -oldEntry.SizeBytes);

        _cache[filePath] = entry;
        Interlocked.Add(ref _totalBytes, data.Length);

        if (Interlocked.Read(ref _totalBytes) > MaxMemoryBytes)
            Trim(MaxMemoryBytes, filePath);
    }

    public void DeleteFromDiskCache(string filePath) => _diskCache.DeleteAllWidths(filePath);

    public long EstimateDiskUsage() => _diskCache.EstimateDiskUsage();

    /// <summary>
    /// 从 JPEG 字节数组头部解析图像宽高（微秒级，不涉及解码）。
    /// 遍历 JPEG marker 段，找到 SOF0/SOF2 marker 读取尺寸。
    /// </summary>
    private static (int Width, int Height) ParseJpegDimensions(byte[] jpeg)
    {
        if (jpeg.Length < 10) return (0, 0);
        int i = 2; // skip SOI marker (0xFF 0xD8)
        while (i < jpeg.Length - 9)
        {
            if (jpeg[i] != 0xFF) return (0, 0);
            byte m = jpeg[i + 1];
            // SOF0 (baseline) or SOF2 (progressive)
            if (m == 0xC0 || m == 0xC2)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        return (0, 0);
    }
}