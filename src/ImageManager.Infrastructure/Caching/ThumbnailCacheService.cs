using System.Collections.Concurrent;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Helpers;

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

    private readonly ConcurrentDictionary<string, LinkedListNode<LruNode>> _index =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruNode> _lruList = new();
    private readonly object _lruLock = new();
    private readonly DiskThumbnailCache _diskCache;
    private readonly IMediaProcessorFactory _factory;
    private long _totalBytes;
    private const int MaxCachedItems = 500;
    private const long MaxMemoryBytes = 80 * 1024 * 1024;

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

    public async Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (null, 0, 0);

        var isVideo = FileTypeConstants.IsVideoFile(filePath);

        if (decodeWidth != DecodeWidth)
        {
            DecodeWidth = decodeWidth;
            _diskCache.DecodeWidth = decodeWidth;
        }

        if (_index.TryGetValue(filePath, out var node)
            && node.Value.Data != null
            && node.Value.DecodeWidth == decodeWidth)
        {
            PromoteToFront(node);
            return (node.Value.Data, node.Value.Width, node.Value.Height);
        }

        var cached = _diskCache.Load(filePath);
        if (cached != null)
        {
            if (isVideo) PerfLogger.Log($"[Cache] DISK HIT {Path.GetFileName(filePath)}");

            var meta = _diskCache.LoadMeta(filePath);
            if (isVideo && IsLegacyVideoPlaceholder(cached, decodeWidth, meta))
            {
                PerfLogger.Log($"[Cache] DROP legacy placeholder {Path.GetFileName(filePath)} w={decodeWidth}");
                _diskCache.DeleteCurrentWidth(filePath);
            }
            else if (meta.HasValue)
            {
                AddToMemory(filePath, cached, decodeWidth, meta.Value.Width, meta.Value.Height);
                return (cached, meta.Value.Width, meta.Value.Height);
            }
            else
            {
                var (w, h) = ParseJpegDimensions(cached);
                if (w > 0 && h > 0)
                {
                    _diskCache.SaveMeta(filePath, w, h);
                    AddToMemory(filePath, cached, decodeWidth, w, h);
                    return (cached, w, h);
                }

                if (!File.Exists(filePath))
                    return (cached, 0, 0);

                var processor = _factory.GetProcessor(filePath);
                (w, h) = processor.GetDimensions(filePath);
                _diskCache.SaveMeta(filePath, w, h);
                AddToMemory(filePath, cached, decodeWidth, w, h);
                return (cached, w, h);
            }
        }

        if (!isVideo && !File.Exists(filePath))
            return (null, 0, 0);

        if (isVideo) PerfLogger.Log($"[Cache] GENERATE start {Path.GetFileName(filePath)}");
        var processorGen = _factory.GetProcessor(filePath);
        var result = await processorGen.ExtractThumbnailAsync(filePath, decodeWidth, ct);

        if (result != null && result.Data.Length > 0)
        {
            AddToMemory(filePath, result.Data, decodeWidth, result.Width, result.Height);
            _diskCache.Save(filePath, result.Data);
            _diskCache.SaveMeta(filePath, result.Width, result.Height);
            return (result.Data, result.Width, result.Height);
        }

        return (null, 0, 0);
    }

    public (byte[]? Data, int Width, int Height) TryGetCachedThumbnail(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (null, 0, 0);

        var isVideo = FileTypeConstants.IsVideoFile(filePath);

        if (_index.TryGetValue(filePath, out var node)
            && node.Value.Data != null
            && node.Value.DecodeWidth == decodeWidth)
        {
            PromoteToFront(node);
            return (node.Value.Data, node.Value.Width, node.Value.Height);
        }

        if (decodeWidth != DecodeWidth)
        {
            DecodeWidth = decodeWidth;
            _diskCache.DecodeWidth = decodeWidth;
        }

        if (!_diskCache.Exists(filePath))
        {
            if (isVideo) PerfLogger.Log($"[Cache] WARM ONLY SKIP {Path.GetFileName(filePath)} w={decodeWidth}");
            return (null, 0, 0);
        }

        var cached = _diskCache.Load(filePath);
        if (cached == null)
            return (null, 0, 0);

        var meta = _diskCache.LoadMeta(filePath);
        if (isVideo && IsLegacyVideoPlaceholder(cached, decodeWidth, meta))
        {
            _diskCache.DeleteCurrentWidth(filePath);
            return (null, 0, 0);
        }

        if (meta.HasValue)
        {
            AddToMemory(filePath, cached, decodeWidth, meta.Value.Width, meta.Value.Height);
            return (cached, meta.Value.Width, meta.Value.Height);
        }

        var (w, h) = ParseJpegDimensions(cached);
        if (w <= 0 || h <= 0)
            return (null, 0, 0);

        _diskCache.SaveMeta(filePath, w, h);
        AddToMemory(filePath, cached, decodeWidth, w, h);
        return (cached, w, h);
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

    public void TrimForPressure()
    {
        var limit = MemoryPressureMonitor.Current switch
        {
            MemoryPressureMonitor.PressureLevel.Critical => MaxMemoryBytes / 4,
            MemoryPressureMonitor.PressureLevel.High => MaxMemoryBytes / 2,
            MemoryPressureMonitor.PressureLevel.Medium => MaxMemoryBytes * 3 / 4,
            _ => MaxMemoryBytes
        };

        Trim(limit);
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
            if (_index.TryGetValue(filePath, out var oldNode))
            {
                _lruList.Remove(oldNode);
                Interlocked.Add(ref _totalBytes, -oldNode.Value.SizeBytes);
            }

            var listNode = _lruList.AddFirst(newNode);
            _index[filePath] = listNode;
            Interlocked.Add(ref _totalBytes, data.Length);

            while (_lruList.Count > MaxCachedItems || Interlocked.Read(ref _totalBytes) > MaxMemoryBytes)
            {
                var tail = _lruList.Last;
                if (tail == null || string.Equals(tail.Value.Key, filePath, StringComparison.OrdinalIgnoreCase))
                    break;

                _lruList.Remove(tail);
                _index.TryRemove(tail.Value.Key, out _);
                Interlocked.Add(ref _totalBytes, -tail.Value.SizeBytes);
                tail.Value.Data = Array.Empty<byte>();
            }
        }
    }

    private void PromoteToFront(LinkedListNode<LruNode> node)
    {
        lock (_lruLock)
        {
            if (node.List != null)
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
        }
    }

    public void DeleteFromDiskCache(string filePath) => _diskCache.DeleteAllWidths(filePath);

    public void MoveDiskCache(string oldPath, string newPath)
    {
        ClearMemoryEntry(oldPath);
        ClearMemoryEntry(newPath);
        _diskCache.MoveAllWidths(oldPath, newPath);
    }

    public void ClearMemoryEntry(string filePath)
    {
        lock (_lruLock)
        {
            if (_index.TryRemove(filePath, out var node))
            {
                if (node.List != null)
                    _lruList.Remove(node);
                Interlocked.Add(ref _totalBytes, -node.Value.SizeBytes);
                node.Value.Data = Array.Empty<byte>();
            }
        }

        if (Interlocked.Read(ref _totalBytes) < 0)
            Interlocked.Exchange(ref _totalBytes, 0);
    }

    public void InvalidateThumbnail(string filePath)
    {
        ClearMemoryEntry(filePath);
        DeleteFromDiskCache(filePath);
    }

    public long EstimateDiskUsage() => _diskCache.EstimateDiskUsage();

    private static (int Width, int Height) ParseJpegDimensions(byte[] jpeg)
    {
        if (jpeg.Length < 10) return (0, 0);
        int i = 2;
        while (i < jpeg.Length - 9)
        {
            if (jpeg[i] != 0xFF) return (0, 0);
            byte m = jpeg[i + 1];
            if (m == 0xC0 || m == 0xC2)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        return (0, 0);
    }

    private static bool IsLegacyVideoPlaceholder(
        byte[] data,
        int decodeWidth,
        (int Width, int Height)? meta)
    {
        if (data.Length < 100 || data.Length > 64 * 1024)
            return false;
        if (meta is not { Width: 1920, Height: 1080 })
            return false;

        var (jpegWidth, jpegHeight) = ParseJpegDimensions(data);
        if (jpegWidth != decodeWidth || jpegHeight != decodeWidth * 9 / 16)
            return false;

        return data.Length < Math.Max(8 * 1024, decodeWidth * 24);
    }
}
