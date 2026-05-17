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
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiskThumbnailCache _diskCache;
    private long _totalBytes;

    public long EstimatedMemoryBytes => Interlocked.Read(ref _totalBytes);
    public string CacheDirectory { get; set; }
    public int DecodeWidth { get; set; } = 200;

    public ThumbnailCacheService(string cacheRoot = @"C:\ImageManagerCache", int decodeWidth = 200)
    {
        CacheDirectory = cacheRoot;
        DecodeWidth = decodeWidth;
        _diskCache = new DiskThumbnailCache(cacheRoot, decodeWidth);
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

        // 1. Memory cache
        if (_cache.TryGetValue(filePath, out var entry) && entry.Data != null)
        {
            entry.LastAccessUtc = DateTime.UtcNow;
            return entry.Data;
        }

        // 2. Disk cache
        var cached = _diskCache.Load(filePath);
        if (cached != null)
        {
            AddToMemory(filePath, cached);
            return cached;
        }

        // 3. Generate thumbnail
        var data = await Task.Run(() => ThumbnailGenerator.Generate(filePath, decodeWidth));
        if (data != null)
        {
            AddToMemory(filePath, data);
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
            .OrderBy(kv => kv.Value.LastAccessUtc)
            .ToList();

        foreach (var kv in sorted)
        {
            if (!string.IsNullOrEmpty(protectedKey) &&
                string.Equals(kv.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_cache.TryRemove(kv.Key, out var entry))
            {
                Interlocked.Add(ref _totalBytes, -entry.SizeBytes);
            }

            if (Interlocked.Read(ref _totalBytes) <= maxBytes)
                break;
        }

        if (Interlocked.Read(ref _totalBytes) < 0)
            Interlocked.Exchange(ref _totalBytes, 0);
    }

    private void AddToMemory(string filePath, byte[] data)
    {
        var entry = new CacheEntry
        {
            Data = data,
            SizeBytes = data.Length,
            LastAccessUtc = DateTime.UtcNow
        };

        _cache[filePath] = entry;
        Interlocked.Add(ref _totalBytes, data.Length);
    }

    public long EstimateDiskUsage() => _diskCache.EstimateDiskUsage();
}
