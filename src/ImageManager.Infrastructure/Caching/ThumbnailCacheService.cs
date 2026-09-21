using System.Collections.Concurrent;
using System.Diagnostics;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Helpers;

namespace ImageManager.Infrastructure.Caching;

public enum ThumbnailCacheSource
{
    Memory,
    Disk,
    Generated,
    Missing
}

public readonly record struct ThumbnailCacheLoadResult(
    byte[]? Data,
    int Width,
    int Height,
    ThumbnailCacheSource Source,
    long CacheLookupMilliseconds,
    long GenerateMilliseconds,
    long WriteMilliseconds,
    long LruMilliseconds);

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
        var result = await GetOrCreateThumbnailWithDiagnosticsAsync(filePath, decodeWidth, ct);
        return (result.Data, result.Width, result.Height);
    }

    public async Task<ThumbnailCacheLoadResult> GetOrCreateThumbnailWithDiagnosticsAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);

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
            var lookup = Stopwatch.StartNew();
            PromoteToFront(node);
            var lookupMs = lookup.ElapsedMilliseconds;
            LogSlow("memory", lookupMs, 5);
            return new(node.Value.Data, node.Value.Width, node.Value.Height,
                ThumbnailCacheSource.Memory, lookupMs, 0, 0, 0);
        }

        var diskRead = Stopwatch.StartNew();
        var cached = _diskCache.Load(filePath);
        var diskReadMs = diskRead.ElapsedMilliseconds;
        if (cached != null)
            LogSlow("disk", diskReadMs, 50);
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
                var lruMs = AddToMemory(filePath, cached, decodeWidth, meta.Value.Width, meta.Value.Height);
                return new(cached, meta.Value.Width, meta.Value.Height,
                    ThumbnailCacheSource.Disk, diskReadMs, 0, 0, lruMs);
            }
            else
            {
                var (w, h) = ParseJpegDimensions(cached);
                if (w > 0 && h > 0)
                {
                    var metaWrite = Stopwatch.StartNew();
                    _diskCache.SaveMeta(filePath, w, h);
                    var metaWriteMs = metaWrite.ElapsedMilliseconds;
                    LogSlow("write", metaWriteMs, 50);
                    var metaLruMs = AddToMemory(filePath, cached, decodeWidth, w, h);
                    return new(cached, w, h, ThumbnailCacheSource.Disk, diskReadMs, 0, metaWriteMs, metaLruMs);
                }

                if (!File.Exists(filePath))
                    return new(cached, 0, 0, ThumbnailCacheSource.Disk, diskReadMs, 0, 0, 0);

                var processor = _factory.GetProcessor(filePath);
                (w, h) = processor.GetDimensions(filePath);
                var write = Stopwatch.StartNew();
                _diskCache.SaveMeta(filePath, w, h);
                var writeMs = write.ElapsedMilliseconds;
                LogSlow("write", writeMs, 50);
                var lruMs = AddToMemory(filePath, cached, decodeWidth, w, h);
                return new(cached, w, h, ThumbnailCacheSource.Disk, diskReadMs, 0, writeMs, lruMs);
            }
        }

        if (!isVideo && !File.Exists(filePath))
            return new(null, 0, 0, ThumbnailCacheSource.Missing, diskReadMs, 0, 0, 0);

        if (isVideo) PerfLogger.Log($"[Cache] GENERATE start {Path.GetFileName(filePath)}");
        var processorGen = _factory.GetProcessor(filePath);
        var generation = Stopwatch.StartNew();
        var result = await processorGen.ExtractThumbnailAsync(filePath, decodeWidth, ct);
        var generationMs = generation.ElapsedMilliseconds;
        LogSlow("generate", generationMs, 150);

        if (result != null && result.Data.Length > 0)
        {
            var lruMs = AddToMemory(filePath, result.Data, decodeWidth, result.Width, result.Height);
            var write = Stopwatch.StartNew();
            _diskCache.Save(filePath, result.Data);
            _diskCache.SaveMeta(filePath, result.Width, result.Height);
            var writeMs = write.ElapsedMilliseconds;
            LogSlow("write", writeMs, 50);
            return new(result.Data, result.Width, result.Height,
                ThumbnailCacheSource.Generated, diskReadMs, generationMs, writeMs, lruMs);
        }

        return new(null, 0, 0, ThumbnailCacheSource.Missing, diskReadMs, generationMs, 0, 0);
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

    /// <summary>
    /// Reads dimensions from existing thumbnail or video-original-frame cache entries.
    /// This method never generates thumbnails, invokes FFmpeg, or reads the source media.
    /// </summary>
    public (int Width, int Height) TryResolveCachedDimensions(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (0, 0);

        var isVideo = FileTypeConstants.IsVideoFile(filePath);
        var dimensions = _diskCache.TryResolveCachedDimensions(filePath, decodeWidth, isVideo);
        return dimensions is { Width: > 1, Height: > 1 }
            ? dimensions.Value
            : (0, 0);
    }

    /// <summary>
    /// Resolves dimensions from existing cache entries for multiple media paths.
    /// This is a read-only operation and never accesses source media or runs FFmpeg.
    /// </summary>
    public Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var decodeWidth = DecodeWidth;

        return Task.Run(() =>
        {
            return _diskCache.GetCachedDimensions(
                filePaths,
                decodeWidth,
                FileTypeConstants.IsVideoFile,
                ct);
        }, ct);
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
        var sw = Stopwatch.StartNew();
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

        LogSlow("lru", sw.ElapsedMilliseconds, 10);
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

    private long AddToMemory(string filePath, byte[] data, int decodeWidth, int width, int height)
    {
        var sw = Stopwatch.StartNew();
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

        var elapsedMs = sw.ElapsedMilliseconds;
        LogSlow("lru", elapsedMs, 10);
        return elapsedMs;
    }

    private static void LogSlow(string stage, long elapsedMs, long thresholdMs)
    {
        if (elapsedMs > thresholdMs)
            AppLogger.Info($"Thumb.Cache.Slow stage={stage} elapsedMs={elapsedMs} thresholdMs={thresholdMs}");
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
