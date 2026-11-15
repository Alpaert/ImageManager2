namespace ImageManager.Infrastructure.Caching;

internal static class MediaWorkPriority
{
    internal static readonly AsyncLocal<Func<int>?> Current = new();
}

/// <summary>Two active jobs and sixteen pending admissions, with independently cancellable subscribers.</summary>
internal sealed class SharedMediaWork<T>(bool cancelRunningWhenUnobserved = false, int maxConcurrency = 2, int maxPending = 16) : IDisposable
{
    private sealed class Work(string key, Func<CancellationToken, Task<T>> action, int priority)
    {
        public readonly string Key = key;
        public readonly Func<CancellationToken, Task<T>> Action = action;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Subscribers = 1, Priority = priority;
        public readonly Func<int>? ParentPriority = MediaWorkPriority.Current.Value;
        public int EffectivePriority => ParentPriority == null ? Priority : Math.Max(Priority, ParentPriority());
        public bool Started, Released;
        public bool ContinueWhenUnobserved;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _work = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Work> _queue = new();
    private readonly SemaphoreSlim _admission = new(maxConcurrency + maxPending, maxConcurrency + maxPending);
    private readonly CancellationTokenSource _shutdown = new();
    private int _workers;
    internal int PendingCount { get { lock (_gate) return _queue.Count; } }

    public async Task<T> RunAsync(string key, Func<CancellationToken, Task<T>> action, CancellationToken ct, int priority = 0,
        bool continueWhenUnobserved = false)
    {
        priority = Math.Max(priority, MediaWorkPriority.Current.Value?.Invoke() ?? priority);
        Work? work;
        lock (_gate)
        {
            _work.TryGetValue(key, out work);
            if (work != null) { work.Subscribers++; work.Priority = Math.Max(work.Priority, priority); }
        }
        if (work == null)
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            await _admission.WaitAsync(admission.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_work.TryGetValue(key, out work))
                {
                    work.Subscribers++;
                    work.Priority = Math.Max(work.Priority, priority);
                    _admission.Release();
                }
                else
                {
                    work = new Work(key, action, priority) { ContinueWhenUnobserved = continueWhenUnobserved };
                    _work.Add(key, work);
                    _queue.Add(work);
                    while (_workers < maxConcurrency && _queue.Count > 0)
                    {
                        _workers++;
                        _ = Task.Run(WorkerAsync);
                    }
                }
            }
        }
        try { return await work.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (--work.Subscribers == 0 && !work.Completion.Task.IsCompleted &&
                    (!work.Started || (cancelRunningWhenUnobserved && !work.ContinueWhenUnobserved)))
                {
                    RemoveIdentity(work);
                    work.Cancellation.Cancel();
                    if (!work.Started)
                    {
                        _queue.Remove(work);
                        work.Completion.TrySetCanceled();
                        Release(work);
                    }
                }
            }
        }
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (_queue.Count == 0) { _workers--; return; }
                // Stable FIFO within a priority, but an interactive subscription promotes a queued job.
                var index = 0;
                for (var i = 1; i < _queue.Count; i++)
                    if (_queue[i].EffectivePriority > _queue[index].EffectivePriority) index = i;
                work = _queue[index];
                _queue.RemoveAt(index);
                work.Started = true;
            }
            var previousPriority = MediaWorkPriority.Current.Value;
            MediaWorkPriority.Current.Value = () => work.EffectivePriority;
            try
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, work.Cancellation.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                work.Completion.TrySetResult(await work.Action(lifetime.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
            catch (Exception ex) { work.Completion.TrySetException(ex); }
            finally
            {
                MediaWorkPriority.Current.Value = previousPriority;
                lock (_gate) { RemoveIdentity(work); Release(work); }
                _ = work.Completion.Task.Exception;
            }
        }
    }
    private void RemoveIdentity(Work work)
    {
        if (_work.TryGetValue(work.Key, out var current) && ReferenceEquals(current, work)) _work.Remove(work.Key);
    }
    private void Release(Work work)
    {
        if (work.Released) return;
        work.Released = true;
        _admission.Release();
    }
    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_gate)
        {
            foreach (var work in _queue)
            {
                RemoveIdentity(work);
                work.Completion.TrySetCanceled();
                Release(work);
            }
            _queue.Clear();
        }
    }
}
