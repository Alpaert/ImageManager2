using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ImageManager.Common.Helpers;

/// <summary>
/// 简单文件日志，用于复合打标模块调试。
/// 日志输出到 {CacheDir}\logs\ensemble_{yyyyMMdd}.log
/// </summary>
public static class AppLogger
{
    private static string? _logDir;
    private static readonly object _lock = new();
    private static string _currentDate = string.Empty;
    private static StreamWriter? _writer;

    // ==================== 内存日志频率控制 ====================
    // 相同 (caller, phase) 每 ThrottleInterval 次调用只输出一次，避免日志爆炸
    private static readonly Dictionary<string, (int count, int nextLogAt)> _memThrottle = new();
    private const int MemThrottleInterval = 50;
    private static readonly HashSet<string> _memAlways = new(StringComparer.Ordinal)
    {
        // 保证关键节点永不被节流
    };

    /// <summary>注册永远不节流的 phase 前缀（匹配 caller+phase）</summary>
    public static void MemAlways(string phasePrefix)
    {
        lock (_memThrottle) { _memAlways.Add(phasePrefix); }
    }

    public static void Init(string cacheDir)
    {
        _logDir = System.IO.Path.Combine(cacheDir, "logs");
        System.IO.Directory.CreateDirectory(_logDir);
    }

    public static void Info(string message, [CallerMemberName] string caller = "")
        => Write("INFO", message, caller);

    public static void Warn(string message, [CallerMemberName] string caller = "")
        => Write("WARN", message, caller);

    public static void Error(string message, [CallerMemberName] string caller = "")
        => Write("ERROR", message, caller);

    public static void Tag(string stage, string detail, [CallerMemberName] string caller = "")
        => Write("TAG", $"[{stage}] {detail}", caller);

    /// <summary>
    /// 内存诊断日志。记录 GC 代数计数、托管堆、工作集、私有提交内存。
    /// 相同 (caller, phase) 每 50 次调用只输出一次；用 MemAlways() 注册不过滤的 phase 前缀。
    /// </summary>
    public static void Memory(string phase, [CallerMemberName] string caller = "")
    {
        var key = $"{caller}|{NormalizeMemoryPhase(phase)}";
        bool always = false;
        lock (_memThrottle)
        {
            foreach (var prefix in _memAlways)
            {
                if (phase.StartsWith(prefix, StringComparison.Ordinal))
                {
                    always = true;
                    break;
                }
            }
            if (!always)
            {
                if (_memThrottle.TryGetValue(key, out var entry))
                {
                    int newCount = entry.count + 1;
                    if (newCount < entry.nextLogAt)
                    {
                        _memThrottle[key] = (newCount, entry.nextLogAt);
                        return;
                    }
                    _memThrottle[key] = (0, newCount + MemThrottleInterval);
                }
                else
                {
                    _memThrottle[key] = (0, MemThrottleInterval);
                }
            }
        }

        try
        {
            var proc = Process.GetCurrentProcess();
            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);
            double heapMB = GC.GetTotalMemory(false) / 1048576.0;   // 托管堆
            double wsMB = proc.WorkingSet64 / 1048576.0;             // 工作集（物理内存）
            double privMB = proc.PrivateMemorySize64 / 1048576.0;    // 私有提交（commit charge）
            string availStr = "";
            try
            {
                var info = GC.GetGCMemoryInfo();
                double availMB = info.TotalAvailableMemoryBytes / 1048576.0;
                availStr = $" Avail={availMB:F0}MB";
            }
            catch { /* 旧版 .NET 无此 API */ }

            Write("MEM", $"{phase} | GC:{g0}/{g1}/{g2} | Heap={heapMB:F1}MB | WS={wsMB:F1}MB | Priv={privMB:F1}MB{availStr}", caller);
        }
        catch { /* 内存日志失败不应影响主流程 */ }
    }

    private static string NormalizeMemoryPhase(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return phase;

        var sb = new StringBuilder(phase.Length);
        var previousWasToken = false;
        foreach (var ch in phase)
        {
            if (char.IsDigit(ch))
            {
                if (!previousWasToken)
                {
                    sb.Append('#');
                    previousWasToken = true;
                }
                continue;
            }

            previousWasToken = false;
            sb.Append(ch);
        }

        var normalized = sb.ToString();
        var lastSpace = normalized.LastIndexOf(' ');
        var lastTokenStart = lastSpace >= 0 ? lastSpace + 1 : 0;
        var fileExtIndex = normalized.LastIndexOf('.');
        if (fileExtIndex > lastTokenStart && LooksLikeFileExtension(normalized, fileExtIndex))
        {
            if (lastSpace >= 0)
                normalized = normalized[..(lastSpace + 1)] + "<file>";
        }

        return normalized;
    }

    private static bool LooksLikeFileExtension(string value, int dotIndex)
    {
        int extLength = value.Length - dotIndex - 1;
        if (extLength is < 2 or > 5) return false;

        for (int i = dotIndex + 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i])) return false;
        }
        return true;
    }

    private static void Write(string level, string message, string caller)
    {
        if (_logDir == null) return;
        var now = DateTime.Now;
        var date = now.ToString("yyyyMMdd");

        lock (_lock)
        {
            try
            {
                if (date != _currentDate)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(
                        System.IO.Path.Combine(_logDir, $"ensemble_{date}.log"), append: true);
                    _currentDate = date;
                }

                var ts = now.ToString("HH:mm:ss.fff");
                _writer?.WriteLine($"[{ts}] [{level}] [{caller}] {message}");
                _writer?.Flush();
            }
            catch { /* 日志失败不应影响主流程 */ }
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
