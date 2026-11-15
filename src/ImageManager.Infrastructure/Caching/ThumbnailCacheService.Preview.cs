using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Audio;
using ImageManager.Infrastructure.Imaging;
using SkiaSharp;

namespace ImageManager.Infrastructure.Caching;

public partial class ThumbnailCacheService : IMediaPreviewService, IDisposable
{
    private readonly SharedMediaWork<MediaPreviewResult> _previewWork = new(cancelRunningWhenUnobserved: true);
    private readonly SharedMediaWork<(VersionedMediaStore.Entry? Entry, PreviewPersistence Persistence)> _primaryWork = new(cancelRunningWhenUnobserved: true);
    private readonly object _retryGate = new();
    private sealed record RetryArtifact(string Token, string Path, VersionedMediaStore.Entry Entry, string Kind)
    {
        public DateTime RetryAfter = DateTime.UtcNow.AddSeconds(2);
    }
    private readonly LinkedList<RetryArtifact> _retry = new();
    private readonly LinkedList<(string Key, MediaPreviewResult Result, DateTime Until)> _failures = new();
    private VersionedMediaStore PreviewStore => new(CacheDirectory, ReadOnlyFallbackCacheDirectory);

    public string? GetPreviewVersion(string path) => PreviewStore.Version(path);
    public bool IsPreviewVersionCurrent(string path, string? version) => version != null && version == GetPreviewVersion(path);
    public bool IsKnownPreviewVersionCurrent(string path, string? version) => PreviewStore.IsKnownCurrent(path, version);
    private static bool IsCoverMedia(string path) => FileTypeConstants.IsVideoFile(path) || FileTypeConstants.IsMusicFile(path);

    public async Task<MediaPreviewResult> LoadPreviewAsync(string path, int decodeWidth, CancellationToken ct = default)
        => await LoadPreviewAtPriorityAsync(path, decodeWidth, ct, 10).ConfigureAwait(false);

    private async Task<MediaPreviewResult> LoadPreviewAtPriorityAsync(string path, int decodeWidth, CancellationToken ct, int priority)
    {
        ct.ThrowIfCancellationRequested();
        var failureKey = path.ToUpperInvariant() + "|" + GetPreviewVersion(path);
        if (priority <= 0)
        {
            lock (_retryGate)
            {
                var recent = _failures.FirstOrDefault(item => item.Key == failureKey && item.Until > DateTime.UtcNow);
                if (recent.Result != null) return recent.Result;
            }
        }
        MediaPreviewResult result;
        try { result = await LoadPreviewCoreAsync(path, decodeWidth, ct, priority).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException ex) { result = new(MediaPreviewState.Failed, Error: "文件或媒体工具不存在：" + ex.Message); }
        catch (UnauthorizedAccessException) { result = new(MediaPreviewState.Failed, Error: "没有读取权限"); }
        catch (TimeoutException) { result = new(MediaPreviewState.Failed, Error: "媒体读取超时，请重试"); }
        catch (Exception ex) { result = new(MediaPreviewState.Failed, Error: "无法读取封面：" + ex.Message); }
        lock (_retryGate)
        {
            for (var item = _failures.First; item != null;)
            {
                var next = item.Next;
                if (item.Value.Key == failureKey || item.Value.Until <= DateTime.UtcNow) _failures.Remove(item);
                item = next;
            }
            if (result.State == MediaPreviewState.Failed)
                _failures.AddFirst((failureKey, result, DateTime.UtcNow.AddSeconds(2)));
            while (_failures.Count > 64) _failures.RemoveLast();
        }
        return result;
    }

    private Task<MediaPreviewResult> LoadPreviewCoreAsync(string path, int decodeWidth, CancellationToken ct, int priority)
    {
        var version = GetPreviewVersion(path);
        if (version == null) return Task.FromResult(new MediaPreviewResult(MediaPreviewState.Failed, Error: "文件不存在或无法访问"));
        decodeWidth = Math.Clamp(decodeWidth, 32, 2048);
        return _previewWork.RunAsync($"{path}|{version}|{decodeWidth}", async token =>
        {
            if (!IsCoverMedia(path))
            {
                var image = await GetOrCreateThumbnailWithDiagnosticsAsync(path, decodeWidth, token).ConfigureAwait(false);
                if (!IsPreviewVersionCurrent(path, version) || image.Data is not { Length: > 0 })
                    return new(MediaPreviewState.Failed, Error: "无法解码图片或源已变化");
                var size = ThumbnailGenerator.GetDimensions(image.Data);
                return new(MediaPreviewState.Ready, image.Data, size.Width, size.Height, image.Persistence,
                    ArtworkOrigin.Image, version, image.RetryToken,
                    image.Persistence == PreviewPersistence.MemoryOnly ? "图片已显示，暂未保存" : null);
            }
            var store = PreviewStore;
            var kind = "w" + decodeWidth;
            var cached = store.Load(path, kind, version);
            if (cached != null && ValidEncodedImage(cached)) return ToResult(cached, PreviewPersistence.Saved);
            var primary = await _primaryWork.RunAsync($"{path}|{version}", async extractionToken =>
            {
                var existing = store.Load(path, "cover", version);
                if (existing != null && ValidEncodedImage(existing)) return (existing, PreviewPersistence.Saved);
                var retained = FindRetry(path, version);
                if (retained != null)
                {
                    var persistence = PreviewPersistence.MemoryOnly;
                    if (DateTime.UtcNow >= retained.RetryAfter)
                    {
                        persistence = store.Save(path, "cover", retained.Entry);
                        retained.RetryAfter = DateTime.UtcNow.AddSeconds(2);
                        if (persistence == PreviewPersistence.Saved) RemoveRetry(path);
                    }
                    return (retained.Entry, persistence);
                }
                VersionedMediaStore.Entry? entry;
                var processor = _factory.GetProcessor(path);
                if (processor is MusicMediaProcessor music)
                {
                    var artwork = await music.ExtractArtworkAsync(path, extractionToken).ConfigureAwait(false);
                    entry = artwork.Result == null ? null : new(version, artwork.Result.Data, artwork.Result.Width,
                        artwork.Result.Height, artwork.Result.IsFinalWithoutThumbnail, artwork.Origin);
                }
                else
                {
                    var result = await processor.ExtractThumbnailAsync(path, 2048, extractionToken).ConfigureAwait(false);
                    entry = result == null ? null : new(version, result.Data, result.Width, result.Height,
                        result.IsFinalWithoutThumbnail, FileTypeConstants.IsVideoFile(path) ? ArtworkOrigin.VideoFrame : ArtworkOrigin.Embedded);
                }
                if (entry == null || !ValidEncodedImage(entry) || store.Version(path) != version) return (null, PreviewPersistence.NotApplicable);
                return (entry, store.Save(path, "cover", entry));
            }, token, continueWhenUnobserved: FileTypeConstants.IsMusicFile(path)).ConfigureAwait(false);
            if (primary.Entry == null || !IsPreviewVersionCurrent(path, version))
                return new(MediaPreviewState.Failed, Version: version, Error: "无法读取封面、媒体工具不可用或源已变化");
            if (primary.Entry.NoArtwork)
                return ToResult(primary.Entry, primary.Persistence) with
                { RetryToken = primary.Persistence == PreviewPersistence.MemoryOnly ? RememberRetry(path, primary.Entry) : null };
            var bytes = SafeResize(primary.Entry.Data!, decodeWidth);
            if (bytes == null) return new(MediaPreviewState.Failed, Version: version, Error: "封面损坏或超过解码预算");
            // Source dimensions remain available to gallery layout; the preview itself is encoded at requested width.
            var derived = primary.Entry with { Data = bytes };
            var pendingSave = FindRetry(path, version);
            var saved = primary.Persistence == PreviewPersistence.MemoryOnly && pendingSave != null && DateTime.UtcNow < pendingSave.RetryAfter
                ? PreviewPersistence.MemoryOnly : store.Save(path, kind, derived);
            if (!IsPreviewVersionCurrent(path, version)) return new(MediaPreviewState.Failed, Error: "源已变化，请重新加载");
            var persistence = saved == PreviewPersistence.Saved && primary.Persistence == PreviewPersistence.Saved
                ? PreviewPersistence.Saved : PreviewPersistence.MemoryOnly;
            var retry = persistence == PreviewPersistence.MemoryOnly ? RememberRetry(path, primary.Entry) : null;
            return ToResult(derived, persistence) with { RetryToken = retry, Error = persistence == PreviewPersistence.MemoryOnly ? "封面已显示，暂未保存" : null };
        }, ct, priority);
    }

    public Task<MediaPreviewResult> RetrySavePreviewAsync(string path, string retryToken, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            RetryArtifact? artifact;
            lock (_retryGate) artifact = _retry.FirstOrDefault(item => item.Token == retryToken && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (artifact == null || !IsPreviewVersionCurrent(path, artifact.Entry.Version))
                return new MediaPreviewResult(MediaPreviewState.Failed, Error: "产物已释放或源已变化，请重新加载");
            var entry = artifact.Entry;
            var persistence = PreviewStore.Save(path, artifact.Kind, entry);
            if (persistence == PreviewPersistence.NotApplicable) return new(MediaPreviewState.Failed, Error: "源已变化，请重新加载");
            if (persistence == PreviewPersistence.Saved)
            {
                RemoveRetry(path, artifact.Kind);
                lock (_lruLock)
                {
                    if (_index.TryGetValue(path, out var node) && node.Value.Version == entry.Version &&
                        artifact.Kind == "w" + node.Value.DecodeWidth)
                    {
                        node.Value.Persistence = PreviewPersistence.Saved;
                        node.Value.RetryToken = null;
                    }
                }
            }
            else artifact.RetryAfter = DateTime.UtcNow.AddSeconds(2);
            return ToResult(entry, persistence) with { RetryToken = persistence == PreviewPersistence.Saved ? null : retryToken };
        }, ct);

    private string? RememberRetry(string path, VersionedMediaStore.Entry entry, string kind = "cover")
    {
        if ((entry.Data?.Length ?? 0) > 32 * 1024 * 1024) return null;
        lock (_retryGate)
        {
            var existing = FindRetry(path, entry.Version, kind);
            if (existing != null) return existing.Token;
            RemoveRetry(path, kind);
            var token = Guid.NewGuid().ToString("N");
            _retry.AddFirst(new RetryArtifact(token, path, entry, kind));
            while (_retry.Count > 2 || _retry.Sum(x => (long)(x.Entry.Data?.Length ?? 0)) > 32L * 1024 * 1024) _retry.RemoveLast();
            return token;
        }
    }
    private RetryArtifact? FindRetry(string path, string version, string kind = "cover")
    {
        lock (_retryGate)
        {
            for (var item = _retry.First; item != null; item = item.Next)
            {
                if (!string.Equals(item.Value.Path, path, StringComparison.OrdinalIgnoreCase) || item.Value.Entry.Version != version || item.Value.Kind != kind) continue;
                _retry.Remove(item);
                _retry.AddFirst(item);
                return item.Value;
            }
            return null;
        }
    }
    private void RemoveRetry(string path, string? kind = null)
    {
        lock (_retryGate)
        {
            for (var item = _retry.First; item != null;)
            {
                var next = item.Next;
                if (string.Equals(item.Value.Path, path, StringComparison.OrdinalIgnoreCase) && (kind == null || kind == item.Value.Kind)) _retry.Remove(item);
                item = next;
            }
        }
    }
    private static MediaPreviewResult ToResult(VersionedMediaStore.Entry entry, PreviewPersistence persistence)
    {
        var size = entry.Data is { Length: > 0 } ? ThumbnailGenerator.GetDimensions(entry.Data) : (Width: 0, Height: 0);
        return new(entry.NoArtwork ? MediaPreviewState.NoArtwork : MediaPreviewState.Ready, entry.Data, size.Width,
            size.Height, persistence, entry.Origin, entry.Version);
    }

    internal static bool ValidEncodedImage(VersionedMediaStore.Entry entry)
    {
        if (entry.NoArtwork) return true;
        if (entry.Data is not { Length: > 0 } || entry.Data.Length > 32 * 1024 * 1024) return false;
        using var stream = new MemoryStream(entry.Data, false);
        using var codec = SKCodec.Create(stream);
        return codec != null && codec.Info.Width > 0 && codec.Info.Height > 0 &&
            (long)codec.Info.Width * codec.Info.Height <= 40_000_000;
    }
    internal static byte[]? SafeResize(byte[] bytes, int width)
    {
        if (!ValidEncodedImage(new("", bytes, 1, 1))) return null;
        return ThumbnailGenerator.Generate(bytes, width);
    }

    private async Task<ThumbnailCacheLoadResult> LoadCoverDiagnosticsAsync(string path, int width, CancellationToken ct)
    {
        var initialVersion = GetPreviewVersion(path);
        var wasCached = initialVersion != null && PreviewStore.Load(path, "w" + Math.Clamp(width, 32, 2048), initialVersion) != null;
        var result = await LoadPreviewAtPriorityAsync(path, width, ct, 0).ConfigureAwait(false);
        var source = result.Version == null ? null : PreviewStore.Load(path, "cover", result.Version);
        return new(result.Data, source?.Width ?? result.Width, source?.Height ?? result.Height, result.State == MediaPreviewState.Failed ? ThumbnailCacheSource.Missing :
            wasCached ? ThumbnailCacheSource.Disk : ThumbnailCacheSource.Generated,
            0, 0, 0, 0, result.State == MediaPreviewState.NoArtwork, result.Version, result.Persistence, result.RetryToken);
    }

    private Task<ThumbnailCacheLoadResult> TryCreateValidatedVideoThumbnailAsync(string path, int width, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            width = Math.Clamp(width, 32, 2048);
            var store = PreviewStore;
            var version = store.Version(path);
            var entry = version == null ? null : store.Load(path, "cover", version) ?? store.Load(path, "original", version);
            if (entry?.Data == null || !ValidEncodedImage(entry))
                return new ThumbnailCacheLoadResult(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
            var bytes = SafeResize(entry.Data, width);
            if (bytes == null || !IsPreviewVersionCurrent(path, version))
                return new ThumbnailCacheLoadResult(null, 0, 0, ThumbnailCacheSource.Missing, 0, 0, 0, 0);
            store.Save(path, "w" + width, entry with { Data = bytes });
            return new ThumbnailCacheLoadResult(bytes, entry.Width, entry.Height, ThumbnailCacheSource.Generated, 0, 0, 0, 0, Version: version);
        }, ct);

    public void Dispose()
    {
        _previewWork.Dispose();
        _primaryWork.Dispose();
        _imageWork.Dispose();
        lock (_retryGate) { _retry.Clear(); _failures.Clear(); }
    }
}
