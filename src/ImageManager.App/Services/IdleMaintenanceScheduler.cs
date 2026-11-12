namespace ImageManager.App.Services;

/// <summary>Owns only automatic work. Interaction cancels it without cancelling manual jobs.</summary>
internal sealed class IdleMaintenanceScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _idleDelay;
    private long _lastInteraction;
    private long _lastFailure;
    private TimeSpan _failureDelay;
    private int _consecutiveFailures;
    private bool _pending;
    private bool _disposed;
    private CancellationTokenSource? _running;
    private Task _completion = Task.CompletedTask;

    public IdleMaintenanceScheduler(TimeProvider? clock = null, TimeSpan? idleDelay = null)
    {
        _clock = clock ?? TimeProvider.System;
        _idleDelay = idleDelay ?? TimeSpan.FromSeconds(5);
        _lastInteraction = _clock.GetTimestamp();
    }

    public bool HasPendingWork { get { lock (_sync) return !_disposed && _pending; } }
    public Task Completion { get { lock (_sync) return _completion; } }
    public TimeSpan RemainingIdle
    {
        get
        {
            lock (_sync)
            {
                var remaining = _idleDelay - _clock.GetElapsedTime(_lastInteraction);
                var failureRemaining = _failureDelay - _clock.GetElapsedTime(_lastFailure);
                if (failureRemaining > remaining) remaining = failureRemaining;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    public void Request()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending = true;
            _failureDelay = TimeSpan.Zero;
            _consecutiveFailures = 0;
            NotifyInteractionCore();
        }
    }

    public void NotifyInteraction()
    {
        lock (_sync)
        {
            if (!_disposed) NotifyInteractionCore();
        }
    }

    public TimeSpan RecordFailure(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            // Request cancels the old run under this same lock. Its late failure
            // must not impose the old folder's backoff on the new request.
            cancellationToken.ThrowIfCancellationRequested();
            _lastFailure = _clock.GetTimestamp();
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
            _failureDelay = TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << (_consecutiveFailures - 1))));
            return _failureDelay;
        }
    }

    private void NotifyInteractionCore()
    {
        _lastInteraction = _clock.GetTimestamp();
        if (_running != null)
        {
            _pending = true;
            if (!_running.IsCancellationRequested) _running.Cancel();
        }
    }

    // The callback returns false when a shared gate is busy or work must be retried.
    // finished must marshal scheduling back to the UI; it is called after the worker releases its gates.
    public bool TryStart(Func<CancellationToken, Task<bool>> work, CancellationToken parent, Action finished)
    {
        lock (_sync)
        {
            if (_disposed || !_pending || _running != null || parent.IsCancellationRequested || RemainingIdle > TimeSpan.Zero)
                return false;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _running = cts;
            _pending = false;
            _completion = Task.Run(async () =>
            {
                var completed = false;
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    completed = await work(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                finally
                {
                    lock (_sync)
                    {
                        if (!_disposed && !completed) _pending = true;
                        if (completed)
                        {
                            _failureDelay = TimeSpan.Zero;
                            _consecutiveFailures = 0;
                        }
                        _running = null;
                        cts.Dispose();
                    }
                    finished();
                }
            });
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = false;
            _running?.Cancel(); // The worker owns disposal after all token users have returned.
        }
    }
}
