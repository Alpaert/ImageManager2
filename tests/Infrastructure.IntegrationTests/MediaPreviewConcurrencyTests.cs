using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Imaging;
using SkiaSharp;
using Xunit;
using ImageManager.Infrastructure.Video;
using ImageManager.Infrastructure.Services;
using System.Collections.Concurrent;

namespace Infrastructure.IntegrationTests;

public sealed class MediaPreviewConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IM2-preview-" + Guid.NewGuid().ToString("N"));
    public MediaPreviewConcurrencyTests() => Directory.CreateDirectory(_root);
    private string Source(string name = "music.mp3")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "synthetic test input");
        return path;
    }
    private sealed class Processor : IMediaProcessor, IMediaProcessorFactory
    {
        public int Calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block;
        public bool NoArtwork;
        public bool Fail;
        public IMediaProcessor GetProcessor(string path) => this;
        public bool CanHandle(string extension) => true;
        public (int Width, int Height) GetDimensions(string path) => (640, 320);
        public async Task<MediaResult?> ExtractThumbnailAsync(string path, int width, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(ct);
            if (Fail) return null;
            if (NoArtwork) return new() { IsFinalWithoutThumbnail = true };
            using var bitmap = new SKBitmap(640, 320);
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return new() { Data = data.ToArray(), Width = 640, Height = 320 };
        }
    }

    [Fact]
    public async Task DifferentSizesShareExtractionAndOneCanceledSubscriberDoesNotCancelOther()
    {
        var source = Source();
        var processor = new Processor { Block = true };
        using var cache = new ThumbnailCacheService(processor, Path.Combine(_root, "cache"));
        using var cancellation = new CancellationTokenSource();
        var small = cache.LoadPreviewAsync(source, 96, cancellation.Token);
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var large = cache.LoadPreviewAsync(source, 320);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => small);
        processor.Release.TrySetResult();
        var result = await large.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MediaPreviewState.Ready, result.State);
        Assert.Equal(320, ThumbnailGenerator.GetDimensions(result.Data!).Width);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task InvalidationDuringExtractionRejectsLateResultAndSurvivesNewService()
    {
        var source = Source();
        var processor = new Processor { Block = true };
        var root = Path.Combine(_root, "cache");
        using var cache = new ThumbnailCacheService(processor, root);
        var previous = cache.GetPreviewVersion(source);
        var pending = cache.LoadPreviewAsync(source, 160);
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.InvalidateThumbnail(source);
        processor.Release.TrySetResult();
        Assert.Equal(MediaPreviewState.Failed, (await pending).State);
        using var reopened = new ThumbnailCacheService(processor, root);
        Assert.NotEqual(previous, reopened.GetPreviewVersion(source));
        Assert.Null(reopened.TryGetCachedThumbnail(source, 160).Data);
        Assert.Equal(MediaPreviewState.Ready, (await reopened.LoadPreviewAsync(source, 160)).State);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task SaveFailureKeepsImageAndRetryDoesNotExtractAgain()
    {
        var source = Source();
        var root = Path.Combine(_root, "blocked-cache");
        File.WriteAllText(root, "prevent directory creation");
        var processor = new Processor();
        using var cache = new ThumbnailCacheService(processor, root);
        var preview = await cache.LoadPreviewAsync(source, 160);
        Assert.Equal(MediaPreviewState.Ready, preview.State);
        Assert.Equal(PreviewPersistence.MemoryOnly, preview.Persistence);
        Assert.NotNull(preview.RetryToken);
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 320)).State);
        Assert.Equal(1, processor.Calls); // Reselecting/resizing a memory-only result reuses its primary artifact.
        File.Delete(root);
        var saved = await cache.RetrySavePreviewAsync(source, preview.RetryToken!);
        Assert.Equal(PreviewPersistence.Saved, saved.Persistence);
        Assert.Equal(1, processor.Calls);
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 320)).State);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task ImageSaveFailureSurvivesMemoryHitsAndRetriesTheCorrectWidthWithoutDecoding()
    {
        var source = Source("unsaved-picture.png");
        var root = Path.Combine(_root, "blocked-image-cache");
        File.WriteAllText(root, "not a directory");
        var processor = new Processor();
        using var cache = new ThumbnailCacheService(processor, root);
        var first = await cache.LoadPreviewAsync(source, 160);
        Assert.Equal(MediaPreviewState.Ready, first.State);
        Assert.Equal(PreviewPersistence.MemoryOnly, first.Persistence);
        Assert.NotNull(first.RetryToken);
        var memoryHit = await cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160);
        Assert.Equal(ThumbnailCacheSource.Memory, memoryHit.Source);
        Assert.Equal(PreviewPersistence.MemoryOnly, memoryHit.Persistence);
        Assert.Equal(first.RetryToken, memoryHit.RetryToken);
        cache.ClearMemoryEntry(source);
        var retained = await cache.LoadPreviewAsync(source, 160);
        Assert.Equal(PreviewPersistence.MemoryOnly, retained.Persistence);
        Assert.Equal(1, processor.Calls);

        File.Delete(root);
        var saved = await cache.RetrySavePreviewAsync(source, first.RetryToken!);
        Assert.Equal(PreviewPersistence.Saved, saved.Persistence);
        Assert.Equal(PreviewPersistence.Saved, (await cache.LoadPreviewAsync(source, 160)).Persistence);
        using var restarted = new ThumbnailCacheService(processor, root);
        var diskHit = await restarted.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160);
        Assert.Equal(ThumbnailCacheSource.Disk, diskHit.Source);
        Assert.Equal(PreviewPersistence.Saved, diskHit.Persistence);
        Assert.NotNull(diskHit.Data);
        Assert.Equal(1, processor.Calls);
        Assert.Single(Directory.EnumerateFiles(root, "w160.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(root, "cover.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImageLruRecreatesAnEvictedRetryHandleWithoutDecodingAgain()
    {
        var a = Source("retry-a.png");
        var b = Source("retry-b.png");
        var c = Source("retry-c.png");
        var root = Path.Combine(_root, "blocked-three-image-cache");
        File.WriteAllText(root, "not a directory");
        var processor = new Processor();
        using var cache = new ThumbnailCacheService(processor, root);
        var firstA = await cache.LoadPreviewAsync(a, 160);
        await cache.LoadPreviewAsync(b, 160);
        await cache.LoadPreviewAsync(c, 160);
        Assert.Equal(PreviewPersistence.MemoryOnly, firstA.Persistence);
        Assert.NotNull(firstA.RetryToken);
        Assert.Equal(MediaPreviewState.Failed, (await cache.RetrySavePreviewAsync(a, firstA.RetryToken!)).State);

        var reselectedA = await cache.LoadPreviewAsync(a, 160);
        Assert.Equal(PreviewPersistence.MemoryOnly, reselectedA.Persistence);
        Assert.NotNull(reselectedA.RetryToken);
        Assert.NotEqual(firstA.RetryToken, reselectedA.RetryToken);
        Assert.Equal(3, processor.Calls);
        File.Delete(root);
        Assert.Equal(PreviewPersistence.Saved,
            (await cache.RetrySavePreviewAsync(a, reselectedA.RetryToken!)).Persistence);
        Assert.Equal(PreviewPersistence.Saved, (await cache.LoadPreviewAsync(a, 160)).Persistence);
        using var restarted = new ThumbnailCacheService(processor, root);
        Assert.Equal(ThumbnailCacheSource.Disk,
            (await restarted.GetOrCreateThumbnailWithDiagnosticsAsync(a, 160)).Source);
        Assert.Equal(3, processor.Calls);
    }

    [Fact]
    public async Task NoArtworkIsVersionedAndDifferentSourceMustBeReadAgain()
    {
        var source = Source();
        var processor = new Processor { NoArtwork = true };
        using var cache = new ThumbnailCacheService(processor, Path.Combine(_root, "cache"));
        Assert.Equal(MediaPreviewState.NoArtwork, (await cache.LoadPreviewAsync(source, 160)).State);
        Assert.Equal(MediaPreviewState.NoArtwork, (await cache.LoadPreviewAsync(source, 320)).State);
        Assert.Equal(1, processor.Calls);
        File.AppendAllText(source, "new version");
        await cache.LoadPreviewAsync(source, 160);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task CorruptEnvelopeRebuildsWithoutAcceptingLegacyFallback()
    {
        var source = Source();
        var processor = new Processor();
        var root = Path.Combine(_root, "cache");
        using var cache = new ThumbnailCacheService(processor, root);
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 160)).State);
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)) File.WriteAllText(file, "broken");
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 160)).State);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task ImageInvalidatedDuringDecodeCannotRepopulateDiskOrFallback()
    {
        var source = Source("picture.png");
        var processor = new Processor { Block = true };
        var root = Path.Combine(_root, "cache");
        var fallback = Path.Combine(_root, "fallback");
        new DiskThumbnailCache(fallback, 160).Save(source, [1, 2, 3]);
        using var cache = new ThumbnailCacheService(processor, root) { ReadOnlyFallbackCacheDirectory = fallback };
        var pending = cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160);
        await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cache.TryInvalidatePreview(source));
        processor.Release.TrySetResult();
        Assert.Null((await pending).Data);
        Assert.Null(cache.TryGetCachedThumbnail(source, 160).Data);
        using var restarted = new ThumbnailCacheService(processor, root) { ReadOnlyFallbackCacheDirectory = fallback };
        Assert.NotNull((await restarted.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task ValidVersionedFallbackIsReusableButForcedInvalidationSurvivesRestart()
    {
        var source = Source("photo.png");
        var processor = new Processor();
        var oldRoot = Path.Combine(_root, "old-cache");
        var newRoot = Path.Combine(_root, "new-cache");
        using (var old = new ThumbnailCacheService(processor, oldRoot))
            Assert.NotNull((await old.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
        using (var current = new ThumbnailCacheService(processor, newRoot) { ReadOnlyFallbackCacheDirectory = oldRoot })
        {
            Assert.NotNull((await current.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
            Assert.Equal(1, processor.Calls);
            Assert.True(current.TryInvalidatePreview(source));
            Assert.Null(current.TryGetCachedThumbnail(source, 160).Data);
        }
        using var restarted = new ThumbnailCacheService(processor, newRoot) { ReadOnlyFallbackCacheDirectory = oldRoot };
        Assert.Null(restarted.TryGetCachedThumbnail(source, 160).Data);
        Assert.NotNull((await restarted.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task CachedDimensionsUseAnyVerifiedWidthAndRejectChangedSourceAndLegacyMetadata()
    {
        var source = Source("dimensions.png");
        var processor = new Processor();
        var root = Path.Combine(_root, "cache");
        using var cache = new ThumbnailCacheService(processor, root);
        await cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 96);
        var wrongLegacy = new DiskThumbnailCache(root, 512);
        wrongLegacy.Save(source, Artwork());
        wrongLegacy.SaveMeta(source, 12, 9999);
        // No w512 entry exists in v2: reuse the source dimensions of the verified w96 entry.
        Assert.Equal((640, 320), cache.TryResolveCachedDimensions(source, 512));
        File.AppendAllText(source, "a different source version");
        Assert.Equal((0, 0), cache.TryResolveCachedDimensions(source, 512));
        Assert.Equal(1, processor.Calls); // Dimension lookup must not start extraction to repair the cache.
        await cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 128);
        Assert.Equal((640, 320), cache.TryResolveCachedDimensions(source, 512));
    }

    [Fact]
    public async Task BackgroundFailureBackoffDoesNotBlockAnExplicitPreviewRetry()
    {
        var source = Source();
        var processor = new Processor { Fail = true };
        using var cache = new ThumbnailCacheService(processor, Path.Combine(_root, "cache"));
        Assert.Null((await cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
        Assert.Null((await cache.GetOrCreateThumbnailWithDiagnosticsAsync(source, 160)).Data);
        Assert.Equal(1, processor.Calls);
        processor.Fail = false;
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 160)).State);
        Assert.Equal(2, processor.Calls);
    }

    [Fact]
    public async Task TempCleanupOnlyReclaimsOldRecognizedUnownedFilesUnderItsCommitLock()
    {
        var source = Source();
        var root = Path.Combine(_root, "cache");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())));
        var directory = Path.Combine(root, "media_v2", hash);
        Directory.CreateDirectory(directory);
        string MakeTemp(string name, bool old)
        {
            var file = Path.Combine(directory, name);
            File.WriteAllText(file, "orphan candidate");
            if (old) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            return file;
        }
        var abandoned = MakeTemp("cover.json." + Guid.NewGuid().ToString("N") + ".tmp", true);
        var recent = MakeTemp("cover.json." + Guid.NewGuid().ToString("N") + ".tmp", false);
        var foreign = MakeTemp("user-notes.tmp", true);
        var active = MakeTemp("original.json." + Guid.NewGuid().ToString("N") + ".tmp", true);
        using var activeHandle = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cache = new ThumbnailCacheService(new Processor(), root);
        Assert.Equal(MediaPreviewState.Ready, (await cache.LoadPreviewAsync(source, 160)).State);
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(active));
    }

    private static byte[] Artwork()
    {
        using var bitmap = new SKBitmap(320, 160);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task CancelingPaneDetachesItsNotYetStartedOriginalFrame()
    {
        var a = Source("a.mp4"); var b = Source("b.mp4"); var abandoned = Source("abandoned.mp4");
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var root = Path.Combine(_root, "cache");
        using var frames = new VideoOriginalFrameCacheService(root, async (path, ct) =>
        {
            calls.Enqueue(path);
            if (path == a || path == b) await released.Task.WaitAsync(ct);
            return Artwork();
        });
        using var cache = new ThumbnailCacheService(new MediaProcessorFactory(frames), root);
        var first = frames.EnsureAsync(a); var second = frames.EnsureAsync(b);
        await UntilAsync(() => calls.Count == 2);
        using var cancellation = new CancellationTokenSource();
        var pane = cache.LoadPreviewAsync(abandoned, 160, cancellation.Token);
        await UntilAsync(() => frames.PendingExtractionCount == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pane);
        await UntilAsync(() => frames.PendingExtractionCount == 0);
        released.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.DoesNotContain(abandoned, calls);
    }

    [Fact]
    public async Task PaneRequestRunsBeforePreviouslyQueuedBackgroundFrame()
    {
        var a = Source("a.mp4"); var b = Source("b.mp4");
        var background = Source("background.mp4"); var interactive = Source("interactive.mp4");
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInteractive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var root = Path.Combine(_root, "cache");
        using var frames = new VideoOriginalFrameCacheService(root, async (path, ct) =>
        {
            calls.Enqueue(path);
            if (path == a) await releaseA.Task.WaitAsync(ct);
            if (path == b) await releaseB.Task.WaitAsync(ct);
            if (path == interactive) await releaseInteractive.Task.WaitAsync(ct);
            return Artwork();
        });
        using var cache = new ThumbnailCacheService(new MediaProcessorFactory(frames), root);
        var first = frames.EnsureAsync(a); var second = frames.EnsureAsync(b);
        await UntilAsync(() => calls.Count == 2);
        var batch = frames.EnsureAsync(background);
        await UntilAsync(() => frames.PendingExtractionCount == 1);
        var pane = cache.LoadPreviewAsync(interactive, 160);
        await UntilAsync(() => frames.PendingExtractionCount == 2);
        releaseA.TrySetResult();
        await UntilAsync(() => calls.Contains(interactive));
        Assert.DoesNotContain(background, calls);
        releaseB.TrySetResult(); releaseInteractive.TrySetResult();
        await Task.WhenAll(first, second, batch);
        Assert.Equal(MediaPreviewState.Ready, (await pane).State);
    }

    [Fact]
    public void CorruptFrontCoverDoesNotHideAnotherValidEmbeddedPicture()
    {
        TagLib.IPicture[] pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(new byte[] { 1, 2, 3 })) { Type = TagLib.PictureType.FrontCover },
            new TagLib.Picture(new TagLib.ByteVector(Artwork())) { Type = TagLib.PictureType.BackCover }
        ];
        var method = typeof(ImageManager.Infrastructure.Audio.MusicMediaProcessor).GetMethod("DecodeEmbeddedArtwork",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var jpeg = Assert.IsType<byte[]>(method.Invoke(null, [pictures]));
        Assert.Equal(320, ThumbnailGenerator.GetDimensions(jpeg).Width);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
