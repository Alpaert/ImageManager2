namespace ImageManager.App.Services;

/// <summary>
/// Shares decoded thumbnails by encoded array identity. Waiting consumers and displayed
/// thumbnails reserve an entry; only idle entries are eligible for LRU eviction.
/// </summary>
internal sealed class DecodedThumbnailCache<T> where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<byte[], Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Entry> _queue = new();
    private readonly LinkedList<Entry> _prefetchQueue = new();
    private readonly LinkedList<Entry> _idle = new();
    private readonly Func<byte[], T> _decode;
    private readonly Func<T, long> _decodedSize;
    private readonly int _workerCount;
    private readonly int _queueCapacity;
    private readonly long _idleBudget;
    private TaskCompletionSource? _spaceAvailable;
    private long _idleBytes;
    private int _running;
    private int _runningPrefetch;

    public DecodedThumbnailCache(
        Func<byte[], T> decode, Func<T, long> decodedSize, int workerCount, int queueCapacity, long idleBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(idleBudget);
        _decode = decode;
        _decodedSize = decodedSize;
        _workerCount = workerCount;
        _queueCapacity = queueCapacity;
        _idleBudget = idleBudget;
    }

    /// <summary>Acquires an already decoded value without queuing work or waiting.</summary>
    public Lease? TryAcquireReady(byte[] bytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(bytes, out var entry) || entry.Value is not { } value)
                return null;

            Reserve(entry, prefetch: false);
            return new Lease(value, () => Release(entry, prefetch: false));
        }
    }

    public async ValueTask<Lease?> AcquireAsync(
        byte[] bytes, CancellationToken cancellationToken = default, bool prefetch = false)
    {
        Entry entry;
        while (true)
        {
            Task available;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_entries.TryGetValue(bytes, out entry!))
                {
                    Reserve(entry, prefetch);
                    if (!prefetch && entry.QueueNode?.List == _prefetchQueue)
                    {
                        _prefetchQueue.Remove(entry.QueueNode);
                        entry.QueueNode = _queue.AddLast(entry);
                        StartWorkers();
                    }
                    break;
                }

                var queuedCount = _queue.Count + _prefetchQueue.Count;
                if (prefetch && (_workerCount < 2 || queuedCount >= _queueCapacity))
                    return null;
                if (queuedCount == _queueCapacity && _prefetchQueue.Last is { } last)
                {
                    // A visible request can always displace queued speculation.
                    var displaced = last.Value;
                    RemoveQueuedEntry(displaced);
                    _entries.Remove(displaced.Bytes);
                    displaced.Completed.SetResult();
                    queuedCount--;
                }
                if (queuedCount < _queueCapacity)
                {
                    entry = new Entry(bytes) { Users = 1, ForegroundUsers = prefetch ? 0 : 1 };
                    _entries.Add(bytes, entry);
                    entry.QueueNode = (prefetch ? _prefetchQueue : _queue).AddLast(entry);
                    StartWorkers();
                    break;
                }

                // Saturated consumers wait for a slot without storing another key or job
                // in the cache. Recycling the control cancels this wait.
                available = (_spaceAvailable ??= NewSignal()).Task;
            }
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var transferred = false;
        try
        {
            await entry.Completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value is not { } value)
                return null;
            var lease = new Lease(value, () => Release(entry, prefetch));
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
                Release(entry, prefetch);
        }
    }

    public void TrimIdle(long remainingBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBudget);
        lock (_gate)
            EvictIdleUntil(remainingBudget);
    }

    private void Reserve(Entry entry, bool prefetch)
    {
        entry.Users++;
        if (!prefetch)
            entry.ForegroundUsers++;
        if (entry.IdleNode is not null)
            RemoveIdleEntry(entry);
    }

    // Called only under _gate. Never decode on the caller's thread.
    private void StartWorkers()
    {
        while (_running < _workerCount)
        {
            var node = _queue.First;
            if (node is null)
            {
                if (_runningPrefetch != 0 || _workerCount < 2)
                    return;
                node = _prefetchQueue.First;
                if (node is null)
                    return;
            }
            var prefetch = node.List == _prefetchQueue;
            if (prefetch)
                _runningPrefetch++;
            var entry = node.Value;
            RemoveQueuedEntry(entry);
            _running++;
            _ = Task.Run(() => Decode(entry, prefetch));
        }
    }

    private void Decode(Entry entry, bool prefetch)
    {
        T? decoded = null;
        long cost = 0;
        try
        {
            decoded = _decode(entry.Bytes);
            cost = checked(entry.Bytes.LongLength + Math.Max(0, _decodedSize(decoded)));
        }
        catch (Exception ex)
        {
            decoded?.Dispose();
            decoded = null;
            System.Diagnostics.Debug.WriteLine($"Thumbnail decode failed: {ex.Message}");
        }

        lock (_gate)
        {
            _running--;
            if (prefetch)
                _runningPrefetch--;
            if (decoded is null || entry.Users == 0)
            {
                _entries.Remove(entry.Bytes);
            }
            else
            {
                entry.Value = decoded;
                entry.Cost = cost;
                decoded = null;
            }
            entry.Completed.SetResult();
            StartWorkers();
        }
        // A decode already inside the native codec cannot be interrupted, but its
        // result is discarded if every consumer has gone away.
        decoded?.Dispose();
    }

    private void Release(Entry entry, bool prefetch)
    {
        lock (_gate)
        {
            if (!prefetch)
                entry.ForegroundUsers--;
            if (--entry.Users != 0)
            {
                // The last visible requester can disappear before a promoted
                // job starts. Keep it in the lookahead concurrency budget.
                if (entry.ForegroundUsers == 0 && entry.QueueNode?.List == _queue)
                {
                    _queue.Remove(entry.QueueNode);
                    entry.QueueNode = _prefetchQueue.AddFirst(entry);
                }
                return;
            }

            if (entry.QueueNode is not null)
            {
                RemoveQueuedEntry(entry);
                _entries.Remove(entry.Bytes);
                return;
            }

            if (entry.Value is null)
                return;

            if (entry.Cost > _idleBudget)
            {
                DisposeEntry(entry);
                return;
            }

            EvictIdleUntil(_idleBudget - entry.Cost);
            entry.IdleNode = _idle.AddLast(entry);
            _idleBytes += entry.Cost;
        }
    }

    private void EvictIdleUntil(long remainingBudget)
    {
        while (_idleBytes > remainingBudget && _idle.First is { } oldest)
        {
            var evicted = oldest.Value;
            RemoveIdleEntry(evicted);
            DisposeEntry(evicted);
        }
    }

    private void DisposeEntry(Entry entry)
    {
        _entries.Remove(entry.Bytes);
        entry.Value!.Dispose();
        entry.Value = null;
    }

    private void RemoveQueuedEntry(Entry entry)
    {
        var node = entry.QueueNode!;
        node.List!.Remove(node);
        entry.QueueNode = null;
        _spaceAvailable?.SetResult();
        _spaceAvailable = null;
    }

    private void RemoveIdleEntry(Entry entry)
    {
        _idle.Remove(entry.IdleNode!);
        entry.IdleNode = null;
        _idleBytes -= entry.Cost;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public TaskCompletionSource Completed { get; } = NewSignal();
        public int Users;
        public int ForegroundUsers;
        public long Cost;
        public T? Value;
        public LinkedListNode<Entry>? QueueNode;
        public LinkedListNode<Entry>? IdleNode;
    }

    internal sealed class Lease(T value, Action release) : IDisposable
    {
        private Action? _release = release;

        public T Value { get; } = value;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
