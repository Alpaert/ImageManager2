using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;

namespace ImageManager.App.Services;

internal static class FolderCoverLoader
{
    public static Task<(byte[]? Data, int Width, int Height)> LoadAsync(
        IThumbnailCacheService cache, string path, int width, CancellationToken ct)
        => Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (FileTypeConstants.IsImageFile(path))
                return await cache.GetOrCreateThumbnailAsync(path, width, ct).ConfigureAwait(false);
            var cached = (cache as ThumbnailCacheService)?.TryGetCachedThumbnail(path, width);
            ct.ThrowIfCancellationRequested();
            return (cached?.Data, cached?.Width ?? 0, cached?.Height ?? 0);
        }, ct);
}
