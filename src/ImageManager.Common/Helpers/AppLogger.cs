using System.Runtime.CompilerServices;

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
