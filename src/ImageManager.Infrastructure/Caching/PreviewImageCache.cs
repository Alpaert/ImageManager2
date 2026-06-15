using System.Collections.Concurrent;
using System.Diagnostics;

namespace ImageManager.Infrastructure.Caching;

/// <summary>
/// Weighted LRU-2 cache for full-resolution preview images.
///
/// Eviction score: S = α·(T_now - T_last_2) + β·|Index - Index_current| + γ·(Size / Resolution)
/// Higher score → evicted first.
///
/// Cached byte arrays are from ThumbnailGenerator (new allocations); eviction just releases
/// the reference for GC. Intermediate decode buffers use ArrayPool internally.
/// </summary>
public sealed class PreviewImageCache
{
    private sealed class CacheEntry
    {
        public byte[] Data = null!;
        public int DataLength;   // actual data length (Data may be from pool, larger)
        public int PixelWidth;
        public int PixelHeight;
        public long SizeBytes;
        public long TicksFirst;  // first access time
        public long TicksLast;   // second-to-last access time (for LRU-2)
        public int AccessCount;
        public int FileIndex;    // position in the image list
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // Scoring weights (tunable)
    private const double Alpha = 1.0;    // recency weight
    private const double Beta = 0.5;     // distance weight
    private const double Gamma = 0.1;    // size/resolution weight

    private const int MaxEntries = 20;
    private const long MaxMemoryBytes = 400 * 1024 * 1024; // 400 MB for JPEG cache
    private long _totalBytes;

    // Current browsing position (set externally by preloader)
    private int _currentIndex;

    public long EstimatedMemoryBytes => Interlocked.Read(ref _totalBytes);
    public int Count => _entries.Count;

    /// <summary>Update the current browsing position for distance-based scoring.</summary>
    public void SetCurrentIndex(int index) => _currentIndex = index;

    /// <summary>
    /// Try to get a cached entry. Returns null if not found.
    /// On hit, updates LRU-2 access tracking.
    /// </summary>
    public byte[]? TryGet(string filePath, out int width, out int height)
    {
        if (_entries.TryGetValue(filePath, out var entry) && entry.Data != null)
        {
            UpdateAccess(entry);
            width = entry.PixelWidth;
            height = entry.PixelHeight;
            return entry.Data;
        }
        width = 0;
        height = 0;
        return null;
    }

    /// <summary>
    /// Store decoded image data in the cache. The data array ownership transfers to the cache.
    /// Cache may evict old entries if limits are exceeded.
    /// </summary>
    public void Store(string filePath, byte[] data, int width, int height, int fileIndex)
    {
        if (data == null || data.Length == 0) return;

        long now = Stopwatch.GetTimestamp();

        lock (_lock)
        {
            // Remove/replace existing entry
            if (_entries.TryGetValue(filePath, out var existing))
            {
                Interlocked.Add(ref _totalBytes, -existing.SizeBytes);
            }

            var entry = new CacheEntry
            {
                Data = data,
                DataLength = data.Length,
                PixelWidth = width,
                PixelHeight = height,
                SizeBytes = data.Length,
                TicksFirst = now,
                TicksLast = 0,
                AccessCount = 1,
                FileIndex = fileIndex
            };

            _entries[filePath] = entry;
            Interlocked.Add(ref _totalBytes, data.Length);

            // Evict if over limits
            while (_entries.Count > MaxEntries || Interlocked.Read(ref _totalBytes) > MaxMemoryBytes)
            {
                var victim = FindEvictionVictim();
                if (victim == null) break;
                EvictEntry(victim);
            }
        }
    }

    private void UpdateAccess(CacheEntry entry)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            entry.TicksLast = entry.TicksFirst;
            entry.TicksFirst = now;
            entry.AccessCount++;
        }
    }

    private CacheEntry? FindEvictionVictim()
    {
        CacheEntry? worst = null;
        double worstScore = double.MinValue;
        long now = Stopwatch.GetTimestamp();

        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            int distance = Math.Abs(entry.FileIndex - _currentIndex);

            // LRU-2: time since second-to-last access
            long recency = entry.TicksLast > 0
                ? now - entry.TicksLast
                : (now - entry.TicksFirst) * 2; // only accessed once → penalize more

            double resolution = entry.PixelWidth * (double)entry.PixelHeight;
            double sizePerPixel = resolution > 0 ? entry.SizeBytes / resolution : 1.0;

            double score = Alpha * recency + Beta * distance + Gamma * sizePerPixel;

            if (score > worstScore)
            {
                worstScore = score;
                worst = entry;
            }
        }

        return worst;
    }

    private void EvictEntry(CacheEntry entry)
    {
        // Find the key for this entry (reverse lookup)
        string? keyToRemove = null;
        foreach (var kv in _entries)
        {
            if (kv.Value == entry)
            {
                keyToRemove = kv.Key;
                break;
            }
        }

        if (keyToRemove != null && _entries.TryRemove(keyToRemove, out _))
        {
            Interlocked.Add(ref _totalBytes, -entry.SizeBytes);
            // Release reference for GC (arrays come from ThumbnailGenerator, not ArrayPool)
            entry.Data = null!;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var kv in _entries)
            {
                kv.Value.Data = null!;
            }
            _entries.Clear();
        }
        Interlocked.Exchange(ref _totalBytes, 0);
    }

    public void ClearExcept(string protectedKey)
    {
        lock (_lock)
        {
            var toRemove = new List<string>();
            foreach (var kv in _entries)
            {
                if (!string.Equals(kv.Key, protectedKey, StringComparison.OrdinalIgnoreCase))
                    toRemove.Add(kv.Key);
            }
            foreach (var key in toRemove)
            {
                if (_entries.TryRemove(key, out var entry))
                {
                    Interlocked.Add(ref _totalBytes, -entry.SizeBytes);
                    entry.Data = null!;
                }
            }
        }
    }
}
