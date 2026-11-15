using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Caching;
using ImageManager.Core.Models;

namespace ImageManager.Infrastructure.Audio;

/// <summary>Extracts embedded album artwork from supported audio files.</summary>
public sealed class MusicMediaProcessor : IMediaProcessor
{
    public bool CanHandle(string extension) => FileTypeConstants.MusicExtensions.Contains(extension);

    public async Task<MediaResult?> ExtractThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct)
        => (await ExtractArtworkAsync(filePath, ct).ConfigureAwait(false)).Result;

    internal Task<(MediaResult? Result, ArtworkOrigin Origin)> ExtractArtworkAsync(string filePath, CancellationToken ct)
    {
        return Task.Run<(MediaResult? Result, ArtworkOrigin Origin)>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var hadEmbeddedCandidate = false;
            byte[]? jpeg = null;
            var tagsReadable = false;
            try
            {
                using var media = TagLib.File.Create(filePath);
                tagsReadable = true;
                var pictures = media.Tag.Pictures ?? Array.Empty<TagLib.IPicture>();
                hadEmbeddedCandidate = pictures.Any(picture => picture.Data?.Data is { Length: > 0 });
                jpeg = DecodeEmbeddedArtwork(pictures);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A readable sidecar can still provide artwork when tags are damaged.
            }

            var origin = ArtworkOrigin.Embedded;
            if (jpeg == null)
            {
                var sidecarPath = MusicArtworkResolver.FindSidecarArtwork(filePath);
                if (sidecarPath == null)
                {
                    // Corrupt/unreadable tags and corrupt embedded artwork remain
                    // retryable; a valid tag set with no candidates is final.
                    return tagsReadable && !hadEmbeddedCandidate
                        ? (new MediaResult { IsFinalWithoutThumbnail = true }, ArtworkOrigin.None)
                        : (null, ArtworkOrigin.None);
                }

                try
                {
                    var sidecar = new FileInfo(sidecarPath);
                    if (sidecar.Length > 32 * 1024 * 1024) return (null, ArtworkOrigin.None);
                    var bytes = File.ReadAllBytes(sidecarPath);
                    if (!ThumbnailCacheService.ValidEncodedImage(new("", bytes, 1, 1))) return (null, ArtworkOrigin.None);
                    jpeg = ThumbnailGenerator.NormalizeJpeg(bytes, 2048);
                    origin = MusicArtworkResolver.ClassifySidecar(filePath, sidecarPath);
                }
                catch
                {
                    return (null, ArtworkOrigin.None);
                }
            }

            if (jpeg == null)
                return (null, ArtworkOrigin.None);

            var (width, height) = ThumbnailGenerator.GetDimensions(jpeg);
            if (width <= 0 || height <= 0)
                return (null, ArtworkOrigin.None);

            ct.ThrowIfCancellationRequested();
            return (new MediaResult { Data = jpeg, Width = width, Height = height }, origin);
        }, ct);
    }

    public (int Width, int Height) GetDimensions(string filePath) => (0, 0);

    internal static byte[]? DecodeEmbeddedArtwork(IEnumerable<TagLib.IPicture> pictures)
    {
        foreach (var picture in pictures.OrderBy(p => p.Type == TagLib.PictureType.FrontCover ? 0 : 1))
        {
            var bytes = picture.Data?.Data;
            if (bytes is not { Length: > 0 } || !ThumbnailCacheService.ValidEncodedImage(new("", bytes, 1, 1))) continue;
            var jpeg = ThumbnailGenerator.NormalizeJpeg(bytes, 2048);
            if (jpeg != null) return jpeg;
        }
        return null;
    }
}
