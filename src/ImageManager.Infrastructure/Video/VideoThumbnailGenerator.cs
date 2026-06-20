using System.Globalization;
using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Video;

/// <summary>
/// 视频缩略图生成结果
/// </summary>
public class VideoThumbnailResult
{
    public byte[]? ThumbnailData { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double ThumbnailTimestamp { get; set; }
}

public static class VideoThumbnailGenerator
{
    /// <summary>
    /// 生成视频缩略图，返回缩略图数据和元数据（宽高、时长）
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    /// <param name="decodeWidth">目标宽度</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>包含缩略图数据和元数据的结果对象</returns>
    public static async Task<VideoThumbnailResult?> GenerateAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct = default)
    {
        try
        {
            AppLogger.Info($"[Video] 开始生成缩略图: {Path.GetFileName(filePath)}");

            // 获取视频元数据（仅调用一次）
            var metadata = await VideoMetadataExtractor.ExtractMetadataAsync(filePath, ct);
            if (metadata == null)
            {
                AppLogger.Warn($"[Video] 元数据提取失败: {Path.GetFileName(filePath)}");
                return null;
            }

            double duration = metadata.Value.Duration;
            int width = metadata.Value.Width;
            int height = metadata.Value.Height;

            AppLogger.Info($"[Video] 元数据: {width}x{height}, {duration:F1}s - {Path.GetFileName(filePath)}");

            // 从 25% 位置开始，避开片头黑屏和广告
            double startTimestamp = duration * 0.25;

            // 提取最佳帧
            var thumbnailData = await ExtractBestFrameAsync(filePath, startTimestamp, decodeWidth, ct);

            if (thumbnailData == null)
            {
                AppLogger.Warn($"[Video] 帧提取失败: {Path.GetFileName(filePath)}");
                return null;
            }

            AppLogger.Info($"[Video] 缩略图生成成功: {Path.GetFileName(filePath)}, {thumbnailData.Length} bytes");

            // 一次性返回所有信息
            return new VideoThumbnailResult
            {
                ThumbnailData = thumbnailData,
                Width = width,
                Height = height,
                Duration = duration,
                ThumbnailTimestamp = startTimestamp
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Video] 生成异常: {Path.GetFileName(filePath)}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 提取视频原分辨率帧（不缩放），用于缓存后续 SkiaSharp 缩放
    /// </summary>
    public static async Task<byte[]?> ExtractOriginalFrameAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var metadata = await VideoMetadataExtractor.ExtractMetadataAsync(filePath, ct);
            if (metadata == null) return null;

            double startTimestamp = metadata.Value.Duration * 0.25;
            string tsStr = startTimestamp.ToString("F2", CultureInfo.InvariantCulture);

            // 策略 1: thumbnail 智能选帧，不带 scale
            string? tempFile = null;
            try
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
                string args = $"-ss {tsStr} -i \"{filePath}\" -t 5 -vf \"thumbnail\" -frames:v 1 -update 1 -q:v 2 \"{tempFile}\"";
                var (exitCode, _, _) = await FFmpegManager.RunAsync(args, 30, ct);
                if (exitCode == 0 && File.Exists(tempFile))
                {
                    var data = await File.ReadAllBytesAsync(tempFile, ct);
                    if (data.Length > 0) return data;
                }
            }
            catch { }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }

            // 策略 2: 简单提取，不带 scale
            tempFile = null;
            try
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
                string args = $"-ss {tsStr} -i \"{filePath}\" -vframes 1 -update 1 -q:v 2 \"{tempFile}\"";
                var (exitCode, _, _) = await FFmpegManager.RunAsync(args, 30, ct);
                if (exitCode == 0 && File.Exists(tempFile))
                {
                    var data = await File.ReadAllBytesAsync(tempFile, ct);
                    if (data.Length > 0) return data;
                }
            }
            catch { }
            finally
            {
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 提取最佳帧：先尝试 thumbnail 滤镜，失败则回退到简单提取
    /// </summary>
    private static async Task<byte[]?> ExtractBestFrameAsync(
        string filePath,
        double startTimestamp,
        int decodeWidth,
        CancellationToken ct)
    {
        // 策略 1：尝试 thumbnail 滤镜（智能选帧）
        var result = await TryExtractWithThumbnailFilter(filePath, startTimestamp, decodeWidth, ct);
        if (result != null) return result;

        AppLogger.Warn($"[Video] thumbnail 滤镜失败，尝试简单提取: {Path.GetFileName(filePath)}");

        // 策略 2：回退到简单提取（固定位置）
        return await TryExtractSimpleFrame(filePath, startTimestamp, decodeWidth, ct);
    }

    /// <summary>
    /// 尝试使用 thumbnail 滤镜提取
    /// </summary>
    private static async Task<byte[]?> TryExtractWithThumbnailFilter(
        string filePath,
        double timestamp,
        int width,
        CancellationToken ct)
    {
        string? tempFile = null;
        try
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
            string tsStr = timestamp.ToString("F2", CultureInfo.InvariantCulture);
            string args = $"-ss {tsStr} -i \"{filePath}\" -t 5 -vf \"thumbnail,scale={width}:-1\" -frames:v 1 -update 1 -q:v 5 \"{tempFile}\"";

            var (exitCode, _, error) = await FFmpegManager.RunAsync(args, 30, ct);

            if (exitCode == 0 && File.Exists(tempFile))
            {
                byte[] data = await File.ReadAllBytesAsync(tempFile, ct);
                if (data.Length > 0)
                {
                    AppLogger.Info($"[Video] thumbnail 滤镜成功: {data.Length} bytes");
                    return data;
                }
            }

            if (!string.IsNullOrEmpty(error))
                AppLogger.Warn($"[Video] thumbnail stderr: {error.Substring(0, Math.Min(150, error.Length))}");

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Video] thumbnail 异常: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); }
            catch { }
        }
    }

    /// <summary>
    /// 简单提取：直接从指定位置提取帧（不使用 thumbnail 滤镜）
    /// </summary>
    private static async Task<byte[]?> TryExtractSimpleFrame(
        string filePath,
        double timestamp,
        int width,
        CancellationToken ct)
    {
        string? tempFile = null;
        try
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
            string tsStr = timestamp.ToString("F2", CultureInfo.InvariantCulture);

            // 简单提取：不使用 thumbnail 滤镜，直接缩放
            string args = $"-ss {tsStr} -i \"{filePath}\" -vframes 1 -vf scale={width}:-1 -update 1 -q:v 5 \"{tempFile}\"";

            var (exitCode, _, error) = await FFmpegManager.RunAsync(args, 30, ct);

            if (exitCode == 0 && File.Exists(tempFile))
            {
                byte[] data = await File.ReadAllBytesAsync(tempFile, ct);
                if (data.Length > 0)
                {
                    AppLogger.Info($"[Video] 简单提取成功: {data.Length} bytes");
                    return data;
                }
            }

            AppLogger.Error($"[Video] 简单提取也失败: {Path.GetFileName(filePath)}");
            if (!string.IsNullOrEmpty(error))
                AppLogger.Error($"[Video] simple stderr: {error.Substring(0, Math.Min(150, error.Length))}");

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Video] 简单提取异常: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); }
            catch { }
        }
    }

    /// <summary>
    /// 异步获取视频尺寸（兼容旧代码调用）
    /// </summary>
    public static async Task<(int Width, int Height)> GetDimensionsAsync(string filePath)
    {
        var meta = await VideoMetadataExtractor.ExtractMetadataAsync(filePath);
        return meta.HasValue ? (meta.Value.Width, meta.Value.Height) : (1920, 1080);
    }
}
