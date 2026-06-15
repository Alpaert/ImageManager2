namespace ImageManager.Core.Services;

public interface IThumbnailCacheService
{
    string CacheDirectory { get; }
    Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default);
    Task ClearAsync();
    long EstimatedMemoryBytes { get; }
    void Trim(long maxBytes, string? protectedKey = null);
}
