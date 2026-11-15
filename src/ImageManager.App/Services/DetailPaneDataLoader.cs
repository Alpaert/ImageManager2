using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;

namespace ImageManager.App.Services;

public sealed record DetailPaneLoadRequest(string Path, int DecodeWidth, bool LoadThumbnail, bool IsFolder = false,
    string? RetryToken = null,
    bool LoadMetadata = true, string? FolderCoverMediaPath = null);
public sealed record DetailPaneData(IReadOnlyList<string> Tags, int? Width = null, int? Height = null,
    long? FileSize = null, DateTime? Modified = null, byte[]? ThumbnailData = null, string? ErrorText = null,
    MediaPreviewResult? Media = null, FolderDetailsSnapshot? Folder = null);

public interface IDetailPaneDataLoader
{
    Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken);
    Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, Func<MediaPreviewResult, Task> publishPreview,
        CancellationToken cancellationToken) => LoadAsync(request, cancellationToken);
    Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, Func<MediaPreviewResult, Task> publishPreview,
        Action<FolderDetailsSnapshot> reportFolderProgress, CancellationToken cancellationToken)
        => LoadAsync(request, publishPreview, cancellationToken);
    bool UsesVersionedPreview => false;
    bool IsKnownPreviewVersionCurrent(string path, string version) => true;
}

/// <summary>Single-file reads only. The caller serializes work, including uncancellable repository/native work.</summary>
public sealed class DetailPaneDataLoader(IImageMetaReadRepository metaRepo, IThumbnailCacheService thumbCache,
    IFolderDetailsService? folderDetails = null)
    : IDetailPaneDataLoader
{
    public bool UsesVersionedPreview => thumbCache is IMediaPreviewService;
    public bool IsKnownPreviewVersionCurrent(string path, string version) =>
        thumbCache is not ThumbnailCacheService cache || cache.IsKnownPreviewVersionCurrent(path, version);

    public Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken)
        => LoadAsync(request, _ => Task.CompletedTask, cancellationToken);

    public Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request,
        Func<MediaPreviewResult, Task> publishPreview, CancellationToken cancellationToken)
        => LoadAsync(request, publishPreview, _ => { }, cancellationToken);

    public async Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request,
        Func<MediaPreviewResult, Task> publishPreview, Action<FolderDetailsSnapshot> reportFolderProgress,
        CancellationToken cancellationToken)
    {
        if (request.IsFolder)
        {
            var statistics = ReadFolderDetailsAsync(request, reportFolderProgress, cancellationToken);
            FolderDetailsSnapshot? folder;
            try
            {
                if (request.LoadThumbnail && request.FolderCoverMediaPath is { } coverPath)
                {
                    var cover = await Task.Run(() => ReadFolderCoverAsync(coverPath, request.DecodeWidth,
                        cancellationToken), cancellationToken).ConfigureAwait(false);
                    await publishPreview(cover).ConfigureAwait(false);
                }
            }
            finally
            {
                folder = await statistics.ConfigureAwait(false);
            }
            return new([], ErrorText: request.LoadMetadata && folder is null ? "无法读取文件夹统计" : null, Folder: folder);
        }

        if (thumbCache is not IMediaPreviewService media)
        {
            if (request.LoadMetadata)
                return await ReadMetadataAsync(request, cancellationToken).ConfigureAwait(false);
            var bytes = request.LoadThumbnail && FileTypeConstants.IsImageFile(request.Path)
                ? (await thumbCache.GetOrCreateThumbnailAsync(request.Path, request.DecodeWidth, cancellationToken)
                    .ConfigureAwait(false)).Data : null;
            return new([], ThumbnailData: bytes);
        }

        if (request.RetryToken is { } retryToken)
            return new([], Media: await ReadPreviewAsync(
                () => media.RetrySavePreviewAsync(request.Path, retryToken, cancellationToken), cancellationToken)
                .ConfigureAwait(false));

        var metadata = request.LoadMetadata
            ? ReadMetadataAsync(request with { LoadThumbnail = false }, cancellationToken)
            : Task.FromResult(new DetailPaneData([]));
        if (!request.LoadThumbnail)
            return await metadata.ConfigureAwait(false);

        try
        {
            var preview = await Task.Run(() => ReadPreviewAsync(
                () => media.LoadPreviewAsync(request.Path, request.DecodeWidth, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await publishPreview(preview).ConfigureAwait(false);
            return (await metadata.ConfigureAwait(false)) with { Media = preview, ThumbnailData = preview.Data };
        }
        catch
        {
            // Keep the metadata read within the same bounded request, even when preview publication fails.
            try { await metadata.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            throw;
        }
    }

    private sealed class FolderProgress(Action<FolderDetailsSnapshot> report) : IProgress<FolderDetailsSnapshot>
    {
        public void Report(FolderDetailsSnapshot value) => report(value);
    }

    private async Task<FolderDetailsSnapshot?> ReadFolderDetailsAsync(DetailPaneLoadRequest request,
        Action<FolderDetailsSnapshot> reportFolderProgress, CancellationToken ct)
        => request.LoadMetadata && folderDetails != null
            ? await folderDetails.GetAsync(request.Path, new FolderProgress(reportFolderProgress), ct).ConfigureAwait(false) : null;

    private async Task<MediaPreviewResult> ReadFolderCoverAsync(string path, int width, CancellationToken ct)
    {
        try
        {
            if (thumbCache is ThumbnailCacheService cache)
            {
                if (FileTypeConstants.IsImageFile(path))
                {
                    var result = await cache.GetOrCreateThumbnailWithDiagnosticsAsync(path, width, ct)
                        .ConfigureAwait(false);
                    return result.Data is { Length: > 0 } && result.Version != null
                        ? new(MediaPreviewState.Ready, result.Data, Version: result.Version)
                        : new(MediaPreviewState.Failed);
                }

                var version = cache.GetPreviewVersion(path);
                var cached = cache.TryGetCachedThumbnail(path, width).Data;
                return cached is { Length: > 0 } && version != null && cache.IsPreviewVersionCurrent(path, version)
                    ? new(MediaPreviewState.Ready, cached, Version: version)
                    : new(MediaPreviewState.Failed);
            }

            var bytes = FileTypeConstants.IsImageFile(path)
                ? (await thumbCache.GetOrCreateThumbnailAsync(path, width, ct).ConfigureAwait(false)).Data
                : null;
            return bytes is { Length: > 0 } ? new(MediaPreviewState.Ready, bytes) : new(MediaPreviewState.Failed);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new(MediaPreviewState.Failed);
        }
    }

    private static async Task<MediaPreviewResult> ReadPreviewAsync(Func<Task<MediaPreviewResult>> read,
        CancellationToken cancellationToken)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new(MediaPreviewState.Failed, Error: ex.Message);
        }
    }

    private Task<DetailPaneData> ReadMetadataAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var meta = await metaRepo.GetByPathAsync(request.Path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var tags = meta?.Tags.Select(t => t.Name).ToArray() ?? Array.Empty<string>();
                var file = new FileInfo(request.Path);
                if (!file.Exists)
                    return new DetailPaneData(tags, ErrorText: "文件不存在或无法访问");
                var music = FileTypeConstants.IsMusicFile(request.Path);
                int? width = !music && meta?.Width > 0 ? meta.Width : null;
                int? height = !music && meta?.Height > 0 ? meta.Height : null;
                // Older video rows include synthetic 1x1 dimensions, and have no provenance flag.
                // Only a non-placeholder record matching the current file is usable; do not probe video here.
                if (FileTypeConstants.IsVideoFile(request.Path) &&
                    (meta == null || meta.Width <= 1 || meta.Height <= 1 ||
                     meta.LastWriteTicks != file.LastWriteTimeUtc.Ticks || meta.FileSize != file.Length))
                    width = height = null;
                if (FileTypeConstants.IsImageFile(request.Path) &&
                    (width == null || height == null || width == 1 || height == 1 ||
                     meta?.LastWriteTicks != file.LastWriteTimeUtc.Ticks || meta.FileSize != file.Length))
                {
                    var dimensions = thumbCache.GetDimensionsOrUnknown(request.Path);
                    width = dimensions.Width > 0 ? dimensions.Width : null;
                    height = dimensions.Height > 0 ? dimensions.Height : null;
                }
                byte[]? bytes = null;
                cancellationToken.ThrowIfCancellationRequested();
                if (request.LoadThumbnail && FileTypeConstants.IsImageFile(request.Path))
                    bytes = (await thumbCache.GetOrCreateThumbnailAsync(request.Path, request.DecodeWidth,
                        cancellationToken).ConfigureAwait(false)).Data;
                cancellationToken.ThrowIfCancellationRequested();
                return new DetailPaneData(tags, width, height, file.Length, file.LastWriteTime, bytes);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return new([], ErrorText: $"无法读取文件信息：{ex.Message}");
            }
        }, cancellationToken);
}
