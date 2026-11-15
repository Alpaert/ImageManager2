using System.Security.Cryptography;
using System.Text;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Video;

public sealed class VideoOriginalFrameCacheService : IVideoOriginalFrameCacheService, IDisposable
{
    private readonly string _cacheDirectory;
    private readonly VersionedMediaStore _store;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _extractFrame;
    private readonly SharedMediaWork<(VersionedMediaStore.Entry? Entry, PreviewPersistence Persistence)> _work = new();
    public VideoOriginalFrameCacheService(string cacheDirectory, Func<string, CancellationToken, Task<byte[]?>>? extractFrame = null)
    {
        _cacheDirectory = cacheDirectory;
        _store = new(cacheDirectory);
        _extractFrame = extractFrame ?? VideoThumbnailGenerator.ExtractOriginalFrameAsync;
    }
    // Legacy roots are not trusted without source-version evidence.
    public string? ReadOnlyFallbackCacheDirectory { get => _store.FallbackRoot; set => _store.FallbackRoot = value; }
    public int PendingExtractionCount => _work.PendingCount;
    public string GetOriginalFramePath(string filePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())));
        return Path.Combine(_cacheDirectory, "media_v2", hash, "frame-" + (_store.Version(filePath) ?? "missing") + ".jpg");
    }
    public bool Exists(string filePath)
    {
        var version = _store.Version(filePath);
        var entry = version == null ? null : _store.Load(filePath, "original", version);
        return entry != null && ThumbnailCacheService.ValidEncodedImage(entry);
    }
    internal (int Width, int Height) TryGetDimensions(string path)
    {
        var version = _store.Version(path);
        var entry = version == null ? null : _store.Load(path, "original", version);
        return entry != null && ThumbnailCacheService.ValidEncodedImage(entry) ? (entry.Width, entry.Height) : (0, 0);
    }

    internal Task<(VersionedMediaStore.Entry? Entry, PreviewPersistence Persistence)> LoadFrameAsync(string path, CancellationToken ct)
    {
        var version = _store.Version(path);
        if (version == null) return Task.FromResult<(VersionedMediaStore.Entry?, PreviewPersistence)>((null, PreviewPersistence.NotApplicable));
        return _work.RunAsync($"{path}|{version}", async token =>
        {
            var cached = _store.Load(path, "original", version);
            if (cached != null && ThumbnailCacheService.ValidEncodedImage(cached)) return (cached, PreviewPersistence.Saved);
            var bytes = await _extractFrame(path, token).ConfigureAwait(false);
            if (bytes == null || _store.Version(path) != version) return (null, PreviewPersistence.NotApplicable);
            var dimensions = ThumbnailGenerator.GetDimensions(bytes);
            var entry = new VersionedMediaStore.Entry(version, bytes, dimensions.Width, dimensions.Height, Origin: ArtworkOrigin.VideoFrame);
            if (!ThumbnailCacheService.ValidEncodedImage(entry)) return (null, PreviewPersistence.NotApplicable);
            return (entry, _store.Save(path, "original", entry));
        }, ct, priority: -10);
    }

    public async Task<VideoOriginalFrameResult> EnsureAsync(string filePath, CancellationToken ct = default,
        bool cancelExtractionOnCancellation = false)
    {
        var existed = Exists(filePath);
        var frame = await LoadFrameAsync(filePath, ct).ConfigureAwait(false);
        if (frame.Entry?.Data == null) return new(false, false, null, "无法提取视频帧或源已变化");
        var path = GetOriginalFramePath(filePath);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_store.ExportFrame(filePath, frame.Entry, path)) return new(false, false, null, "源已变化");
            return new(true, existed, path, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, false, null, ex.Message); }
    }
    public void Dispose() => _work.Dispose();
}
