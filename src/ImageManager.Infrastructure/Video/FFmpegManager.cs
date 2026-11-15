using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace ImageManager.Infrastructure.Video;

public static class FFmpegManager
{
    private static string? _cachedPath;
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly ConcurrentDictionary<int, Process> OwnedProcesses = new();
    private static readonly object ProcessLifecycleGate = new();

    public static async Task<bool> ShutdownOwnedProcessesAsync(TimeSpan timeout)
    {
        Process[] processes;
        lock (ProcessLifecycleGate)
        {
            Shutdown.Cancel();
            processes = OwnedProcesses.Values.ToArray();
        }
        try
        {
            var exits = processes.Select(async process =>
            {
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { /* Handle already closed after confirmed exit. */ }
            }).ToArray();
            await Task.WhenAll(exits).WaitAsync(timeout).ConfigureAwait(false);
            return processes.All(process => { try { return process.HasExited; } catch (InvalidOperationException) { return true; } });
        }
        catch (TimeoutException) { return false; }
    }

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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown.Token);
        await ProcessSlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { return await RunOwnedProcessAsync(arguments, timeoutSeconds, lifetime.Token).ConfigureAwait(false); }
        finally { ProcessSlots.Release(); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunOwnedProcessAsync(
        string arguments, int timeoutSeconds, CancellationToken ct)
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

        lock (ProcessLifecycleGate)
        {
            ct.ThrowIfCancellationRequested();
            process.Start();
            OwnedProcesses[process.Id] = process;
        }

        // Combine external token + hard timeout
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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
        finally
        {
            // A failed kill is not proof of exit. Retain the slot/process handle until the OS confirms exit.
            // This wait is asynchronous; disposing a service cancels owned work without blocking the UI.
            if (!process.HasExited)
            {
                KillProcessTree(process);
                ImageManager.Common.Helpers.AppLogger.Warn("FFmpeg has not exited after cancellation; retaining its process slot.");
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            OwnedProcesses.TryRemove(process.Id, out _);
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
