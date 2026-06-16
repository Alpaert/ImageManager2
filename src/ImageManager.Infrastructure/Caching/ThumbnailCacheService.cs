using System.Collections.Concurrent;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Video;

namespace ImageManager.Infrastructure.Caching;

public class ThumbnailCacheService : IThumbnailCacheService
{
    private sealed class LruNode
    {
        public string Key { get; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long SizeBytes { get; set; }
        public int DecodeWidth { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public LruNode(string key) => Key = key;
    }

    private readonly ConcurrentDictionary<string, LinkedListNode<LruNode>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruNode> _lruList = new();
    private readonly object _lruLock = new();
    private readonly DiskThumbnailCache _diskCache;
    private readonly IMediaProcessorFactory _factory;
    private long _totalBytes;
    private const int MaxCachedItems = 500;
    private const long MaxMemoryBytes = 80 * 1024 * 1024; // 80 MB hard cap

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

    public async Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default)
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
        if (_index.TryGetValue(filePath, out var node) && node.Value.Data != null
            && node.Value.DecodeWidth == decodeWidth)
        {
            PromoteToFront(node);
            return (node.Value.Data, node.Value.Width, node.Value.Height);
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
        var result = await processorGen.ExtractThumbnailAsync(filePath, decodeWidth, ct);

        if (result != null && result.Data.Length > 0)
        {
            AddToMemory(filePath, result.Data, decodeWidth, result.Width, result.Height);
            _diskCache.Save(filePath, result.Data);
            _diskCache.SaveMeta(filePath, result.Width, result.Height);
            AppLogger.Memory($"ThumbCache.Gen {Path.GetFileName(filePath)}");
            return (result.Data, result.Width, result.Height);
        }

        return (null, 0, 0);
    }

    public Task ClearAsync()
    {
        lock (_lruLock)
        {
            _lruList.Clear();
            _index.Clear();
        }
        Interlocked.Exchange(ref _totalBytes, 0);
        return Task.CompletedTask;
    }

    public void Trim(long maxBytes, string? protectedKey = null)
    {
        lock (_lruLock)
        {
            // Evict from tail (least recently used) until under byte limit
            var node = _lruList.Last;
            while (node != null && Interlocked.Read(ref _totalBytes) > maxBytes)
            {
                var next = node.Previous;
                if (!string.IsNullOrEmpty(protectedKey) &&
                    string.Equals(node.Value.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    node = next;
                    continue;
                }

                _lruList.Remove(node);
                _index.TryRemove(node.Value.Key, out _);
                Interlocked.Add(ref _totalBytes, -node.Value.SizeBytes);
                node.Value.Data = Array.Empty<byte>();
                node = next;
            }
        }

        if (Interlocked.Read(ref _totalBytes) < 0)
            Interlocked.Exchange(ref _totalBytes, 0);
    }

    private void AddToMemory(string filePath, byte[] data, int decodeWidth, int width, int height)
    {
        var newNode = new LruNode(filePath)
        {
            Data = data,
            SizeBytes = data.Length,
            DecodeWidth = decodeWidth,
            Width = width,
            Height = height
        };

        lock (_lruLock)
        {
            // Remove old entry if exists
            if (_index.TryGetValue(filePath, out var oldNode))
            {
                _lruList.Remove(oldNode);
                Interlocked.Add(ref _totalBytes, -oldNode.Value.SizeBytes);
            }

            // Add to front (most recently used)
            var listNode = _lruList.AddFirst(newNode);
            _index[filePath] = listNode;
            Interlocked.Add(ref _totalBytes, data.Length);

            // Evict if over item count OR byte limit
            while (_lruList.Count > MaxCachedItems || Interlocked.Read(ref _totalBytes) > MaxMemoryBytes)
            {
                var tail = _lruList.Last;
                if (tail == null || string.Equals(tail.Value.Key, filePath, StringComparison.OrdinalIgnoreCase))
                    break; // Don't evict the item we just added

                _lruList.Remove(tail);
                _index.TryRemove(tail.Value.Key, out _);
                Interlocked.Add(ref _totalBytes, -tail.Value.SizeBytes);
                // Clear reference to help GC
                tail.Value.Data = Array.Empty<byte>();
            }
        }
        AppLogger.Memory("ThumbCache.LRU");
    }

    private void PromoteToFront(LinkedListNode<LruNode> node)
    {
        lock (_lruLock)
        {
            if (node.List != null) // still in the list
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
        }
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