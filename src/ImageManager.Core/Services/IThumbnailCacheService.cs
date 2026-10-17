using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ImageManager.Core.Services;

public interface IThumbnailCacheService
{
    string CacheDirectory { get; set; }
    Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default);
    Task ClearAsync();
    long EstimatedMemoryBytes { get; }
    void Trim(long maxBytes, string? protectedKey = null);
    void MoveDiskCache(string oldPath, string newPath);
    void InvalidateThumbnail(string filePath);
    (int Width, int Height) GetDimensions(string filePath);
    (int Width, int Height) GetDimensionsOrUnknown(string filePath);
    byte[]? DecodeForHashInput(string filePath, int maxSize = 256);
    Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(IReadOnlyCollection<string> filePaths, CancellationToken ct = default);
}
