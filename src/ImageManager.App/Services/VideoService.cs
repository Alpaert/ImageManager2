using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using LibVLCSharp.Shared;

namespace ImageManager.App.Services;

public class VideoService : IVideoService, IDisposable
{
    // Shared LibVLC instance — initialized once by App startup, used by both thumbnail and playback
    private static LibVLCSharp.Shared.LibVLC? _sharedLibVlc;
    private static readonly object _initLock = new();

    public static LibVLCSharp.Shared.LibVLC GetOrCreateLibVLC()
    {
        lock (_initLock)
        {
            if (_sharedLibVlc == null)
            {
                _sharedLibVlc = new LibVLCSharp.Shared.LibVLC(
                    "--no-video-title-show",
                    "--quiet"
                );
            }
            return _sharedLibVlc;
        }
    }

    public async System.Threading.Tasks.Task<byte[]?> ExtractThumbnailAsync(string filePath, int maxWidth)
    {
        return await System.Threading.Tasks.Task.Run(() => ExtractThumbnail(filePath, maxWidth));
    }

    private static string? _ffmpegPath;
    private static bool _ffmpegSearched;

    private static string? FindFfmpeg()
    {
        // 1. bundled with app
        var appPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (System.IO.File.Exists(appPath)) return appPath;

        // 2. in cache directory
        var cachePath = System.IO.Path.Combine(App.CacheDirectoryPath, "ffmpeg", "ffmpeg.exe");
        if (System.IO.File.Exists(cachePath)) return cachePath;

        // 3. in PATH
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var firstLine = output.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstLine != null && System.IO.File.Exists(firstLine.Trim()))
                return firstLine.Trim();
        }
        catch { }

        return null;
    }

    private byte[]? ExtractThumbnail(string filePath, int maxWidth)
    {
        if (!_ffmpegSearched)
        {
            _ffmpegPath = FindFfmpeg();
            _ffmpegSearched = true;
        }

        if (_ffmpegPath == null)
            return null;

        try
        {
            // -ss after -i for container compatibility (MKV etc.); format=yuvj420p ensures JPEG-valid output
            var args = $"-i \"{filePath}\" -ss 0.5 -vframes 1 -vf \"scale={maxWidth}:-1,format=yuvj420p\" -vcodec mjpeg -f mjpeg -";
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            using var ms = new System.IO.MemoryStream();
            process.Start();
            process.BeginErrorReadLine(); // async consume stderr to avoid pipe deadlock
            process.StandardOutput.BaseStream.CopyTo(ms);
            process.WaitForExit(5000);

            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch (Exception ex) { AppLogger.Warn($"FFmpeg thumbnail failed for {filePath}: {ex.Message}"); return null; }
    }

    public (int Width, int Height) GetVideoDimensions(string filePath)
    {
        try
        {
            var libVlc = GetOrCreateLibVLC();
            using var media = new Media(libVlc, new Uri(filePath));
            media.Parse(MediaParseOptions.ParseLocal);
            foreach (var track in media.Tracks)
            {
                if (track.TrackType == TrackType.Video)
                {
                    var vt = track.Data.Video;
                    if (vt.Width > 0 && vt.Height > 0)
                        return ((int)vt.Width, (int)vt.Height);
                }
            }
        }
        catch (Exception ex) { AppLogger.Warn($"GetVideoDimensions failed for {filePath}: {ex.Message}"); }
        return (0, 0);
    }

    public void Dispose()
    {
        lock (_initLock)
        {
            _sharedLibVlc?.Dispose();
            _sharedLibVlc = null;
        }
    }
}
