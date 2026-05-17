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
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var hashName = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(CurrentCacheDirectory, hashName + ".jpg");
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

    public byte[]? Load(string filePath)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            if (!File.Exists(cachePath))
                return null;

            return File.ReadAllBytes(cachePath);
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string filePath)
    {
        try
        {
            var cachePath = GetCacheFilePath(filePath);
            if (File.Exists(cachePath))
                File.Delete(cachePath);
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
