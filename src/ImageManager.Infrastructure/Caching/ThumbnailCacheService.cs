using System.Collections.Concurrent;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Caching;

public class ThumbnailCacheService : IThumbnailCacheService
{
    private class CacheEntry
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long SizeBytes { get; set; }
        public DateTime LastAccessUtc { get; set; }
        public int DecodeWidth { get; set; }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiskThumbnailCache _diskCache;
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

    public ThumbnailCacheService(string cacheRoot = @"C:\ImageManagerCache", int decodeWidth = 200)
    {
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

    public async Task<byte[]?> GetOrCreateThumbnailAsync(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

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
            return entry.Data;
        }

        // 2. Disk cache
        var cached = _diskCache.Load(filePath);
        if (cached != null)
        {
            AddToMemory(filePath, cached, decodeWidth);
            return cached;
        }

        // 3. Generate thumbnail
        var data = await Task.Run(() => ThumbnailGenerator.Generate(filePath, decodeWidth));
        if (data != null)
        {
            AddToMemory(filePath, data, decodeWidth);
            _diskCache.Save(filePath, data);
        }

        return data;
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

    private void AddToMemory(string filePath, byte[] data, int decodeWidth)
    {
        var entry = new CacheEntry
        {
            Data = data,
            SizeBytes = data.Length,
            LastAccessUtc = DateTime.UtcNow,
            DecodeWidth = decodeWidth
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
}