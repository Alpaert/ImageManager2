using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Video;

/// <summary>Opt-in native-process checks; never included in the ordinary filter/layout regression run.</summary>
internal static class MediaPreviewProcessChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ImageManager-MediaProcessSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "testsrc.mp4");
            var cacheRoot = Path.Combine(directory, "cache");
            var generated = await FFmpegManager.RunAsync(
                $"-y -f lavfi -i testsrc=size=320x180:rate=10 -t 2 -c:v mpeg4 -pix_fmt yuv420p \"{source}\"", 30);
            Require(generated.ExitCode == 0 && File.Exists(source) && new FileInfo(source).Length > 0,
                "native FFmpeg must generate the disposable testsrc video: " + generated.Error);

            byte[] initialBytes;
            using (var originals = new VideoOriginalFrameCacheService(cacheRoot))
            using (var previews = new ThumbnailCacheService(new MediaProcessorFactory(originals), cacheRoot))
            {
                var first = await previews.LoadPreviewAsync(source, 160).WaitAsync(TimeSpan.FromSeconds(45));
                Require(first.State == MediaPreviewState.Ready && first.Persistence == PreviewPersistence.Saved &&
                    first.Data is { Length: > 0 } && first.Width == 160 && first.Height > 0,
                    "real video extraction must produce a display-sized Ready + Saved preview: " + first.Error);
                initialBytes = first.Data!;
            }

            using (var originals = new VideoOriginalFrameCacheService(cacheRoot,
                (_, _) => throw new InvalidOperationException("A warm restart must not invoke extraction.")))
            using (var previews = new ThumbnailCacheService(new MediaProcessorFactory(originals), cacheRoot))
            {
                var cached = await previews.LoadPreviewAsync(source, 160).WaitAsync(TimeSpan.FromSeconds(5));
                Require(cached.State == MediaPreviewState.Ready && cached.Persistence == PreviewPersistence.Saved &&
                    cached.Data != null && cached.Data.SequenceEqual(initialBytes),
                    "a fresh service instance must reuse the verified persisted preview without extraction");
            }
            Console.WriteLine("PASS real FFmpeg preview extraction, persistence, and fresh-service cache reuse");

            // This produces no media file. -re keeps the owned process alive long enough to verify shutdown.
            var longRunning = FFmpegManager.RunAsync(
                "-re -f lavfi -i testsrc=size=64x64:rate=10 -t 120 -f null -", 150);
            using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!OwnedProcesses().Any(process => !HasExited(process)))
            {
                if (longRunning.IsCompleted)
                    throw new InvalidOperationException("The long-running FFmpeg job ended before shutdown could be tested: " +
                        (await longRunning).Error);
                await Task.Delay(25, startupDeadline.Token);
            }
            Require(!longRunning.IsCompleted, "the manager-owned FFmpeg process must be running before shutdown");
            var stopped = await FFmpegManager.ShutdownOwnedProcessesAsync(TimeSpan.FromSeconds(5));
            Require(stopped, "shutdown must confirm that every manager-owned FFmpeg process has exited within five seconds");
            try
            {
                await longRunning.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("The long-running job must report cancellation after shutdown.");
            }
            catch (OperationCanceledException) { }
            Require(OwnedProcesses().All(HasExited), "shutdown must not leave a registered live FFmpeg process");
            Console.WriteLine("PASS real manager-owned FFmpeg shutdown confirms exit and cancels its original task");
        }
        finally
        {
            // Uses only the manager's ownership registry; never enumerates or kills unrelated FFmpeg processes.
            await FFmpegManager.ShutdownOwnedProcessesAsync(TimeSpan.FromSeconds(5));
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static Process[] OwnedProcesses()
    {
        var registry = (ConcurrentDictionary<int, Process>)typeof(FFmpegManager)
            .GetField("OwnedProcesses", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return registry.Values.ToArray();
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
