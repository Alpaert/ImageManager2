namespace ImageManager.Common.Helpers;

/// <summary>Monotonic-time policy; deciding to collect is separate from executing GC.</summary>
public sealed class MemoryCollectionPolicy
{
    private readonly object _gate = new();
    private long? _highSince;
    private long? _lastCollection;

    public bool ShouldCollect(MemoryPressureMonitor.PressureLevel level, long nowMs, long lastInteractionMs)
    {
        lock (_gate) return ShouldCollectCore(level, nowMs, lastInteractionMs);
    }

    public void ObservePressure(MemoryPressureMonitor.PressureLevel level, long nowMs)
    {
        lock (_gate)
        {
            if (level < MemoryPressureMonitor.PressureLevel.High) _highSince = null;
            else _highSince ??= nowMs;
        }
    }

    private bool ShouldCollectCore(MemoryPressureMonitor.PressureLevel level, long nowMs, long lastInteractionMs)
    {
        if (level < MemoryPressureMonitor.PressureLevel.High)
        {
            _highSince = null;
            return false;
        }
        _highSince ??= nowMs;
        if (level == MemoryPressureMonitor.PressureLevel.Critical)
            return !_lastCollection.HasValue || nowMs - _lastCollection.Value >= 30_000;

        return nowMs - _highSince.Value >= 30_000 && nowMs - lastInteractionMs >= 5_000 &&
               (!_lastCollection.HasValue || nowMs - _lastCollection.Value >= 120_000);
    }

    public void RecordCollection(long nowMs)
    {
        lock (_gate) _lastCollection = nowMs;
    }
}
