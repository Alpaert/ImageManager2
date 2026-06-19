using System.Diagnostics;
using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Helpers;

/// <summary>
/// LOH 纰庣墖鍖栬嚜閫傚簲绠＄悊銆?/// 64浣?GC 榛樿涓嶅帇缂?LOH 鈫?SKBitmap 瑙ｇ爜纰庣墖绱Н 鈫?commit exhaustion銆?/// 鐢?Private/Heap 姣斿€间綔涓虹鐗囦唬鐞嗘寚鏍囷紝鍥涚骇鍘嬪姏鑷€傚簲瑙﹀彂 Gen2+LOH 鍘嬬缉銆?/// </summary>
public static class MemoryPressureMonitor
{
    public enum PressureLevel { Low, Medium, High, Critical }
    private const double MediumCommitMB = 5200;
    private const double HighCommitMB = 6500;
    private const double CriticalCommitMB = 8000;
    private const double MinHeapForFragmentationMB = 512;

    // ==================== 鐘舵€?====================
    private static readonly object _lock = new();
    private static long _largeAllocCount;
    private static long _lastCompactAtCount;
    private static PressureLevel _cachedLevel = PressureLevel.Low;
    private static DateTime _lastLevelRefresh = DateTime.MinValue;
    private static readonly TimeSpan LevelRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim _compactGate = new(1, 1);

    // ==================== 鍏紑灞炴€?====================

    public static PressureLevel Current
    {
        get
        {
            if (DateTime.UtcNow - _lastLevelRefresh > LevelRefreshInterval)
                RefreshPressureLevel();
            return _cachedLevel;
        }
    }

    /// <summary>纰庣墖璇勫垎: (PrivateMB - HeapMB) / HeapMB銆傚€艰秺澶х鐗囪秺涓ラ噸銆?/summary>
    public static double FragmentationScore => ComputeScore();

    /// <summary>褰撳墠 Private commit (MB)</summary>
    public static double CommitChargeMB
    {
        get
        {
            try { return Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0; }
            catch { return 0; }
        }
    }

    // ==================== 鍐崇瓥 API ====================

    /// <summary>鏄惁搴旇鎵ц LOH 鍘嬬缉锛堣皟鐢ㄦ柟鍦ㄥ垎閰嶅ぇ瀵硅薄鍚庤皟鐢級</summary>
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

    // ==================== 鎿嶄綔 API ====================

    /// <summary>璁板綍涓€娆″ぇ瀵硅薄鍒嗛厤锛堢缉鐣ュ浘鐢熸垚銆佸紶閲忓垎閰嶇瓑锛?/summary>
    public static void RecordAllocation()
    {
        Interlocked.Increment(ref _largeAllocCount);
    }

    /// <summary>鎵ц Gen2 + LOH 鍘嬬缉锛堝湪鍚庡彴绾跨▼鎵ц锛屼笉闃诲璋冪敤鑰咃級</summary>
    public static void CompactLoh()
    {
        var levelBefore = Current;
        double scoreBefore = ComputeScore();
        double commitBefore = CommitChargeMB;

        // 鍦ㄥ悗鍙扮嚎绋嬫墽琛屼互閬垮厤闃诲 UI
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!_compactGate.Wait(0)) return;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                Interlocked.Exchange(ref _lastCompactAtCount, Interlocked.Read(ref _largeAllocCount));
                // 寮哄埗鍒锋柊缂撳瓨
                _lastLevelRefresh = DateTime.MinValue;

                double scoreAfter = ComputeScore();
                double commitAfter = CommitChargeMB;
                double freedMB = commitBefore - commitAfter;
                AppLogger.Memory($"LohCompact level={levelBefore}->{Current} " +
                    $"score={scoreBefore:F1}->{scoreAfter:F1} " +
                    $"commit={commitBefore:F0}->{commitAfter:F0}MB " +
                    $"freed={freedMB:F1}MB");
            }
            catch { }
            finally { _compactGate.Release(); }
        });
    }

    /// <summary>Critical 绾у埆绱ф€ュ洖鏀讹細娓呯┖璋冪敤鏂规彁渚涚殑娓呯悊鍔ㄤ綔 + 鍘嬬缉</summary>
    public static void EmergencyCleanup(Action? cleanupAction = null)
    {
        AppLogger.Memory($"EmergencyCleanup level={Current} score={ComputeScore():F1}");
        cleanupAction?.Invoke();
        // 鍚屾鎵ц 鈥?Critical 鏃舵€ц兘宸蹭弗閲嶅姡鍖栵紝浼樺厛鍥炴敹
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        Interlocked.Exchange(ref _lastCompactAtCount, Interlocked.Read(ref _largeAllocCount));
        _lastLevelRefresh = DateTime.MinValue;
        AppLogger.Memory($"EmergencyCleanup done level={Current}");
    }

    // ==================== 鍐呴儴 ====================

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

            double commitMB = CommitChargeMB;
            double heapMB = GC.GetTotalMemory(false) / 1048576.0;
            double score = ComputeScore();
            bool scoreIsMeaningful = heapMB >= MinHeapForFragmentationMB;

            if (commitMB >= CriticalCommitMB)
                _cachedLevel = PressureLevel.Critical;
            else if (commitMB >= HighCommitMB || (scoreIsMeaningful && commitMB >= MediumCommitMB && score > 30))
                _cachedLevel = PressureLevel.High;
            else if (commitMB >= MediumCommitMB || (scoreIsMeaningful && commitMB >= 4000 && score > 10))
                _cachedLevel = PressureLevel.Medium;
            else
                _cachedLevel = PressureLevel.Low;
        }
    }
}

