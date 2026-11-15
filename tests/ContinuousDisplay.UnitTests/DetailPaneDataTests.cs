using System.Threading.Channels;
using System.Reflection;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Services;
using ImageManager.Core.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class DetailPaneDataTests
{
    private class ThumbnailSpy : IThumbnailCacheService
    {
        public int Generates;
        public int HeaderReads;
        public string CacheDirectory { get; set; } = string.Empty;
        public long EstimatedMemoryBytes => 0;
        public Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string path, int width, CancellationToken ct = default)
        { Generates++; return Task.FromResult<(byte[]?, int, int)>(([7], 32, 16)); }
        public (int Width, int Height) GetDimensionsOrUnknown(string path) { HeaderReads++; return (640, 480); }
        public (int Width, int Height) GetDimensions(string path) => throw new NotSupportedException();
        public Task ClearAsync() => Task.CompletedTask;
        public void Trim(long maxBytes, string? protectedKey = null) { }
        public void MoveDiskCache(string oldPath, string newPath) { }
        public void InvalidateThumbnail(string path) { }
        public byte[]? DecodeForHashInput(string path, int maxSize = 256) => throw new NotSupportedException();
        public Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class MediaPreviewSpy : ThumbnailSpy, IMediaPreviewService
    {
        public Task<MediaPreviewResult> LoadPreviewAsync(string path, int decodeWidth, CancellationToken ct = default)
            => Task.FromResult(new MediaPreviewResult(MediaPreviewState.Ready, [4],
                Origin: ArtworkOrigin.Embedded, Persistence: PreviewPersistence.Saved));

        public Task<MediaPreviewResult> RetrySavePreviewAsync(string path, string retryToken,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PreviewPublishesBeforeMetadataAndSurvivesMetadataFailure()
    {
        var repo = DispatchProxy.Create<IImageMetaReadRepository, TestMetaProxy>();
        var metadata = new TaskCompletionSource<ImageMeta?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((TestMetaProxy)(object)repo).CustomHandler = (method, _) =>
            method.Name == nameof(IImageMetaReadRepository.GetByPathAsync) ? metadata.Task : null;
        using var vm = new DetailPaneViewModel(new DetailPaneDataLoader(repo, new MediaPreviewSpy()),
            new ImmediateDispatcher());
        vm.Show("track.mp3", null, 320, 1);
        try
        {
            await Until(() => vm.ThumbnailItem?.ThumbnailData is { Length: > 0 });
            Assert.True(vm.IsLoading);
            Assert.Equal("内嵌封面", vm.ArtworkSourceText);
        }
        finally
        {
            metadata.TrySetException(new InvalidOperationException("metadata failure"));
        }
        await Until(() => !vm.IsLoading);
        Assert.NotNull(vm.ErrorText);
        Assert.Equal(new byte[] { 4 }, vm.ThumbnailItem!.ThumbnailData);
    }

    [Theory]
    [InlineData(1, 1, true, false)]
    [InlineData(1920, 1080, false, false)]
    [InlineData(0, 0, true, false)]
    [InlineData(1920, 1080, true, true)]
    public async Task VideoDimensionsRequireNonPlaceholderAndMatchingFile(int width, int height,
        bool current, bool known)
    {
        var path = Path.Combine(Path.GetTempPath(), $"detail-video-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            var file = new FileInfo(path);
            var repo = DispatchProxy.Create<IImageMetaReadRepository, TestMetaProxy>();
            ((TestMetaProxy)(object)repo).MetaByPath[path] = new ImageMeta
            {
                FilePath = path, Width = width, Height = height, FileSize = file.Length,
                LastWriteTicks = current ? file.LastWriteTimeUtc.Ticks : 0
            };
            var cache = new ThumbnailSpy();
            var data = await new DetailPaneDataLoader(repo, cache).LoadAsync(new(path, 320, true), CancellationToken.None);
            Assert.Equal(known ? width : (int?)null, data.Width);
            Assert.Equal(known ? height : (int?)null, data.Height);
            Assert.Equal(0, cache.HeaderReads);
            Assert.Equal(0, cache.Generates);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".mp3", true, 0, 0)]
    [InlineData(".mp4", true, 0, 0)]
    [InlineData(".jpg", true, 1, 1)]
    [InlineData(".jpg", false, 0, 1)]
    public async Task LoaderOnlyGeneratesOrdinaryImages_AndNeverUsesThumbnailDimensions(string extension,
        bool thumbnail, int generates, int headers)
    {
        var path = Path.Combine(Path.GetTempPath(), $"detail-test-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            var repo = DispatchProxy.Create<IImageMetaReadRepository, TestMetaProxy>();
            ((TestMetaProxy)(object)repo).MetaByPath[path] = new ImageMeta { FilePath = path };
            var cache = new ThumbnailSpy();
            var data = await new DetailPaneDataLoader(repo, cache).LoadAsync(new(path, 320, thumbnail), CancellationToken.None);
            Assert.Equal(generates, cache.Generates);
            Assert.Equal(headers, cache.HeaderReads);
            Assert.Equal(headers == 1 ? 640 : (int?)null, data.Width);
            Assert.Equal(1L, data.FileSize);
        }
        finally { File.Delete(path); }
    }

    private sealed class Read(DetailPaneLoadRequest request, CancellationToken cancellation,
        Action<FolderDetailsSnapshot> reportFolderProgress)
    {
        public DetailPaneLoadRequest Request { get; } = request;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<DetailPaneData> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Finish(string tag, byte[]? bytes = null) => Completion.TrySetResult(new([tag], ThumbnailData: bytes));
        public void ReportFolder(long files) => reportFolderProgress(new(Request.Path, 1, files, 1, 1, files,
            FolderStatisticsState.Scanning, DateTimeOffset.Now));
    }
    private sealed class ControlledLoader : IDetailPaneDataLoader
    {
        private readonly Channel<Read> _reads = Channel.CreateUnbounded<Read>();
        public int Count;
        public Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken)
            => LoadAsync(request, _ => Task.CompletedTask, _ => { }, cancellationToken);

        public async Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request,
            Func<MediaPreviewResult, Task> publishPreview, Action<FolderDetailsSnapshot> reportFolderProgress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            var read = new Read(request, cancellationToken, reportFolderProgress);
            await _reads.Writer.WriteAsync(read);
            // Intentionally ignores cancellation, like an already executing SQLite/native operation.
            return await read.Completion.Task;
        }
        public async Task<Read> Next() => await _reads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class DelayedDispatcher : IDispatcher
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();

        public Task InvokeAsync(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _callbacks.Writer.TryWrite(() =>
            {
                try { action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
            });
            return done.Task;
        }

        public Task<Action> NextAsync() => _callbacks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Post(Action action) => _callbacks.Writer.TryWrite(action);
        public Task InvokeAsync(Func<Task> callback) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(Func<T> callback) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(Func<Task<T>> callback) => throw new NotSupportedException();
    }

    private sealed class VersionedLoader : IDetailPaneDataLoader
    {
        public string CurrentVersion { get; set; } = "v1";
        public string ExpectedVersionPath { get; set; } = "a.jpg";
        public bool UsesVersionedPreview => true;
        public bool IsKnownPreviewVersionCurrent(string path, string version) =>
            path == ExpectedVersionPath && version == CurrentVersion;

        public Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, Func<MediaPreviewResult, Task> publishPreview,
            CancellationToken cancellationToken)
        {
            var preview = new MediaPreviewResult(MediaPreviewState.Ready, [1], Version: CurrentVersion);
            await publishPreview(preview);
            return new([], ThumbnailData: preview.Data, Media: preview);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationWhilePublicationIsQueuedRejectsOldCoverBeforeRefreshArrives(bool isFolder)
    {
        var loader = new VersionedLoader { ExpectedVersionPath = isFolder ? "album/a.jpg" : "a.jpg" };
        var dispatcher = new DelayedDispatcher();
        using var vm = new DetailPaneViewModel(loader, dispatcher);
        if (isFolder)
            vm.ShowFolder("album", coverMediaPath: loader.ExpectedVersionPath);
        else
            vm.Show("a.jpg", null, 320, 1);
        var publish = await dispatcher.NextAsync();

        loader.CurrentVersion = "v2";
        publish();
        Assert.Null(vm.ThumbnailItem!.ThumbnailData);
        (await dispatcher.NextAsync())();
        Assert.Null(vm.ThumbnailItem.ThumbnailData);

        vm.Refresh(reloadThumbnail: true);
        (await dispatcher.NextAsync())();
        Assert.NotNull(vm.ThumbnailItem.ThumbnailData);
        (await dispatcher.NextAsync())();
    }

    [Fact]
    public async Task FolderScanProgressShowsLowerBoundsAndRejectsQueuedUpdatesAfterSwitch()
    {
        var loader = new ControlledLoader();
        var dispatcher = new DelayedDispatcher();
        using var vm = new DetailPaneViewModel(loader, dispatcher);
        vm.ShowFolder("old", [1]);
        var old = await loader.Next();
        old.ReportFolder(5);
        (await dispatcher.NextAsync())();
        Assert.True(vm.IsLoading);
        Assert.Equal("至少 5", vm.FolderFileCount);
        Assert.Equal("至少 5 B", vm.FileSizeText);
        Assert.NotNull(vm.ThumbnailItem!.ThumbnailData);

        old.ReportFolder(42);
        var late = await dispatcher.NextAsync();
        vm.ShowFolder("new");
        late();
        Assert.Null(vm.Folder);
        Assert.Equal("未知", vm.FolderFileCount);
        old.Finish("");
        (await dispatcher.NextAsync())();
        var current = await loader.Next();
        current.Completion.SetResult(new([], Folder: new("new", 1, 3, 1, 1, 3,
            FolderStatisticsState.Complete, DateTimeOffset.Now)));
        (await dispatcher.NextAsync())();
        Assert.False(vm.IsLoading);
        Assert.Equal("3", vm.FolderFileCount);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for detail state");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task QuickABA_ExecutesOnlyRunningAndLatest_RejectsFirstA()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("a.jpg", null, 320, 1);
        var first = await loader.Next();
        for (var i = 0; i < 100; i++) vm.Show($"b{i}.jpg", null, 320, 1);
        vm.Show("a.jpg", null, 320, 1);
        Assert.Equal(1, loader.Count);
        Assert.True(first.Cancellation.IsCancellationRequested);
        first.Finish("stale", [1]);
        var latest = await loader.Next();
        Assert.Equal("a.jpg", latest.Request.Path);
        Assert.Empty(vm.Tags);
        Assert.Null(vm.ThumbnailItem!.ThumbnailData);
        latest.Finish("fresh", [2]);
        await Until(() => !vm.IsLoading);
        Assert.Equal("fresh", Assert.Single(vm.Tags));
        Assert.Equal(2, loader.Count);
    }

    [Fact]
    public async Task SourceUnloadKeepsSnapshot_CloseReleasesOwner_ReopenWaitsForOldRead()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        var bytes = new byte[] { 1, 2 };
        var source = new ImageViewItem { FilePath = "a.jpg", ThumbnailData = bytes };
        vm.Show("a.jpg", source, 320, 1);
        var first = await loader.Next();
        var owner = vm.ThumbnailItem!;
        Assert.NotSame(source, owner);
        Assert.Same(bytes, owner.ThumbnailData);
        Assert.False(first.Request.LoadThumbnail);
        source.ThumbnailData = null;
        Assert.Same(bytes, owner.ThumbnailData);
        vm.IsOpen = false;
        Assert.Null(owner.ThumbnailData);
        Assert.Null(vm.ThumbnailItem);
        Assert.Equal("a.jpg", vm.CurrentPath);
        vm.IsOpen = true;
        Assert.NotSame(owner, vm.ThumbnailItem);
        Assert.Equal(1, loader.Count);
        first.Finish("old", bytes);
        var reopened = await loader.Next();
        Assert.True(reopened.Request.LoadThumbnail);
        reopened.Finish("new");
        await Until(() => !vm.IsLoading);
        Assert.Equal("new", Assert.Single(vm.Tags));
    }

    [Fact]
    public async Task SamePathDoesNotReload_MetadataRefreshKeepsBytes_RefreshGenerationRejectsStale()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        var bytes = new byte[] { 1 };
        var source = new ImageViewItem { FilePath = "a.jpg", ThumbnailData = bytes };
        vm.Show("a.jpg", source, 320, 1);
        var first = await loader.Next();
        vm.Show("a.jpg", null, 320, 4);
        Assert.Equal(1, loader.Count);
        Assert.Contains("4", vm.SelectionHint);
        vm.Refresh();
        first.Finish("old");
        var refresh = await loader.Next();
        Assert.False(refresh.Request.LoadThumbnail);
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
        Assert.Empty(vm.Tags);
        refresh.Finish("new");
        await Until(() => !vm.IsLoading);
        Assert.Same(bytes, vm.ThumbnailItem.ThumbnailData);
        Assert.Equal("new", Assert.Single(vm.Tags));
    }

    [Fact]
    public async Task RetainedLoadingSourceWithoutPendingWorkFallsBack()
    {
        var loader = new ControlledLoader();
        var source = new ImageViewItem { FilePath = "a.jpg", IsLoading = true };
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher(), item =>
        {
            Assert.Same(source, item);
            return false;
        });
        vm.Show("a.jpg", source, 320, 1);
        var metadata = await loader.Next();
        Assert.False(metadata.Request.LoadThumbnail);
        metadata.Finish("metadata");
        var fallback = await loader.Next();
        Assert.True(source.IsLoading);
        Assert.True(fallback.Request.LoadThumbnail);
        var bytes = new byte[] { 8 };
        fallback.Finish("fallback", bytes);
        await Until(() => !vm.IsLoading);
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
        Assert.Equal(2, loader.Count);
        GC.KeepAlive(source);
    }

    [Fact]
    public async Task PendingSourceWorkKeepsWaitingWithoutDuplicateDecode()
    {
        var loader = new ControlledLoader();
        var checks = 0;
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher(), _ =>
        {
            Interlocked.Increment(ref checks);
            return true;
        });
        var source = new ImageViewItem { FilePath = "a.jpg", IsLoading = true };
        vm.Show("a.jpg", source, 320, 1);
        var metadata = await loader.Next();
        Assert.False(metadata.Request.LoadThumbnail);
        metadata.Finish("metadata");
        await Until(() => Volatile.Read(ref checks) > 0);
        Assert.Equal(1, loader.Count);
        var bytes = new byte[] { 9 };
        source.ThumbnailData = bytes;
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
        Assert.Equal(1, loader.Count);
    }

    [Fact]
    public async Task LoadingSourceSuppliesBytesWithoutParallelThumbnailRequest()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        var source = new ImageViewItem { FilePath = "a.jpg", IsLoading = true };
        vm.Show("a.jpg", source, 320, 1);
        var metadata = await loader.Next();
        Assert.False(metadata.Request.LoadThumbnail);
        metadata.Finish("tag");
        await Until(() => !vm.IsLoading);
        Assert.True(vm.ThumbnailItem!.IsLoading);
        var bytes = new byte[] { 3 };
        source.ThumbnailData = bytes;
        source.IsLoading = false;
        Assert.Same(bytes, vm.ThumbnailItem.ThumbnailData);
        source.ThumbnailData = null;
        Assert.Same(bytes, vm.ThumbnailItem.ThumbnailData);
        Assert.Equal(1, loader.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingSourceBecomesUnavailable_FallsBackThroughSingleQueue(bool detach)
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        var source = new ImageViewItem { FilePath = "a.jpg", IsLoading = true };
        vm.Show("a.jpg", source, 320, 1);
        var metadata = await loader.Next();
        Assert.False(metadata.Request.LoadThumbnail);
        if (detach) vm.Show("a.jpg", null, 320, 1);
        else source.IsLoading = false;
        Assert.True(metadata.Cancellation.IsCancellationRequested);
        Assert.Equal(1, loader.Count);
        metadata.Finish("stale");
        var fallback = await loader.Next();
        Assert.True(fallback.Request.LoadThumbnail);
        var bytes = new byte[] { 4 };
        fallback.Finish("current", bytes);
        await Until(() => !vm.IsLoading);
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
        vm.IsOpen = false;
        source.ThumbnailData = [5];
        Assert.Null(vm.ThumbnailItem);
        Assert.Equal(2, loader.Count);
    }

    [Fact]
    public async Task RefreshDuringInitialLoadPreservesUnfulfilledThumbnailDemand()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("a.jpg", null, 320, 1);
        var first = await loader.Next();
        vm.Refresh();
        first.Finish("stale", [1]);
        var refreshed = await loader.Next();
        Assert.True(refreshed.Request.LoadThumbnail);
        var bytes = new byte[] { 2 };
        refreshed.Finish("fresh", bytes);
        await Until(() => !vm.IsLoading);
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
    }

    [Fact]
    public async Task FolderWithTheSamePathRejectsLateFileResultAndKeepsItsOwnCover()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("album.jpg", null, 320, 1);
        var fileRead = await loader.Next();

        var cover = new byte[] { 3 };
        vm.ShowFolder("album.jpg", cover);
        Assert.True(vm.IsFolder);
        Assert.False(vm.CanEditTags);
        Assert.True(fileRead.Cancellation.IsCancellationRequested);
        Assert.Same(cover, vm.ThumbnailItem!.ThumbnailData);
        fileRead.Finish("stale", [1]);

        var folderRead = await loader.Next();
        Assert.True(folderRead.Request.IsFolder);
        Assert.False(folderRead.Request.LoadThumbnail);
        folderRead.Completion.SetResult(new([], Folder: new("album.jpg", 1, 2, 1, 1, 1234,
            FolderStatisticsState.Partial, DateTimeOffset.Now, 1)));
        await Until(() => !vm.IsLoading);
        Assert.Equal(2L, vm.Folder?.FileCount);
        Assert.NotNull(vm.ErrorText);
        Assert.Empty(vm.Tags);
        Assert.Same(cover, vm.ThumbnailItem!.ThumbnailData);
    }

    [Fact]
    public async Task FailedFolderCoverRefreshClearsExistingCover()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.ShowFolder("album", [1], "album/a.jpg");
        (await loader.Next()).Finish("");
        await Until(() => !vm.IsLoading);

        vm.Refresh(reloadThumbnail: true);
        var refresh = await loader.Next();
        Assert.True(refresh.Request.LoadThumbnail);
        refresh.Completion.SetResult(new([], Media: new(MediaPreviewState.Failed)));
        await Until(() => !vm.IsLoading);
        Assert.Null(vm.ThumbnailItem!.ThumbnailData);
    }

    [Fact]
    public async Task RetrySavePreservesCoverAndDoesNotRequestAnotherPreview()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("clip.mp4", null, 320, 1);
        var initial = await loader.Next();
        var cover = new byte[] { 2 };
        initial.Completion.SetResult(new([], Media: new(MediaPreviewState.Ready, cover,
            Persistence: PreviewPersistence.MemoryOnly, Origin: ArtworkOrigin.VideoFrame, RetryToken: "retry")));
        await Until(() => vm.CanRetrySave);

        var save = vm.RetrySaveAsync();
        var retry = await loader.Next();
        Assert.Equal("retry", retry.Request.RetryToken);
        Assert.False(retry.Request.LoadThumbnail);
        Assert.Same(cover, vm.ThumbnailItem!.ThumbnailData);
        retry.Completion.SetResult(new([], Media: new(MediaPreviewState.Ready, Persistence: PreviewPersistence.Saved)));
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.CanRetrySave);
        Assert.Same(cover, vm.ThumbnailItem!.ThumbnailData);
    }

    [Fact]
    public async Task ChangingDecodeTierRequestsOnlyCoverAndRetainsMetadata()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("a.jpg", null, 256, 1);
        (await loader.Next()).Finish("tag", [1]);
        await Until(() => !vm.IsLoading);

        vm.SetPreviewDecodeWidth(512);
        var resized = await loader.Next();
        Assert.Equal(512, resized.Request.DecodeWidth);
        Assert.False(resized.Request.LoadMetadata);
        Assert.True(resized.Request.LoadThumbnail);
        Assert.Equal("tag", Assert.Single(vm.Tags));
        resized.Completion.SetResult(new([], Media: new(MediaPreviewState.Ready, [2])));
        await Until(() => vm.ThumbnailItem?.ThumbnailData?.FirstOrDefault() == 2);
        vm.SetPreviewDecodeWidth(512);
        Assert.Equal(2, loader.Count);
        Assert.Equal("tag", Assert.Single(vm.Tags));
    }

    [Fact]
    public async Task SamePathDecodeWidthChangeReplacesMissingThumbnailRequest_ButKeepsLoadedBytes()
    {
        var loader = new ControlledLoader();
        using var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        vm.Show("a.jpg", null, 320, 1);
        var first = await loader.Next();
        vm.Show("a.jpg", null, 640, 1);
        Assert.True(first.Cancellation.IsCancellationRequested);
        first.Finish("stale", [1]);
        var latest = await loader.Next();
        Assert.True(latest.Request.LoadThumbnail);
        Assert.Equal(640, latest.Request.DecodeWidth);
        var bytes = new byte[] { 2 };
        latest.Finish("fresh", bytes);
        await Until(() => !vm.IsLoading);
        vm.Show("a.jpg", null, 800, 1);
        Assert.Same(bytes, vm.ThumbnailItem!.ThumbnailData);
        Assert.Equal(2, loader.Count);
    }

    [Fact]
    public async Task ClearAndDisposeRejectLateResults_AndDetachSource()
    {
        var loader = new ControlledLoader();
        var vm = new DetailPaneViewModel(loader, new ImmediateDispatcher());
        var source = new ImageViewItem { FilePath = "a.jpg" };
        vm.Show("a.jpg", source, 320, 1);
        var read = await loader.Next();
        source.ThumbnailData = [1];
        Assert.Same(source.ThumbnailData, vm.ThumbnailItem!.ThumbnailData);
        vm.Show(null, null, 320, 0);
        Assert.False(vm.HasItem);
        vm.Dispose();
        read.Finish("late", [2]);
        source.ThumbnailData = [3];
        await Task.Delay(30);
        Assert.Null(vm.ThumbnailItem);
        Assert.Empty(vm.Tags);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData("track.mp3", true, false)]
    [InlineData("clip.mp4", false, true)]
    [InlineData("picture.gif", false, true)]
    public void MediaAndCollapsedState(string path, bool music, bool editTags)
    {
        using var vm = new DetailPaneViewModel(new ControlledLoader(), new ImmediateDispatcher());
        vm.IsOpen = false;
        vm.Show(path, null, 320, 1);
        Assert.Equal(music, vm.IsMusic);
        Assert.Equal(editTags, vm.CanEditTags);
        Assert.Null(vm.ThumbnailItem);
        Assert.False(vm.IsLoading);
        Assert.Equal("未知", vm.DimensionsText);
    }

    [Theory]
    [InlineData(double.NaN, 312)]
    [InlineData(double.PositiveInfinity, 312)]
    [InlineData(-1, 248)]
    [InlineData(999, 520)]
    [InlineData(360, 360)]
    public void WidthIsFiniteAndClamped(double input, double expected)
    {
        using var vm = new DetailPaneViewModel(new ControlledLoader(), new ImmediateDispatcher());
        vm.PreferredWidth = input;
        Assert.Equal(expected, vm.PreferredWidth);
    }
}
