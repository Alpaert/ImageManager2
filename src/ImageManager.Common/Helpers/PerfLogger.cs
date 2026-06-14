using System.Diagnostics;

namespace ImageManager.Common.Helpers;

/// <summary>性能诊断日志：输出到 %LocalAppData%\ImageManager\perf.log</summary>
public static class PerfLogger
{
    private static readonly object _lock = new();
    private static string? _logPath;

    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageManager");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "perf.log");
            }
            return _logPath;
        }
    }

    [Conditional("DEBUG")]
    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
        }
    }

    /// <summary>记录并返回 Stopwatch，用于 using 模式自动计时</summary>
    [Conditional("DEBUG")]
    public static void LogStart(string phase) => Log($"[+] {phase}");

    [Conditional("DEBUG")]
    public static void LogEnd(string phase, long elapsedMs) => Log($"[-] {phase} done {elapsedMs}ms");
}
