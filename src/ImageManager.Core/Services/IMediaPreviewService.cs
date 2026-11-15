using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

/// <summary>Shared preview loading; caller cancellation only detaches that consumer.</summary>
public interface IMediaPreviewService
{
    Task<MediaPreviewResult> LoadPreviewAsync(string path, int decodeWidth, CancellationToken ct = default);
    Task<MediaPreviewResult> RetrySavePreviewAsync(string path, string retryToken, CancellationToken ct = default);
}
