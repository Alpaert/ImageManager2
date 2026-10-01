using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Audio;

/// <summary>Extracts embedded album artwork from supported audio files.</summary>
public sealed class MusicMediaProcessor : IMediaProcessor
{
    public bool CanHandle(string extension) => FileTypeConstants.MusicExtensions.Contains(extension);

    public Task<MediaResult?> ExtractThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            byte[]? embeddedArtwork = null;
            var tagsReadable = false;
            try
            {
                using var media = TagLib.File.Create(filePath);
                tagsReadable = true;
                var pictures = media.Tag.Pictures ?? Array.Empty<TagLib.IPicture>();
                var picture = pictures.FirstOrDefault(item => item.Type == TagLib.PictureType.FrontCover)
                    ?? pictures.FirstOrDefault();
                embeddedArtwork = picture?.Data?.Data is { Length: > 0 } artwork
                    ? artwork
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A readable sidecar can still provide artwork when tags are damaged.
            }

            var jpeg = embeddedArtwork is { Length: > 0 }
                ? ThumbnailGenerator.NormalizeJpeg(embeddedArtwork, 2048)
                : null;
            if (jpeg == null)
            {
                var sidecarPath = MusicArtworkResolver.FindSidecarArtwork(filePath);
                if (sidecarPath == null)
                {
                    // Corrupt/unreadable tags and corrupt embedded artwork remain
                    // retryable; a valid tag set with no candidates is final.
                    return tagsReadable && embeddedArtwork == null
                        ? new MediaResult { IsFinalWithoutThumbnail = true }
                        : null;
                }

                try
                {
                    jpeg = ThumbnailGenerator.NormalizeJpeg(File.ReadAllBytes(sidecarPath), 2048);
                }
                catch
                {
                    return null;
                }
            }

            if (jpeg == null)
                return null;

            var (width, height) = ThumbnailGenerator.GetDimensions(jpeg);
            if (width <= 0 || height <= 0)
                return null;

            ct.ThrowIfCancellationRequested();
            return new MediaResult { Data = jpeg, Width = width, Height = height };
        }, ct);
    }

    public (int Width, int Height) GetDimensions(string filePath) => (0, 0);
}
