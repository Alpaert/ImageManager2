using System.Collections.Concurrent;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class PageManagerViewportTests
{
    [Fact]
    public async Task PendingUiPublicationDoesNotOccupyThumbnailIoSlots()
    {
        using var fixture = new Fixture();
        var items = Enumerable.Range(0, 7).Select(i => fixture.Item($"slot-{i}.png")).ToArray();
        fixture.Page.UpdatePagedViewport(items, items, items);
        // Hold all UI handoffs. The seventh disk hit must still finish reading.
        await fixture.Dispatcher.WaitForCountAsync(7);
        fixture.Dispatcher.Drain();
        Assert.All(items, item => Assert.NotNull(item.ThumbnailData));
    }

    [Fact]
    public async Task LookaheadLoadsOutsideRealizedRangeAndOverlappingMovementKeepsItsBytes()
    {
        using var fixture = new Fixture();
        var visible = fixture.Item("visible.png");
        var ahead = fixture.Item("ahead.png");
        var far = fixture.Item("far.png");
        fixture.Page.UpdatePagedViewport([visible, ahead, far], [visible], [visible], prefetchItems: [ahead]);
        await fixture.Dispatcher.WaitForCountAsync(2);
        fixture.Dispatcher.Drain();
        var aheadBytes = ahead.ThumbnailData;
        Assert.NotNull(aheadBytes);
        Assert.Null(far.ThumbnailData);
        fixture.Page.UpdatePagedViewport([visible, ahead, far], [ahead], [visible, ahead]);
        Assert.Same(aheadBytes, ahead.ThumbnailData);
        fixture.Page.UpdatePagedViewport([visible, ahead, far], [far], [far]);
        Assert.Null(ahead.ThumbnailData);
    }

    [Fact]
    public async Task ContinuousOverlapKeepsPendingWorkWhileLeavingTheWindowRejectsPublication()
    {
        using var fixture = new Fixture();
        using var lifetime = new CancellationTokenSource();
        var first = fixture.Item("first-visible.png");
        var ahead = fixture.Item("next.png");
        fixture.Page.UpdateContinuousThumbnailWindow([first], [first], [ahead], lifetime.Token);
        await fixture.Dispatcher.WaitForCountAsync(2);
        fixture.Page.UpdateContinuousThumbnailWindow([first], [first], [ahead], lifetime.Token);
        fixture.Dispatcher.Drain();
        Assert.NotNull(first.ThumbnailData);
        Assert.NotNull(ahead.ThumbnailData);
        var pending = fixture.Item("canceled.png");
        fixture.Page.UpdateContinuousThumbnailWindow([pending], [pending], [], lifetime.Token);
        await fixture.Dispatcher.WaitForCountAsync(1);
        fixture.Page.ClearContinuousViewport();
        fixture.Dispatcher.Drain();
        Assert.Null(pending.ThumbnailData);
    }
    [Fact]
    public async Task PressureTrimDropsOptionalLookaheadWithoutClearingVisibleImages()
    {
        using var fixture = new Fixture();
        var visible = fixture.Item("pressure-visible.png");
        var ahead = fixture.Item("pressure-ahead.png");
        fixture.Page.UpdatePagedViewport([visible, ahead], [visible], [visible], prefetchItems: [ahead]);
        await fixture.Dispatcher.WaitForCountAsync(2);
        fixture.Dispatcher.Drain();
        var bytes = visible.ThumbnailData;
        Assert.NotNull(bytes);
        Assert.NotNull(ahead.ThumbnailData);
        fixture.Page.TrimForMemoryPressure();
        Assert.Same(bytes, visible.ThumbnailData);
        Assert.Null(ahead.ThumbnailData);
    }

    [Fact]
    public async Task LeavingRetainedRangeRejectsAlreadyQueuedPublication()
    {
        using var fixture = new Fixture();
        var item = fixture.Item("pending.png");
        fixture.Page.UpdatePagedViewport([item], [item], [item]);
        await fixture.Dispatcher.WaitForWorkAsync();
        fixture.Page.UpdatePagedViewport([item], [], []);
        fixture.Dispatcher.Drain();
        Assert.Null(item.ThumbnailData);
        Assert.False(item.IsLoaded);
    }

    [Fact]
    public async Task DeletionRefillDoesNotReloadOffscreenPageItems()
    {
        using var fixture = new Fixture();
        var visible = fixture.Item("visible.png");
        visible.IsLoaded = true;
        visible.ThumbnailData = [1, 2, 3];
        var outside = fixture.Item("outside.png");
        fixture.Page.UpdatePagedViewport([visible, outside], [visible], [visible]);
        await fixture.Page.LoadThumbnailsForItemsAsync([visible, outside]).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(outside.ThumbnailData);
        Assert.False(outside.IsLoaded);
        Assert.Equal(0, fixture.Dispatcher.PendingCount);
    }

    [Fact]
    public async Task OlderPageTrimCannotEvictOrResetNewCurrentPage()
    {
        using var fixture = new Fixture();
        fixture.Page.PageSize = 800;
        var first = fixture.Item("first.png");
        var second = fixture.Item("second.png");
        first.IsLoaded = second.IsLoaded = true;
        var bytes = second.ThumbnailData = [1, 2, 3];
        fixture.Page.SetPageCache(0, [first]);
        fixture.Page.SetPageCache(1, [second]);
        await fixture.Page.ShowPageAsync(0, 2, [], _ => [], true, null);
        await fixture.Page.ShowPageAsync(1, 2, [], _ => [], true, null);
        fixture.Dispatcher.Drain();
        Assert.Same(bytes, second.ThumbnailData);
        Assert.True(second.IsLoaded);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ImageManager-ViewportTests", Guid.NewGuid().ToString("N"));
        public QueuedDispatcher Dispatcher { get; } = new();
        public PageManager Page { get; }

        public Fixture()
        {
            Page = new PageManager(new ThumbnailCacheService(null!, _directory), null!, dispatcher: Dispatcher)
                { UseViewportLoading = true };
        }

        public ImageViewItem Item(string name)
        {
            var path = Path.Combine(_directory, name);
            // The cache path does not require any user file or image decoding service.
            var cache = new DiskThumbnailCache(_directory, 200);
            cache.Save(path, [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 10, 0, 20, 1, 1, 0, 0, 0xff, 0xd9]);
            cache.SaveMeta(path, 20, 10);
            return new ImageViewItem { FilePath = path, FileName = name };
        }

        public void Dispose()
        {
            Page.Dispose();
            Dispatcher.Drain();
            // Temporary test cache can still have canceled workers unwinding.
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private sealed class QueuedDispatcher : IDispatcher
    {
        private readonly ConcurrentQueue<Action> _work = new();
        private readonly SemaphoreSlim _available = new(0);
        public int PendingCount => _work.Count;
        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (PendingCount < count && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(PendingCount >= count, $"Expected {count} UI publications, observed {PendingCount}.");
        }
        public Task WaitForWorkAsync() => _available.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t =>
        {
            if (!t.Result) throw new TimeoutException("Thumbnail did not reach UI publication.");
        });
        public void Drain() { while (_work.TryDequeue(out var action)) action(); }
        public void Post(Action action) { _work.Enqueue(action); _available.Release(); }
        public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });
        public Task<T> InvokeAsync<T>(Func<T> callback)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => { try { done.SetResult(callback()); } catch (Exception ex) { done.SetException(ex); } });
            return done.Task;
        }
        public Task InvokeAsync(Func<Task> callback) => InvokeAsync<Task>(callback).Unwrap();
        public Task<T> InvokeAsync<T>(Func<Task<T>> callback) => InvokeAsync<Task<T>>(callback).Unwrap();
    }
}
