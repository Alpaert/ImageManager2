namespace ImageManager.Core.Services;

public interface IThumbnailCacheService
{
    Task<byte[]?> GetOrCreateThumbnailAsync(string filePath, int decodeWidth);
    Task ClearAsync();
    long EstimatedMemoryBytes { get; }
    void Trim(long maxBytes, string? protectedKey = null);
}
