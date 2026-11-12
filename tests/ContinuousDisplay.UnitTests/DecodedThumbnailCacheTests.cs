using System.Collections.Concurrent;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class DecodedThumbnailCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PrefetchLeavesAWorkerFreeAndForegroundPromotesAnExistingJob()
    {
        using var unblock = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<byte>();
        var cache = new DecodedThumbnailCache<FakeBitmap>(bytes =>
        {
            seen.Enqueue(bytes[0]);
            if (bytes[0] == 1)
            {
                started.SetResult();
                Assert.True(unblock.Wait(Timeout));
            }
            return new FakeBitmap();
        }, _ => 16, workerCount: 2, queueCapacity: 2, idleBudget: 256);

        byte[] promotedKey = [3];
        var warming = cache.AcquireAsync([1], prefetch: true).AsTask();
        var lowerPriority = cache.AcquireAsync([2], prefetch: true).AsTask();
        var promoted = cache.AcquireAsync(promotedKey, prefetch: true).AsTask();
        try
        {
            await started.Task.WaitAsync(Timeout);
            Assert.False(lowerPriority.IsCompleted);
            Assert.False(promoted.IsCompleted);
            using var visible = await cache.AcquireAsync(promotedKey).AsTask().WaitAsync(Timeout);
            using var shared = await promoted.WaitAsync(Timeout);
            Assert.NotNull(visible);
            Assert.NotNull(shared);
            Assert.Same(visible.Value, shared.Value);
            Assert.Equal(new byte[] { 1, 3 }, seen.ToArray());
        }
        finally
        {
            unblock.Set();
        }
        using var first = await warming.WaitAsync(Timeout);
        using var second = await lowerPriority.WaitAsync(Timeout);
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ForegroundDisplacesQueuedPrefetchWhenCapacityIsFull()
    {
        using var unblock = new ManualResetEventSlim();
        var seen = new ConcurrentQueue<byte>();
        var cache = new DecodedThumbnailCache<FakeBitmap>(bytes =>
        {
            seen.Enqueue(bytes[0]);
            if (bytes[0] is 1 or 2)
                Assert.True(unblock.Wait(Timeout));
            return new FakeBitmap();
        }, _ => 16, workerCount: 2, queueCapacity: 1, idleBudget: 256);

        var warming = cache.AcquireAsync([1], prefetch: true).AsTask();
        var visible = cache.AcquireAsync([2]).AsTask();
        var displaced = cache.AcquireAsync([3], prefetch: true).AsTask();
        var nextVisible = cache.AcquireAsync([4]).AsTask();
        try
        {
            Assert.Null(await displaced.WaitAsync(Timeout));
        }
        finally
        {
            unblock.Set();
        }
        using var first = await warming.WaitAsync(Timeout);
        using var second = await visible.WaitAsync(Timeout);
        using var next = await nextVisible.WaitAsync(Timeout);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(next);
        Assert.Equal(new byte[] { 1, 2, 4 }, seen.Order().ToArray());
    }

    [Fact]
    public async Task CancelledPromotionRestoresThePrefetchConcurrencyLimit()
    {
        using var unblockPrefetch = new ManualResetEventSlim();
        using var unblockVisible = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var cache = new DecodedThumbnailCache<FakeBitmap>(bytes =>
        {
            if (bytes[0] == 1)
                Assert.True(unblockPrefetch.Wait(Timeout));
            if (bytes[0] == 2)
                Assert.True(unblockVisible.Wait(Timeout));
            return new FakeBitmap();
        }, _ => 16, workerCount: 2, queueCapacity: 2, idleBudget: 256);

        byte[] promotedKey = [3];
        var warming = cache.AcquireAsync([1], prefetch: true).AsTask();
        var visible = cache.AcquireAsync([2]).AsTask();
        var queued = cache.AcquireAsync(promotedKey, prefetch: true).AsTask();
        var promoted = cache.AcquireAsync(promotedKey, cancellation.Token).AsTask();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => promoted.WaitAsync(Timeout));
            unblockVisible.Set();
            using var firstVisible = await visible.WaitAsync(Timeout);
            using var secondVisible = await cache.AcquireAsync([4]).AsTask().WaitAsync(Timeout);
            Assert.NotNull(firstVisible);
            Assert.NotNull(secondVisible);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            unblockVisible.Set();
            unblockPrefetch.Set();
        }
        using var firstWarm = await warming.WaitAsync(Timeout);
        using var secondWarm = await queued.WaitAsync(Timeout);
        Assert.NotNull(firstWarm);
        Assert.NotNull(secondWarm);
    }

    [Fact]
    public void LookaheadTracksOnlyBoundedCandidatesAndCancelsOnlyChangedWork()
    {
        var calls = new List<(byte[] Bytes, CancellationToken Token)>();
        using var parent = new CancellationTokenSource();
        using var prefetcher = new ThumbnailBitmapPrefetcher((bytes, token) =>
        {
            calls.Add((bytes, token));
            return new ValueTask(Task.Delay(System.Threading.Timeout.Infinite, token));
        });
        var candidates = Enumerable.Range(0, ThumbnailBitmapPrefetcher.CandidateLimit + 2)
            .Select(index => new ImageViewItem { ThumbnailData = [(byte)index] }).ToArray();
        prefetcher.UpdateCandidates(candidates, parent.Token);
        Assert.Equal(ThumbnailBitmapPrefetcher.CandidateLimit, calls.Count);
        Assert.Equal(Enumerable.Range(0, calls.Count).Select(index => (byte)index),
            calls.Select(call => call.Bytes[0]));

        prefetcher.UpdateCandidates(candidates.Skip(1).ToArray(), parent.Token);
        Assert.True(calls[0].Token.IsCancellationRequested);
        Assert.False(calls[1].Token.IsCancellationRequested);
        Assert.Equal(ThumbnailBitmapPrefetcher.CandidateLimit + 1, calls.Count);

        candidates[1].ThumbnailData = [200];
        Assert.True(calls[1].Token.IsCancellationRequested);
        Assert.Equal((byte)200, calls[^1].Bytes[0]);
        Assert.False(calls[^1].Token.IsCancellationRequested);
        var count = calls.Count;
        candidates[0].ThumbnailData = [201];
        candidates[^1].ThumbnailData = [202];
        Assert.Equal(count, calls.Count);

        parent.Cancel();
        Assert.All(calls, call => Assert.True(call.Token.IsCancellationRequested));
        candidates[2].ThumbnailData = [203];
        Assert.Equal(count, calls.Count);
        prefetcher.Clear();
        prefetcher.UpdateCandidates([candidates[0]], CancellationToken.None);
        Assert.Equal(count + 1, calls.Count);
        prefetcher.Dispose();
        Assert.True(calls[^1].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadyLookupReusesOnlyCompletedEntriesAndProtectsTheLeaseFromTrim()
    {
        using var unblock = new ManualResetEventSlim();
        var decoded = new FakeBitmap();
        var decodes = 0;
        var cache = new DecodedThumbnailCache<FakeBitmap>(_ =>
        {
            Interlocked.Increment(ref decodes);
            Assert.True(unblock.Wait(Timeout));
            return decoded;
        }, _ => 16, workerCount: 1, queueCapacity: 1, idleBudget: 256);
        byte[] key = [1];

        Assert.Null(cache.TryAcquireReady(key));
        Assert.Equal(0, decodes);
        var pending = cache.AcquireAsync(key).AsTask();
        try
        {
            Assert.Null(cache.TryAcquireReady(key));
        }
        finally
        {
            unblock.Set();
        }

        using var loaded = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await pending.WaitAsync(Timeout));
        loaded.Dispose();
        Assert.Null(cache.TryAcquireReady([1]));
        using var hit = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(cache.TryAcquireReady(key));
        Assert.Same(decoded, hit.Value);
        cache.TrimIdle(0);
        Assert.Equal(0, decoded.DisposeCalls);
        hit.Dispose();
        cache.TrimIdle(0);
        Assert.Equal(1, decoded.DisposeCalls);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public async Task SharedDecodeRunsOffCallerThreadAndSurvivesOneCancelledConsumer()
    {
        using var unblock = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var decodes = 0;
        var decoderThread = 0;
        var cache = new DecodedThumbnailCache<FakeBitmap>(_ =>
        {
            Interlocked.Increment(ref decodes);
            decoderThread = Environment.CurrentManagedThreadId;
            started.Set();
            Assert.True(unblock.Wait(Timeout));
            return new FakeBitmap();
        }, _ => 16, workerCount: 2, queueCapacity: 2, idleBudget: 0);
        byte[] key = [1];
        var callerThread = Environment.CurrentManagedThreadId;
        var cancelled = cache.AcquireAsync(key, cancellation.Token).AsTask();
        var remaining = cache.AcquireAsync(key).AsTask();
        try
        {
            Assert.True(started.Wait(Timeout));
            Assert.NotEqual(callerThread, decoderThread);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));
            Assert.False(remaining.IsCompleted);
        }
        finally
        {
            unblock.Set();
        }

        using var first = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await remaining.WaitAsync(Timeout));
        using var second = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(key));
        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, decodes);
        first.Dispose();
        Assert.Equal(0, second.Value.DisposeCalls);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, second.Value.DisposeCalls);
    }

    [Fact]
    public async Task BoundedQueueReleasesCancelledJobsAndWaitingRequests()
    {
        using var unblock = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<byte>();
        var cache = new DecodedThumbnailCache<FakeBitmap>(bytes =>
        {
            seen.Enqueue(bytes[0]);
            if (bytes[0] == 1)
            {
                started.SetResult();
                Assert.True(unblock.Wait(Timeout));
            }
            return new FakeBitmap();
        }, _ => 16, workerCount: 1, queueCapacity: 1, idleBudget: 0);
        using var queuedCancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();

        var running = cache.AcquireAsync([1]).AsTask();
        var queued = cache.AcquireAsync([2], queuedCancellation.Token).AsTask();
        var waiting = cache.AcquireAsync([3], waitingCancellation.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(Timeout);
            Assert.False(queued.IsCompleted);
            Assert.False(waiting.IsCompleted);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        }
        finally
        {
            unblock.Set();
        }

        using var first = await running.WaitAsync(Timeout);
        using var last = await cache.AcquireAsync([4]).AsTask().WaitAsync(Timeout);
        Assert.NotNull(first);
        Assert.NotNull(last);
        Assert.Equal(new byte[] { 1, 4 }, seen.ToArray());
    }

    [Fact]
    public async Task CancelledInFlightResultIsDisposedAndDoesNotFillTheIdleCache()
    {
        using var unblock = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new FakeBitmap();
        var decodes = 0;
        var cache = new DecodedThumbnailCache<FakeBitmap>(_ =>
        {
            if (Interlocked.Increment(ref decodes) != 1)
                return new FakeBitmap();
            started.SetResult();
            Assert.True(unblock.Wait(Timeout));
            return stale;
        }, _ => 16, workerCount: 1, queueCapacity: 2, idleBudget: 256);
        byte[] key = [7];
        var cancelled = cache.AcquireAsync(key, cancellation.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));
        }
        finally
        {
            unblock.Set();
        }

        await stale.Disposed.Task.WaitAsync(Timeout);
        using var current = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(
            await cache.AcquireAsync(key).AsTask().WaitAsync(Timeout));
        Assert.NotSame(stale, current.Value);
        Assert.Equal(2, decodes);
    }

    [Fact]
    public async Task IdleBudgetIncludesEncodedBytesAndEvictsLeastRecentlyUsedLeaseOnly()
    {
        var cache = new DecodedThumbnailCache<FakeBitmap>(_ => new FakeBitmap(), _ => 16,
            workerCount: 1, queueCapacity: 2, idleBudget: 35);
        byte[] a = [7, 7], b = [7, 7], c = [8, 8];
        using var active = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(a));
        using var second = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(b));
        using var third = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(c));
        Assert.NotSame(active.Value, second.Value);
        second.Dispose();
        third.Dispose();
        // One 18-byte entry fits, but two don't. The visible lease stays alive.
        Assert.Equal(1, second.Value.DisposeCalls);
        Assert.Equal(0, third.Value.DisposeCalls);
        Assert.Equal(0, active.Value.DisposeCalls);
        using var retained = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(c));
        Assert.Same(third.Value, retained.Value);
        using var replacement = Assert.IsType<DecodedThumbnailCache<FakeBitmap>.Lease>(await cache.AcquireAsync(b));
        Assert.NotSame(second.Value, replacement.Value);
        retained.Dispose();
        cache.TrimIdle(0);
        Assert.Equal(1, third.Value.DisposeCalls);
        Assert.Equal(0, active.Value.DisposeCalls);
        Assert.Equal(0, replacement.Value.DisposeCalls);
    }

    [Fact]
    public async Task DecodeFailureDoesNotPoisonTheKey()
    {
        var decodes = 0;
        var cache = new DecodedThumbnailCache<FakeBitmap>(_ =>
        {
            if (Interlocked.Increment(ref decodes) == 1)
                throw new ArgumentException("Invalid image");
            return new FakeBitmap();
        }, _ => 16, workerCount: 1, queueCapacity: 2, idleBudget: 0);
        byte[] key = [1];
        Assert.Null(await cache.AcquireAsync(key).AsTask().WaitAsync(Timeout));
        using var recovered = await cache.AcquireAsync(key).AsTask().WaitAsync(Timeout);
        Assert.NotNull(recovered);
    }

    private sealed class FakeBitmap : IDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            Disposed.TrySetResult();
        }
    }
}
