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
                // Old flat path (backward compat cleanup)
                var oldPath = Path.Combine(dir, hashName);
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
            }
        }
        catch { }
    }

    private string GetCacheFileName(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant() + ".jpg";
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
