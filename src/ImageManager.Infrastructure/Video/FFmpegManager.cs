using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ImageManager.Infrastructure.Video;

public static class FFmpegManager
{
    private static string? _cachedPath;

    /// <summary>
    /// Get ffmpeg.exe path for current platform architecture
    /// </summary>
    public static string GetFFmpegPath()
    {
        if (_cachedPath != null && File.Exists(_cachedPath))
            return _cachedPath;

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string ffmpegPath = Path.Combine(appDir, "libffmpeg", $"win-{arch}", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
            throw new FileNotFoundException($"ffmpeg.exe not found at {ffmpegPath}");

        _cachedPath = ffmpegPath;
        return _cachedPath;
    }

    /// <summary>
    /// Execute ffmpeg command with timeout support
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string arguments,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetFFmpegPath(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"FFmpeg timeout after {timeoutSeconds}s");
        }
    }
}
