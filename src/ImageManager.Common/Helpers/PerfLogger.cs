using System.Diagnostics;

namespace ImageManager.Common.Helpers;

/// <summary>性能诊断日志：输出到 %LocalAppData%\ImageManager\perf.log</summary>
public static class PerfLogger
{
    private static readonly object _lock = new();
    private static string? _logPath;
    private const long MaxLogDirectoryBytes = 25L * 1024 * 1024;

    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImageManager",
                    "logs");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                CleanupOldLogs(dir, _logPath);
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

    private static void CleanupOldLogs(string dir, string currentPath)
    {
        try
        {
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.log")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            long totalBytes = files.Sum(f => f.Length);

            foreach (var file in files)
            {
                if (totalBytes <= MaxLogDirectoryBytes) break;
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var length = file.Length;
                try
                {
                    file.Delete();
                    totalBytes -= length;
                }
                catch { }
            }
        }
        catch { }
    }
}
