using System.Diagnostics;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Caching;

namespace ImageManager.App.Services;

internal enum ThumbnailLoadSource
{
    Page,
    Preload,
    Viewport
}

internal sealed class ThumbnailBatchMetrics
{
    private readonly ThumbnailLoadSource _source;
    private readonly int _pageIndex;
    private readonly int _decodeWidth;
    private readonly Func<string> _activeLoads;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TaskCompletionSource _requestsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _requestGate = new();
    private readonly object _loadedItemsLock = new();
    private readonly List<ImageViewItem> _loadedItems = new();
    private int _requested;
    private int _completed;
    private int _failed;
    private int _canceled;
    private int _memoryHits;
    private int _diskHits;
    private int _cacheOnlyHits;
    private int _generated;
    private int _inFlight;
    private bool _requestsSealed;
    private long _inFlightMax;
    private int _pendingUiPosts;
    private long _queueMaxMs;
    private long _completeMaxMs;
    private long _uiPostDelayMaxMs;
    private string _outcome = "Success";
    private long _elapsedMs;
    private int _logRequested;
    private int _logged;

    public ThumbnailBatchMetrics(ThumbnailLoadSource source, int pageIndex, int decodeWidth, Func<string> activeLoads)
    {
        _source = source;
        _pageIndex = pageIndex;
        _decodeWidth = decodeWidth;
        _activeLoads = activeLoads;
    }

    public void RecordRequested()
    {
        Interlocked.Increment(ref _requested);
        var inFlight = Interlocked.Increment(ref _inFlight);
        Max(ref _inFlightMax, inFlight);
    }

    public void RecordResult(ThumbnailCacheLoadResult result, double elapsedMs)
    {
        Interlocked.Increment(ref _completed);
        Max(ref _completeMaxMs, (long)Math.Ceiling(elapsedMs));
        switch (result.Source)
        {
            case ThumbnailCacheSource.Memory:
                Interlocked.Increment(ref _memoryHits);
                break;
            case ThumbnailCacheSource.Disk:
                Interlocked.Increment(ref _diskHits);
                break;
            case ThumbnailCacheSource.Generated:
                Interlocked.Increment(ref _generated);
                break;
            case ThumbnailCacheSource.Missing:
                Interlocked.Increment(ref _failed);
                break;
        }
        CompleteRequest();
    }

    public void RecordMemoryHit(double elapsedMs)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Increment(ref _memoryHits);
        Max(ref _completeMaxMs, (long)Math.Ceiling(elapsedMs));
        CompleteRequest();
    }

    public void RecordDiskHit(double elapsedMs)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Increment(ref _diskHits);
        Max(ref _completeMaxMs, (long)Math.Ceiling(elapsedMs));
        CompleteRequest();
    }

    public void RecordGenerated(double elapsedMs)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Increment(ref _generated);
        Max(ref _completeMaxMs, (long)Math.Ceiling(elapsedMs));
        CompleteRequest();
    }

    public void RecordCacheOnlyHit(double elapsedMs)
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Increment(ref _cacheOnlyHits);
        Max(ref _completeMaxMs, (long)Math.Ceiling(elapsedMs));
        CompleteRequest();
    }

    public void RecordFailed()
    {
        Interlocked.Increment(ref _failed);
        CompleteRequest();
    }

    public void RecordCanceled()
    {
        Interlocked.Increment(ref _canceled);
        CompleteRequest();
    }

    public void RecordQueueDelay(double elapsedMs) => Max(ref _queueMaxMs, (long)Math.Ceiling(elapsedMs));
    public void RegisterUiPost() => Interlocked.Increment(ref _pendingUiPosts);

    public void CompleteUiPost(double elapsedMs)
    {
        Max(ref _uiPostDelayMaxMs, (long)Math.Ceiling(elapsedMs));
        Interlocked.Decrement(ref _pendingUiPosts);
        TryLog();
    }

    public void RecordLoadedItem(ImageViewItem item)
    {
        lock (_loadedItemsLock)
            _loadedItems.Add(item);
    }

    public List<ImageViewItem> TakeLoadedItems()
    {
        lock (_loadedItemsLock)
        {
            var result = new List<ImageViewItem>(_loadedItems);
            _loadedItems.Clear();
            return result;
        }
    }

    public Task WaitForRequestsAsync()
    {
        return _requestsCompleted.Task;
    }

    public void SealRequests()
    {
        lock (_requestGate)
        {
            _requestsSealed = true;
            if (Volatile.Read(ref _inFlight) == 0)
                _requestsCompleted.TrySetResult();
        }
    }

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    public void RequestLog(string outcome, long elapsedMs)
    {
        _outcome = outcome;
        _elapsedMs = elapsedMs;
        Interlocked.Exchange(ref _logRequested, 1);
        TryLog();
    }

    private void TryLog()
    {
        if (Volatile.Read(ref _logRequested) == 0 ||
            Volatile.Read(ref _inFlight) != 0 ||
            Volatile.Read(ref _pendingUiPosts) != 0 ||
            Interlocked.Exchange(ref _logged, 1) != 0)
            return;

        var prefix = _source switch
        {
            ThumbnailLoadSource.Preload => "Thumb.Preload.Batch",
            ThumbnailLoadSource.Viewport => "ThumbViewport.Batch",
            _ => "Thumb.Batch"
        };
        ScrollDiagnosticsLogger.Log(
            $"{prefix}.End source={_source} page={_pageIndex} width={_decodeWidth} outcome={_outcome} " +
            $"requested={Volatile.Read(ref _requested)} completed={Volatile.Read(ref _completed)} " +
            $"failed={Volatile.Read(ref _failed)} canceled={Volatile.Read(ref _canceled)} " +
            $"memHit={Volatile.Read(ref _memoryHits)} diskHit={Volatile.Read(ref _diskHits)} cacheOnlyHit={Volatile.Read(ref _cacheOnlyHits)} generated={Volatile.Read(ref _generated)} " +
            $"inFlightMax={Volatile.Read(ref _inFlightMax)} " +
            $"queueMaxMs={Volatile.Read(ref _queueMaxMs)} completeMaxMs={Volatile.Read(ref _completeMaxMs)} " +
            $"uiPostDelayMaxMs={Volatile.Read(ref _uiPostDelayMaxMs)} active={_activeLoads()} elapsedMs={_elapsedMs}");
    }

    private void CompleteRequest()
    {
        lock (_requestGate)
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && _requestsSealed)
                _requestsCompleted.TrySetResult();
        }
        TryLog();
    }

    private static void Max(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

internal sealed class ThumbnailScheduler : IDisposable
{
    public const int MaxConcurrentThumbnailLoads = 6;
    public const int MaxNonViewportThumbnailLoads = MaxConcurrentThumbnailLoads - 1;

    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(MaxConcurrentThumbnailLoads);
    private readonly SemaphoreSlim _nonViewportThumbnailLoadSemaphore = new(MaxNonViewportThumbnailLoads);
    private readonly SemaphoreSlim _videoLoadSemaphore = new(2);

    private int _activePageThumbnailLoads;
    private int _activePreloadThumbnailLoads;
    private int _activeViewportThumbnailLoads;

    public static bool IsNonViewportThumbnailLoad(ThumbnailLoadSource source) =>
        source is ThumbnailLoadSource.Page or ThumbnailLoadSource.Preload;

    public void EnterThumbnailLoad(ThumbnailLoadSource source)
    {
        switch (source)
        {
            case ThumbnailLoadSource.Page:
                Interlocked.Increment(ref _activePageThumbnailLoads);
                break;
            case ThumbnailLoadSource.Preload:
                Interlocked.Increment(ref _activePreloadThumbnailLoads);
                break;
            case ThumbnailLoadSource.Viewport:
                Interlocked.Increment(ref _activeViewportThumbnailLoads);
                break;
        }
    }

    public void ExitThumbnailLoad(ThumbnailLoadSource source)
    {
        switch (source)
        {
            case ThumbnailLoadSource.Page:
                Interlocked.Decrement(ref _activePageThumbnailLoads);
                break;
            case ThumbnailLoadSource.Preload:
                Interlocked.Decrement(ref _activePreloadThumbnailLoads);
                break;
            case ThumbnailLoadSource.Viewport:
                Interlocked.Decrement(ref _activeViewportThumbnailLoads);
                break;
        }
    }

    public string GetActiveLoadSummary() =>
        $"page={Volatile.Read(ref _activePageThumbnailLoads)} viewport={Volatile.Read(ref _activeViewportThumbnailLoads)} preload={Volatile.Read(ref _activePreloadThumbnailLoads)}";

    public async Task<SlotReleaser> AcquireSlotAsync(
        ThumbnailLoadSource source,
        bool isVideo,
        ThumbnailBatchMetrics? metrics,
        CancellationToken ct)
    {
        var semaphore = isVideo ? _videoLoadSemaphore : _thumbnailLoadSemaphore;
        var nonViewportSemaphore = !isVideo && IsNonViewportThumbnailLoad(source)
            ? _nonViewportThumbnailLoadSemaphore
            : null;

        var queuedAt = Stopwatch.GetTimestamp();
        EnterThumbnailLoad(source);
        bool acquiredSemaphore = false;
        bool acquiredNonViewportSemaphore = false;
        try
        {
            if (nonViewportSemaphore != null)
            {
                await nonViewportSemaphore.WaitAsync(ct).ConfigureAwait(false);
                acquiredNonViewportSemaphore = true;
            }
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquiredSemaphore = true;
            metrics?.RecordQueueDelay(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
            return new SlotReleaser(this, source, semaphore, nonViewportSemaphore);
        }
        catch (OperationCanceledException)
        {
            if (acquiredSemaphore)
                semaphore.Release();
            if (acquiredNonViewportSemaphore)
                nonViewportSemaphore!.Release();
            metrics?.RecordCanceled();
            ExitThumbnailLoad(source);
            throw;
        }
    }

    public void Dispose()
    {
        _thumbnailLoadSemaphore.Dispose();
        _nonViewportThumbnailLoadSemaphore.Dispose();
        _videoLoadSemaphore.Dispose();
    }

    public readonly struct SlotReleaser : IDisposable
    {
        private readonly ThumbnailScheduler _scheduler;
        private readonly ThumbnailLoadSource _source;
        private readonly SemaphoreSlim _semaphore;
        private readonly SemaphoreSlim? _nonViewportSemaphore;

        public SlotReleaser(
            ThumbnailScheduler scheduler,
            ThumbnailLoadSource source,
            SemaphoreSlim semaphore,
            SemaphoreSlim? nonViewportSemaphore)
        {
            _scheduler = scheduler;
            _source = source;
            _semaphore = semaphore;
            _nonViewportSemaphore = nonViewportSemaphore;
        }

        public void Dispose()
        {
            _semaphore.Release();
            _nonViewportSemaphore?.Release();
            _scheduler.ExitThumbnailLoad(_source);
        }
    }
}
