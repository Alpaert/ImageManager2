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
    /// Execute ffmpeg command with timeout support.
    /// </summary>
    /// <param name="arguments">ffmpeg arguments</param>
    /// <param name="timeoutSeconds">Hard timeout after which the process is killed.</param>
    /// <param name="ct">External cancellation token. Triggers process kill + <see cref="OperationCanceledException"/>.</param>
    /// <returns>Exit code, stdout, and stderr.</returns>
    /// <exception cref="TimeoutException">Process exceeded <paramref name="timeoutSeconds"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
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
        const int maxOutputChars = 100_000; // prevent runaway memory if ffmpeg spews

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null && outputBuilder.Length < maxOutputChars)
                outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null && errorBuilder.Length < maxOutputChars)
                errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Combine external token + hard timeout
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Hard timeout — force kill
            KillProcessTree(process);
            throw new TimeoutException($"FFmpeg timed out after {timeoutSeconds}s. Args: {arguments}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation — kill and rethrow original
            KillProcessTree(process);
            ct.ThrowIfCancellationRequested(); // unreachable, satisfies compiler
            throw; // never reached — ThrowIfCancellationRequested always throws
        }
    }

    /// <summary>
    /// Force-kill the process tree with a 3-second safety window.
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            // Give the OS a moment to reap; if still alive, nuke harder
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Process may have already exited — swallow
        }
    }
}
