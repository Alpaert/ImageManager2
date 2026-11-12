using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace ImageManager.Common.Helpers;

/// <summary>Samples pressure away from the UI. Native memory is not LOH fragmentation.</summary>
public static class MemoryPressureMonitor
{
    public enum PressureLevel { Low, Medium, High, Critical }
    private const double MediumCommitMB = 5200, HighCommitMB = 6500, CriticalCommitMB = 8000;
    private static readonly SemaphoreSlim CompactGate = new(1, 1);
    private static readonly MemoryCollectionPolicy CollectionPolicy = new();
    private static int _cachedLevel, _samplePending;
    private static long _lastSampleMs = long.MinValue / 2;
    private static long _lastInteractionMs = Environment.TickCount64;
    private static double _commitMB;

    public static PressureLevel Current
    {
        get
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastSampleMs) >= 5_000 &&
                Interlocked.CompareExchange(ref _samplePending, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { RefreshPressureLevel(); }
                    finally { Volatile.Write(ref _samplePending, 0); }
                });
            return (PressureLevel)Volatile.Read(ref _cachedLevel);
        }
    }

    /// <summary>Last GC's measured managed-heap fragmentation ratio, not private/heap.</summary>
    public static double FragmentationScore
    {
        get
        {
            var info = GC.GetGCMemoryInfo();
            return info.FragmentedBytes / (double)Math.Max(1L, info.HeapSizeBytes);
        }
    }

    /// <summary>Last background sample of private commit in MiB.</summary>
    public static double CommitChargeMB => Volatile.Read(ref _commitMB);
    public static void NotifyInteraction() => Interlocked.Exchange(ref _lastInteractionMs, Environment.TickCount64);
    public static bool ShouldCompactNow() => Current >= PressureLevel.High;
    public static int RecommendedPageSize(int normalSize) => Current switch
    {
        PressureLevel.Critical => normalSize / 2,
        PressureLevel.High => normalSize * 3 / 4,
        _ => normalSize
    };
    public static int RecommendedCacheLimit(int normal) => Current switch
    {
        PressureLevel.Critical => normal / 4,
        PressureLevel.High => normal / 2,
        _ => normal
    };
    // Kept for allocation callers; pressure no longer depends on allocation counts.
    public static void RecordAllocation() { }

    /// <summary>
    /// Automatic calls require sustained pressure, idle time and a cooldown.
    /// force is reserved for the manual command. Worker-initiated GC still pauses the UI.
    /// </summary>
    public static void CompactLoh(bool force = false)
    {
        if (!CompactGate.Wait(0)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                RefreshPressureLevel();
                var now = Environment.TickCount64;
                var level = (PressureLevel)Volatile.Read(ref _cachedLevel);
                if (!force && !CollectionPolicy.ShouldCollect(level, now, Interlocked.Read(ref _lastInteractionMs)))
                    return;
                var before = CommitChargeMB;
                if (!force && level != PressureLevel.Critical &&
                    Environment.TickCount64 - Interlocked.Read(ref _lastInteractionMs) < 5_000)
                    return;
                CollectOnce();
                RefreshPressureLevel();
                AppLogger.Memory($"MemoryCollect manual={force} level={level} commit={before:F0}->{CommitChargeMB:F0}MB");
            }
            catch (Exception ex) { AppLogger.Warn($"Memory collection failed: {ex.Message}"); }
            finally { CompactGate.Release(); }
        });
    }

    public static void EmergencyCleanup(Action? cleanupAction = null)
    {
        cleanupAction?.Invoke();
        if (!CompactGate.Wait(0)) return;
        try
        {
            CollectOnce();
            RefreshPressureLevel();
            AppLogger.Memory($"EmergencyCleanup done level={(PressureLevel)Volatile.Read(ref _cachedLevel)}");
        }
        finally { CompactGate.Release(); }
    }

    private static void CollectOnce()
    {
        CollectionPolicy.RecordCollection(Environment.TickCount64);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void RefreshPressureLevel()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var commit = process.PrivateMemorySize64 / 1048576.0;
            Volatile.Write(ref _commitMB, commit);
            Volatile.Write(ref _cachedLevel, (int)(commit >= CriticalCommitMB ? PressureLevel.Critical :
                commit >= HighCommitMB ? PressureLevel.High : commit >= MediumCommitMB ? PressureLevel.Medium : PressureLevel.Low));
            CollectionPolicy.ObservePressure((PressureLevel)Volatile.Read(ref _cachedLevel), Environment.TickCount64);
            Interlocked.Exchange(ref _lastSampleMs, Environment.TickCount64);
        }
        catch { Interlocked.Exchange(ref _lastSampleMs, Environment.TickCount64); }
    }
}
