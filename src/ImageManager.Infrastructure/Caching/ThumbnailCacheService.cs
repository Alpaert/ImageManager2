using System.Collections.Concurrent;
using System.Diagnostics;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Audio;
using ImageManager.Infrastructure.Imaging;

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
    long LruMilliseconds,
    bool IsFinalWithoutThumbnail = false, string? Version = null,
    PreviewPersistence Persistence = PreviewPersistence.Saved, string? RetryToken = null);

public partial class ThumbnailCacheService : IThumbnailCacheService
{
    private sealed class LruNode
    {
        public string Key { get; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long SizeBytes { get; set; }
        public int DecodeWidth { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Version { get; set; }
        public PreviewPersistence Persistence { get; set; }
        public string? RetryToken { get; set; }
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

    /// <summary>Read-only cache root retained from before a startup fallback.</summary>
    public string? ReadOnlyFallbackCacheDirectory
    {
        get => _diskCache.FallbackCacheDirectory;
        set => _diskCache.FallbackCacheDirectory = value;
    }

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

    private DiskThumbnailCache CreateDiskCache(int decodeWidth)
        => new(CacheDirectory, decodeWidth, ReadOnlyFallbackCacheDirectory);

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
                    var musicCoverDirectory = Path.Combine(oldPath, "music_covers");
                    if (Directory.Exists(musicCoverDirectory))
                    {
                        try { Directory.Delete(musicCoverDirectory, true); } catch { }
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
        var version = GetPreviewVersion(filePath);
        var result = await GetOrCreateThumbnailCoreAsync(filePath, decodeWidth, ct).ConfigureAwait(false);
        if (result.Version != null) return result;
        if (version != null && IsPreviewVersionCurrent(filePath, version)) return result with { Version = version };
        ClearMemoryEntry(filePath);
        return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
    }

    private readonly SharedMediaWork<ThumbnailCacheLoadResult> _imageWork = new(maxConcurrency: 6, maxPending: 32);

    private async Task<ThumbnailCacheLoadResult> GetOrCreateThumbnailCoreAsync(string filePath, int decodeWidth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
        if (IsCoverMedia(filePath)) return await LoadCoverDiagnosticsAsync(filePath, decodeWidth, ct).ConfigureAwait(false);
        var version = GetPreviewVersion(filePath);
        if (version == null) return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
        if (_index.TryGetValue(filePath, out var node) && node.Value.DecodeWidth == decodeWidth &&
            node.Value.Version == version && node.Value.Data is { Length: > 0 })
        {
            PromoteToFront(node);
            if (node.Value.Persistence == PreviewPersistence.MemoryOnly)
            {
                var kind = "w" + decodeWidth;
                var retained = FindRetry(filePath, version, kind);
                // The retry pool is intentionally smaller than the image LRU. Reconstruct a released
                // save handle from these already decoded bytes instead of returning a permanently stale token.
                node.Value.RetryToken = retained?.Token ?? RememberRetry(filePath,
                    new VersionedMediaStore.Entry(version, node.Value.Data, node.Value.Width, node.Value.Height,
                        Origin: ArtworkOrigin.Image), kind);
            }
            return new(node.Value.Data, node.Value.Width, node.Value.Height, ThumbnailCacheSource.Memory, 0, 0, 0, 0,
                Version: version, Persistence: node.Value.Persistence, RetryToken: node.Value.RetryToken);
        }
        return await _imageWork.RunAsync($"{filePath}|{version}|{decodeWidth}", async token =>
        {
            var store = PreviewStore;
            var kind = "w" + decodeWidth;
            var cached = store.Load(filePath, kind, version);
            if (cached != null && ValidEncodedImage(cached))
            {
                AddToMemory(filePath, cached.Data!, decodeWidth, cached.Width, cached.Height, version);
                return new(cached.Data, cached.Width, cached.Height, ThumbnailCacheSource.Disk, 0, 0, 0, 0, Version: version);
            }
            var retained = FindRetry(filePath, version, kind);
            if (retained != null)
            {
                var saved = PreviewPersistence.MemoryOnly;
                if (DateTime.UtcNow >= retained.RetryAfter)
                {
                    saved = store.Save(filePath, kind, retained.Entry);
                    retained.RetryAfter = DateTime.UtcNow.AddSeconds(2);
                }
                if (saved == PreviewPersistence.NotApplicable || store.Version(filePath) != version)
                    return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
                var retryToken = saved == PreviewPersistence.Saved ? null : retained.Token;
                if (saved == PreviewPersistence.Saved) RemoveRetry(filePath, kind);
                AddToMemory(filePath, retained.Entry.Data!, decodeWidth, retained.Entry.Width, retained.Entry.Height, version, saved, retryToken);
                return new(retained.Entry.Data, retained.Entry.Width, retained.Entry.Height, ThumbnailCacheSource.Memory,
                    0, 0, 0, 0, Version: version, Persistence: saved, RetryToken: retryToken);
            }
            // Legacy images have no source fingerprint. Validate by decoding the current source once;
            // never stamp unverified old-root bytes with the current identity.
            var result = await _factory.GetProcessor(filePath).ExtractThumbnailAsync(filePath, decodeWidth, token).ConfigureAwait(false);
            if (result?.Data is not { Length: > 0 } || store.Version(filePath) != version)
                return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
            var entry = new VersionedMediaStore.Entry(version, result.Data, result.Width, result.Height,
                Origin: ImageManager.Core.Models.ArtworkOrigin.Image);
            if (!ValidEncodedImage(entry)) return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
            var persistence = store.Save(filePath, kind, entry);
            if (persistence == PreviewPersistence.NotApplicable || store.Version(filePath) != version)
                return new(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
            var retry = persistence == PreviewPersistence.MemoryOnly ? RememberRetry(filePath, entry, kind) : null;
            AddToMemory(filePath, result.Data, decodeWidth, result.Width, result.Height, version, persistence, retry);
            return new(result.Data, result.Width, result.Height, ThumbnailCacheSource.Generated, 0, 0, 0, 0,
                Version: version, Persistence: persistence, RetryToken: retry);
        }, ct).ConfigureAwait(false);
    }

    public (byte[]? Data, int Width, int Height) TryGetCachedThumbnail(string filePath, int decodeWidth)
    {
        var version = GetPreviewVersion(filePath);
        if (version == null) return (null, 0, 0);
        if (!IsCoverMedia(filePath) && _index.TryGetValue(filePath, out var node) &&
            node.Value.DecodeWidth == decodeWidth && node.Value.Version == version)
        {
            PromoteToFront(node);
            return (node.Value.Data, node.Value.Width, node.Value.Height);
        }
        var width = IsCoverMedia(filePath) ? Math.Clamp(decodeWidth, 32, 2048) : decodeWidth;
        var cached = PreviewStore.Load(filePath, "w" + width, version);
        return cached != null && ValidEncodedImage(cached) ? (cached.Data, cached.Width, cached.Height) : (null, 0, 0);
    }

    /// <summary>
    /// Reads dimensions from existing thumbnail or video-original-frame cache entries.
    /// This method never generates thumbnails, invokes FFmpeg, or reads the source media.
    /// </summary>
    public (int Width, int Height) TryResolveCachedDimensions(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (0, 0);

        if (IsCoverMedia(filePath))
        {
            var version = GetPreviewVersion(filePath);
            var entry = version == null ? null : PreviewStore.Load(filePath, "original", version) ?? PreviewStore.Load(filePath, "cover", version);
            return entry != null && ValidEncodedImage(entry) ? (entry.Width, entry.Height) : (0, 0);
        }

        var imageVersion = GetPreviewVersion(filePath);
        var image = imageVersion == null ? null : PreviewStore.LoadAnyWidth(filePath, decodeWidth, imageVersion, ValidEncodedImage);
        return image != null && ValidEncodedImage(image) ? (image.Width, image.Height) : (0, 0);
    }

    /// <summary>
    /// Resolves dimensions from existing cache entries for multiple media paths.
    /// Read-only cache lookup; checks source identity with stat but never decodes source media or runs FFmpeg.
    /// </summary>
    public Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var decodeWidth = DecodeWidth;

        return Task.Run(() =>
        {
            var result = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths)
            {
                ct.ThrowIfCancellationRequested();
                var size = TryResolveCachedDimensions(path, decodeWidth);
                if (size.Width > 0 && size.Height > 0) result[path] = size;
            }
            return result;
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

    public (int Width, int Height) GetDimensions(string filePath) => ThumbnailGenerator.GetDimensions(filePath);
    public (int Width, int Height) GetDimensionsOrUnknown(string filePath) => ThumbnailGenerator.GetDimensionsOrUnknown(filePath);
    public byte[]? DecodeForHashInput(string filePath, int maxSize = 256) => ThumbnailGenerator.DecodeForHashInput(filePath, maxSize);

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

    private long AddToMemory(string filePath, byte[] data, int decodeWidth, int width, int height, string? version = null,
        PreviewPersistence persistence = PreviewPersistence.Saved, string? retryToken = null)
    {
        var sw = Stopwatch.StartNew();
        var newNode = new LruNode(filePath)
        {
            Data = data,
            SizeBytes = data.Length,
            DecodeWidth = decodeWidth,
            Width = width,
            Height = height,
            Version = version ?? GetPreviewVersion(filePath),
            Persistence = persistence,
            RetryToken = retryToken
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

    public void DeleteFromDiskCache(string filePath)
    {
        _diskCache.DeleteAllWidths(filePath);
        new MusicCoverCache(CacheDirectory).Delete(filePath);
    }

    public void MoveDiskCache(string oldPath, string newPath)
    {
        PreviewStore.Invalidate(oldPath);
        PreviewStore.Invalidate(newPath);
        RemoveRetry(oldPath);
        RemoveRetry(newPath);
        ClearMemoryEntry(oldPath);
        ClearMemoryEntry(newPath);
        _diskCache.MoveAllWidths(oldPath, newPath);
        new MusicCoverCache(CacheDirectory).Move(oldPath, newPath);
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

    public void InvalidateThumbnail(string filePath) => TryInvalidatePreview(filePath);

    public bool TryInvalidatePreview(string filePath)
    {
        var persisted = PreviewStore.Invalidate(filePath);
        RemoveRetry(filePath);
        ClearMemoryEntry(filePath);
        DeleteFromDiskCache(filePath);
        return persisted;
    }

    /// <summary>
    /// Creates a requested-width video thumbnail only from an already cached original frame.
    /// This never opens the source video, invokes a media processor, or starts FFmpeg.
    /// </summary>
    public Task<ThumbnailCacheLoadResult> TryCreateThumbnailFromExistingVideoOriginalFrameAsync(
        string filePath,
        string originalFramePath,
        int decodeWidth,
        CancellationToken ct = default)
    {
        return TryCreateValidatedVideoThumbnailAsync(filePath, decodeWidth, ct);
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
