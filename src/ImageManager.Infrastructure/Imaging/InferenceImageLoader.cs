using ImageManager.Infrastructure.Imaging;
using SkiaSharp;

namespace ImageManager.Infrastructure.Imaging;

/// <summary>
/// 负责打标推理前的图像加载与解码。
/// 确保在双模型打标管线中只对磁盘文件进行一次解码，
/// 随后将同一份原始位图分发给各模型提取张量。
/// </summary>
public static class InferenceImageLoader
{
    public const int DefaultDecodeMaxSize = 896; // 448 * 2

    /// <summary>
    /// 一次性解码图像，限制最长边不超过 maxSize（默认 896），
    /// 原生利用 JPEG 等格式的硬件/快速降采样。
    /// </summary>
    public static SKBitmap? DecodeForInference(string imagePath, int maxSize = DefaultDecodeMaxSize)
    {
        return ThumbnailGenerator.DecodeForAnalysis(imagePath, maxSize);
    }
}
