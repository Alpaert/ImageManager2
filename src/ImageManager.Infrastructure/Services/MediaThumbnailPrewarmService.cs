using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Video;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Low-priority cache warmer. Video work is deliberately limited to existing
/// original-frame cache entries so background warming can never start FFmpeg.
/// </summary>
public sealed class MediaThumbnailPrewarmService : IMediaThumbnailPrewarmService
{
    private readonly ThumbnailCacheService _thumbnailCache;
    private readonly VideoOriginalFrameCacheService _videoOriginalFrames;

    public MediaThumbnailPrewarmService(
        ThumbnailCacheService thumbnailCache,
        VideoOriginalFrameCacheService videoOriginalFrames)
    {
        _thumbnailCache = thumbnailCache;
        _videoOriginalFrames = videoOriginalFrames;
    }

    public async Task<MediaThumbnailPrewarmResult> PrewarmAsync(
        IReadOnlyCollection<string> filePaths,
        int decodeWidth,
        IProgress<MediaThumbnailPrewarmProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && FileTypeConstants.IsMediaFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var counts = new Counts(paths.Length);
        Report(progress, counts, null);

        await Parallel.ForEachAsync(paths, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = cancellationToken
        }, async (path, ct) =>
        {
            try
            {
                ThumbnailCacheLoadResult result;
                if (FileTypeConstants.IsVideoFile(path))
                {
                    var originalFramePath = _videoOriginalFrames.GetOriginalFramePath(path);
                    var hadOriginalFrame = _videoOriginalFrames.Exists(path);
                    result = await _thumbnailCache.TryCreateThumbnailFromExistingVideoOriginalFrameAsync(
                        path, originalFramePath, decodeWidth, ct).ConfigureAwait(false);
                    if (result.Data is null && !hadOriginalFrame)
                        counts.IncrementSkippedVideos();
                    else
                        counts.Record(result);
                }
                else
                {
                    result = await _thumbnailCache.GetOrCreateThumbnailWithDiagnosticsAsync(path, decodeWidth, ct)
                        .ConfigureAwait(false);
                    counts.Record(result);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                counts.IncrementFailed();
            }
            finally
            {
                counts.IncrementCompleted();
                Report(progress, counts, Path.GetFileName(path));
            }
        }).ConfigureAwait(false);

        return counts.ToResult();
    }

    private static void Report(IProgress<MediaThumbnailPrewarmProgress>? progress, Counts counts, string? currentFileName)
    {
        if (progress == null)
            return;
        progress.Report(counts.ToProgress(currentFileName));
    }

    private sealed class Counts(int total)
    {
        private int _completed;
        private int _cached;
        private int _generated;
        private int _noPreview;
        private int _skippedVideos;
        private int _failed;

        public void Record(ThumbnailCacheLoadResult result)
        {
            if (result.IsFinalWithoutThumbnail)
            {
                Interlocked.Increment(ref _noPreview);
                return;
            }
            if (result.Data is null)
            {
                Interlocked.Increment(ref _failed);
                return;
            }
            if (result.Source is ThumbnailCacheSource.Memory or ThumbnailCacheSource.Disk)
                Interlocked.Increment(ref _cached);
            else if (result.Source == ThumbnailCacheSource.Generated)
                Interlocked.Increment(ref _generated);
            else
                Interlocked.Increment(ref _failed);
        }

        public void IncrementCompleted() => Interlocked.Increment(ref _completed);
        public void IncrementSkippedVideos() => Interlocked.Increment(ref _skippedVideos);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);

        public MediaThumbnailPrewarmProgress ToProgress(string? currentFileName) => new(
            Volatile.Read(ref _completed), total, Volatile.Read(ref _cached), Volatile.Read(ref _generated),
            Volatile.Read(ref _noPreview), Volatile.Read(ref _skippedVideos), Volatile.Read(ref _failed), currentFileName);

        public MediaThumbnailPrewarmResult ToResult() => new(
            total, Volatile.Read(ref _completed), Volatile.Read(ref _cached), Volatile.Read(ref _generated),
            Volatile.Read(ref _noPreview), Volatile.Read(ref _skippedVideos), Volatile.Read(ref _failed));
    }
}
