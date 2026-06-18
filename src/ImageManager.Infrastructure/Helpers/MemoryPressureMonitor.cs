using System.Diagnostics;
using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Helpers;

/// <summary>
/// LOH 碎片化自适应管理。
/// 64位 GC 默认不压缩 LOH → SKBitmap 解码碎片累积 → commit exhaustion。
/// 用 Private/Heap 比值作为碎片代理指标，四级压力自适应触发 Gen2+LOH 压缩。
/// </summary>
public static class MemoryPressureMonitor
{
    public enum PressureLevel { Low, Medium, High, Critical }

    // ==================== 状态 ====================
    private static readonly object _lock = new();
    private static long _largeAllocCount;
    private static long _lastCompactAtCount;
    private static PressureLevel _cachedLevel = PressureLevel.Low;
    private static DateTime _lastLevelRefresh = DateTime.MinValue;
    private static readonly TimeSpan LevelRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim _compactGate = new(1, 1);

    // ==================== 公开属性 ====================

    public static PressureLevel Current
    {
        get
        {
            if (DateTime.UtcNow - _lastLevelRefresh > LevelRefreshInterval)
                RefreshPressureLevel();
            return _cachedLevel;
        }
    }

    /// <summary>碎片评分: (PrivateMB - HeapMB) / HeapMB。值越大碎片越严重。</summary>
    public static double FragmentationScore => ComputeScore();

    /// <summary>当前 Private commit (MB)</summary>
    public static double CommitChargeMB
    {
        get
        {
            try { return Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0; }
            catch { return 0; }
        }
    }

    // ==================== 决策 API ====================

    /// <summary>是否应该执行 LOH 压缩（调用方在分配大对象后调用）</summary>
    public static bool ShouldCompactNow()
    {
        var level = Current;
        long count = Interlocked.Read(ref _largeAllocCount);
        long lastCompact = Interlocked.Read(ref _lastCompactAtCount);
        long sinceLast = count - lastCompact;

        return level switch
        {
            PressureLevel.Low => false,
            PressureLevel.Medium => sinceLast >= 50,
            PressureLevel.High => sinceLast >= 20,
            PressureLevel.Critical => sinceLast >= 5,
            _ => false
        };
    }

    public static int RecommendedPageSize(int normalSize)
    {
        return Current switch
        {
            PressureLevel.Critical => normalSize / 2,
            PressureLevel.High => normalSize * 3 / 4,
            _ => normalSize
        };
    }

    public static int RecommendedCacheLimit(int normal)
    {
        return Current switch
        {
            PressureLevel.Critical => normal / 4,
            PressureLevel.High => normal / 2,
            _ => normal
        };
    }

    // ==================== 操作 API ====================

    /// <summary>记录一次大对象分配（缩略图生成、张量分配等）</summary>
    public static void RecordAllocation()
    {
        Interlocked.Increment(ref _largeAllocCount);
    }

    /// <summary>执行 Gen2 + LOH 压缩（在后台线程执行，不阻塞调用者）</summary>
    public static void CompactLoh()
    {
        var levelBefore = Current;
        double scoreBefore = ComputeScore();
        double commitBefore = CommitChargeMB;

        // 在后台线程执行以避免阻塞 UI
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!_compactGate.Wait(0)) return; // 已有压缩在进行
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                Interlocked.Exchange(ref _lastCompactAtCount, Interlocked.Read(ref _largeAllocCount));
                // 强制刷新缓存
                _lastLevelRefresh = DateTime.MinValue;

                double scoreAfter = ComputeScore();
                double commitAfter = CommitChargeMB;
                double freedMB = commitBefore - commitAfter;
                AppLogger.Memory($"LohCompact level={levelBefore}→{Current} " +
                    $"score={scoreBefore:F1}→{scoreAfter:F1} " +
                    $"commit={commitBefore:F0}→{commitAfter:F0}MB " +
                    $"freed={freedMB:F1}MB");
            }
            catch { }
            finally { _compactGate.Release(); }
        });
    }

    /// <summary>Critical 级别紧急回收：清空调用方提供的清理动作 + 压缩</summary>
    public static void EmergencyCleanup(Action? cleanupAction = null)
    {
        AppLogger.Memory($"EmergencyCleanup level={Current} score={ComputeScore():F1}");
        cleanupAction?.Invoke();
        // 同步执行 — Critical 时性能已严重劣化，优先回收
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        Interlocked.Exchange(ref _lastCompactAtCount, Interlocked.Read(ref _largeAllocCount));
        _lastLevelRefresh = DateTime.MinValue;
        AppLogger.Memory($"EmergencyCleanup done level={Current}");
    }

    // ==================== 内部 ====================

    private static double ComputeScore()
    {
        try
        {
            double heap = GC.GetTotalMemory(false) / 1048576.0;
            double priv = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
            double denom = Math.Max(1, heap);
            return (priv - heap) / denom;
        }
        catch { return 0; }
    }

    private static void RefreshPressureLevel()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastLevelRefresh <= LevelRefreshInterval)
                return;
            _lastLevelRefresh = DateTime.UtcNow;

            double heapMB = GC.GetTotalMemory(false) / 1048576.0;
            double score = ComputeScore();
            double commitMB = CommitChargeMB;
            bool scoreIsMeaningful = heapMB >= 256;

            if (commitMB > 8000 || (scoreIsMeaningful && score > 50))
                _cachedLevel = PressureLevel.Critical;
            else if (commitMB > 6500 || (scoreIsMeaningful && score > 20))
                _cachedLevel = PressureLevel.High;
            else if (commitMB > 4000 || (scoreIsMeaningful && score > 5))
                _cachedLevel = PressureLevel.Medium;
            else
                _cachedLevel = PressureLevel.Low;
        }
    }
}
