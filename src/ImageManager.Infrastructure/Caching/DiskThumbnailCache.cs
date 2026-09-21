using System.Collections.Concurrent;

namespace ImageManager.Infrastructure.Caching;

/// <summary>
/// Disk-based thumbnail cache. Isolates caches by decode width into subdirectories.
/// </summary>
public class DiskThumbnailCache
{
    private string _cacheRoot;
    private int _decodeWidth;

    public string CacheDirectory
    {
        get => _cacheRoot;
        set
        {
            _cacheRoot = value;
            try { Directory.CreateDirectory(CurrentCacheDirectory); } catch { }
        }
    }

    public int DecodeWidth
    {
        get => _decodeWidth;
        set
        {
            _decodeWidth = value;
            try { Directory.CreateDirectory(CurrentCacheDirectory); } catch { }
        }
    }

    public string CurrentCacheDirectory => Path.Combine(_cacheRoot, $"w{_decodeWidth}");

    public DiskThumbnailCache(string cacheRoot = @"C:\ImageManagerCache", int decodeWidth = 200)
    {
        _cacheRoot = cacheRoot;
        _decodeWidth = decodeWidth;
        try { Directory.CreateDirectory(CurrentCacheDirectory); } catch { }
    }

    public string GetCacheFilePath(string filePath)
    {
        var folderHash = GetFolderHash(filePath);
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var hashName = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(CurrentCacheDirectory, folderHash, hashName + ".jpg");
    }

    /// <summary>Old flat cache path (pre-folder-hierarchy) for backward compat migration</summary>
    private string GetOldCacheFilePath(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var hashName = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(CurrentCacheDirectory, hashName + ".jpg");
    }

    private static string GetFolderHash(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "_root";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..8];
    }

    public void Save(string filePath, byte[] pngData)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(cachePath, pngData);
        }
        catch
        {
            // Disk cache failures should not affect display
        }
    }

    /// <summary>Save dimension metadata alongside thumbnail (avoids re-running ffmpeg for video dimensions on cold load)</summary>
    public void SaveMeta(string filePath, int width, int height)
    {
        try
        {
            var metaPath = Path.ChangeExtension(GetCacheFilePath(filePath), ".json");
            var dir = Path.GetDirectoryName(metaPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var meta = System.Text.Json.JsonSerializer.Serialize(
                new { w = width, h = height, ts = DateTime.UtcNow.Ticks });
            File.WriteAllText(metaPath, meta);
        }
        catch { }
    }

    public (int Width, int Height)? LoadMeta(string filePath)
    {
        try
        {
            var metaPath = Path.ChangeExtension(GetCacheFilePath(filePath), ".json");
            if (!File.Exists(metaPath))
            {
                // Backward compat: check old flat path
                var oldPath = Path.ChangeExtension(GetOldCacheFilePath(filePath), ".json");
                if (File.Exists(oldPath))
                {
                    // Migrate to new path
                    try { File.Move(oldPath, metaPath); } catch { metaPath = oldPath; }
                }
                else
                {
                    return null;
                }
            }
            var json = File.ReadAllText(metaPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32());
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves intrinsic media dimensions from existing cache files without creating,
    /// moving, or deleting any cache entry. The requested decode width is checked first.
    /// </summary>
    public (int Width, int Height)? TryResolveCachedDimensions(
        string filePath,
        int decodeWidth,
        bool preferVideoOriginalFrame)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var current = TryLoadMeta(filePath, decodeWidth);
        if (IsUsableDimension(current))
            return current;

        // Some older cache entries predate the sidecar JSON. The thumbnail itself
        // is still sufficient and must be reused before probing the source video.
        var currentThumbnail = TryReadCachedJpegDimensions(filePath, Path.Combine(_cacheRoot, $"w{decodeWidth}"));
        if (IsUsableDimension(currentThumbnail))
            return currentThumbnail;

        if (preferVideoOriginalFrame)
        {
            var originalFramePath = GetOriginalFramePath(filePath);
            var originalDimensions = TryReadJpegDimensions(originalFramePath);
            if (IsUsableDimension(originalDimensions))
                return originalDimensions;
        }

        return TryResolveFromOtherWidths(filePath, decodeWidth, GetWidthDirectories());
    }

    /// <summary>
    /// Resolves multiple cached dimensions while enumerating width directories only once.
    /// </summary>
    public Dictionary<string, (int Width, int Height)> GetCachedDimensions(
        IReadOnlyCollection<string> filePaths,
        int decodeWidth,
        Func<string, bool> preferVideoOriginalFrame,
        CancellationToken ct)
    {
        var widthDirectories = GetWidthDirectories();
        var dimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            var current = TryLoadMeta(filePath, decodeWidth);
            if (IsUsableDimension(current))
            {
                dimensions[filePath] = current!.Value;
                continue;
            }

            var currentThumbnail = TryReadCachedJpegDimensions(filePath, Path.Combine(_cacheRoot, $"w{decodeWidth}"));
            if (IsUsableDimension(currentThumbnail))
            {
                dimensions[filePath] = currentThumbnail!.Value;
                continue;
            }

            if (preferVideoOriginalFrame(filePath))
            {
                var original = TryReadJpegDimensions(GetOriginalFramePath(filePath));
                if (IsUsableDimension(original))
                {
                    dimensions[filePath] = original!.Value;
                    continue;
                }
            }

            var other = TryResolveFromOtherWidths(filePath, decodeWidth, widthDirectories);
            if (IsUsableDimension(other))
                dimensions[filePath] = other!.Value;
        }

        return dimensions;
    }

    public byte[]? Load(string filePath)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            if (File.Exists(cachePath))
                return File.ReadAllBytes(cachePath);

            // Backward compat: migrate from old flat path to new folder-hierarchy path
            var oldPath = GetOldCacheFilePath(filePath);
            if (File.Exists(oldPath))
            {
                var data = File.ReadAllBytes(oldPath);
                try { Save(filePath, data); } catch { }
                try { File.Delete(oldPath); } catch { }
                return data;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool Exists(string filePath)
    {
        try
        {
            return File.Exists(GetCacheFilePath(filePath)) || File.Exists(GetOldCacheFilePath(filePath));
        }
        catch
        {
            return false;
        }
    }

    public void Delete(string filePath)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            if (File.Exists(cachePath))
                File.Delete(cachePath);
            // Also clean up old flat-path cache
            var oldPath = GetOldCacheFilePath(filePath);
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }
        catch { }
    }

    public void DeleteCurrentWidth(string filePath)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            var metaPath = Path.ChangeExtension(cachePath, ".json");
            if (File.Exists(cachePath))
                File.Delete(cachePath);
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            var oldPath = GetOldCacheFilePath(filePath);
            var oldMetaPath = Path.ChangeExtension(oldPath, ".json");
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            if (File.Exists(oldMetaPath))
                File.Delete(oldMetaPath);
        }
        catch { }
    }

    public void DeleteAllWidths(string filePath)
    {
        try
        {
            var hashName = GetCacheFileName(filePath);
            var folderHash = GetFolderHash(filePath);
            if (!Directory.Exists(_cacheRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_cacheRoot, "w*"))
            {
                // New nested path
                var cachePath = Path.Combine(dir, folderHash, hashName);
                if (File.Exists(cachePath))
                    File.Delete(cachePath);
                var metaPath = Path.ChangeExtension(cachePath, ".json");
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                // Old flat path (backward compat cleanup)
                var oldPath = Path.Combine(dir, hashName);
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
                var oldMetaPath = Path.ChangeExtension(oldPath, ".json");
                if (File.Exists(oldMetaPath))
                    File.Delete(oldMetaPath);
            }

            // Also delete video original frame
            var originalPath = Path.Combine(_cacheRoot, "video_originals", folderHash, hashName);
            if (File.Exists(originalPath))
                File.Delete(originalPath);
        }
        catch { }
    }

    public void MoveAllWidths(string oldPath, string newPath)
    {
        try
        {
            if (!Directory.Exists(_cacheRoot)) return;

            var oldHashName = GetCacheFileName(oldPath);
            var oldFolderHash = GetFolderHash(oldPath);
            var newHashName = GetCacheFileName(newPath);
            var newFolderHash = GetFolderHash(newPath);

            foreach (var dir in Directory.EnumerateDirectories(_cacheRoot, "w*"))
            {
                MoveCacheFile(
                    Path.Combine(dir, oldFolderHash, oldHashName),
                    Path.Combine(dir, newFolderHash, newHashName));
                MoveCacheFile(
                    Path.ChangeExtension(Path.Combine(dir, oldFolderHash, oldHashName), ".json"),
                    Path.ChangeExtension(Path.Combine(dir, newFolderHash, newHashName), ".json"));

                MoveCacheFile(Path.Combine(dir, oldHashName), Path.Combine(dir, newHashName));
                MoveCacheFile(
                    Path.ChangeExtension(Path.Combine(dir, oldHashName), ".json"),
                    Path.ChangeExtension(Path.Combine(dir, newHashName), ".json"));
            }

            MoveCacheFile(
                Path.Combine(_cacheRoot, "video_originals", oldFolderHash, oldHashName),
                Path.Combine(_cacheRoot, "video_originals", newFolderHash, newHashName));
        }
        catch { }
    }

    private string GetCacheFileName(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant() + ".jpg";
    }

    private (int Width, int Height)? TryLoadMeta(string filePath, int decodeWidth)
    {
        return TryLoadMeta(filePath, Path.Combine(_cacheRoot, $"w{decodeWidth}"));
    }

    private (int Width, int Height)? TryLoadMeta(string filePath, string cacheDirectory)
    {
        try
        {
            var hashName = GetCacheFileName(filePath);
            var folderHash = GetFolderHash(filePath);
            var metaPath = Path.ChangeExtension(Path.Combine(cacheDirectory, folderHash, hashName), ".json");
            if (!File.Exists(metaPath))
            {
                metaPath = Path.ChangeExtension(Path.Combine(cacheDirectory, hashName), ".json");
                if (!File.Exists(metaPath))
                    return null;
            }

            using var stream = File.OpenRead(metaPath);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            var root = document.RootElement;
            return (root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32());
        }
        catch { return null; }
    }

    private (int Width, int Height)? TryReadCachedJpegDimensions(string filePath, string cacheDirectory)
    {
        var hashName = GetCacheFileName(filePath);
        var folderHash = GetFolderHash(filePath);
        var nested = TryReadJpegDimensions(Path.Combine(cacheDirectory, folderHash, hashName));
        return IsUsableDimension(nested)
            ? nested
            : TryReadJpegDimensions(Path.Combine(cacheDirectory, hashName));
    }

    private string GetOriginalFramePath(string filePath)
    {
        return Path.Combine(_cacheRoot, "video_originals", GetFolderHash(filePath), GetCacheFileName(filePath));
    }

    private static bool IsUsableDimension((int Width, int Height)? dimensions)
    {
        return dimensions is { Width: > 1, Height: > 1 };
    }

    private (int Width, int Height)? TryResolveFromOtherWidths(
        string filePath,
        int decodeWidth,
        IReadOnlyList<string> widthDirectories)
    {
        foreach (var directory in widthDirectories)
        {
            if (TryGetDecodeWidth(directory) == decodeWidth)
                continue;

            var dimensions = TryLoadMeta(filePath, directory);
            if (IsUsableDimension(dimensions))
                return dimensions;

            var thumbnailDimensions = TryReadCachedJpegDimensions(filePath, directory);
            if (IsUsableDimension(thumbnailDimensions))
                return thumbnailDimensions;
        }

        return null;
    }

    private IReadOnlyList<string> GetWidthDirectories()
    {
        try
        {
            return Directory.Exists(_cacheRoot)
                ? Directory.EnumerateDirectories(_cacheRoot, "w*").ToArray()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int TryGetDecodeWidth(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.Length > 1 && name[0] == 'w' && int.TryParse(name.AsSpan(1), out var width)
            ? width
            : -1;
    }

    private static (int Width, int Height)? TryReadJpegDimensions(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using var stream = File.OpenRead(filePath);
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
                return null;

            while (stream.Position < stream.Length)
            {
                var markerPrefix = stream.ReadByte();
                if (markerPrefix != 0xFF)
                    return null;

                int marker;
                do { marker = stream.ReadByte(); } while (marker == 0xFF);
                if (marker < 0 || marker == 0xD9 || marker == 0xDA)
                    return null;

                var lengthHigh = stream.ReadByte();
                var lengthLow = stream.ReadByte();
                var segmentLength = (lengthHigh << 8) | lengthLow;
                if (lengthHigh < 0 || lengthLow < 0 || segmentLength < 2)
                    return null;

                if (IsStartOfFrameMarker(marker))
                {
                    if (segmentLength < 8 || stream.ReadByte() < 0)
                        return null;
                    var heightHigh = stream.ReadByte();
                    var heightLow = stream.ReadByte();
                    var widthHigh = stream.ReadByte();
                    var widthLow = stream.ReadByte();
                    if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0)
                        return null;

                    var height = (heightHigh << 8) | heightLow;
                    var width = (widthHigh << 8) | widthLow;
                    return width > 0 && height > 0 ? (width, height) : null;
                }

                stream.Seek(segmentLength - 2, SeekOrigin.Current);
            }
        }
        catch { }

        return null;
    }

    private static bool IsStartOfFrameMarker(int marker)
    {
        return marker is >= 0xC0 and <= 0xC3
            or >= 0xC5 and <= 0xC7
            or >= 0xC9 and <= 0xCB
            or >= 0xCD and <= 0xCF;
    }

    private static void MoveCacheFile(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return;

            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
                Directory.CreateDirectory(destinationDir);

            if (File.Exists(destinationPath))
            {
                File.Delete(sourcePath);
                return;
            }

            File.Move(sourcePath, destinationPath);
        }
        catch { }
    }

    public long EstimateDiskUsage()
    {
        try
        {
            if (!Directory.Exists(_cacheRoot))
                return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
