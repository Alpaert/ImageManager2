using System.Threading;
using System.Threading.Tasks;
using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IVideoOriginalFrameCacheService
{
    string? ReadOnlyFallbackCacheDirectory { get; set; }
    string GetOriginalFramePath(string filePath);
    bool Exists(string filePath);
    Task<VideoOriginalFrameResult> EnsureAsync(
        string filePath,
        CancellationToken ct = default,
        bool cancelExtractionOnCancellation = false);
}
