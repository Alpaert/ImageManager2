namespace ImageManager.Core.Services;

/// <summary>媒体处理结果（包含缩略图数据和元数据）</summary>
public class MediaResult
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
    public double? Duration { get; set; }        // 视频专用
    public double? Timestamp { get; set; }       // 视频专用
}

/// <summary>媒体处理器接口（策略模式）</summary>
public interface IMediaProcessor
{
    /// <summary>判断是否可处理该扩展名</summary>
    bool CanHandle(string extension);

    /// <summary>提取缩略图（异步，返回数据+尺寸）</summary>
    Task<MediaResult?> ExtractThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default);

    /// <summary>获取原始尺寸（同步，仅头信息）</summary>
    (int Width, int Height) GetDimensions(string filePath);
}
