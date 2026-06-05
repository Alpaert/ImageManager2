namespace ImageManager.Core.Services;

/// <summary>Service for extracting video thumbnails and metadata.</summary>
public interface IVideoService
{
    /// <summary>Extract the first frame of a video as JPEG bytes, scaled to maxWidth.</summary>
    Task<byte[]?> ExtractThumbnailAsync(string filePath, int maxWidth);

    /// <summary>Get video dimensions (width, height). Returns (0,0) on failure.</summary>
    (int Width, int Height) GetVideoDimensions(string filePath);
}