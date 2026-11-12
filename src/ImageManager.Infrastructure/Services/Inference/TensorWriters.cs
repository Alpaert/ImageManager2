using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services.Inference;

/// <summary>
/// 负责将已解码的原始图像转换为各 ONNX 模型所需格式的张量。
/// 支持零拷贝或直接填充到目标 DenseTensor / Span 缓冲区中。
/// </summary>
public static class TensorWriters
{
    /// <summary>
    /// 将原图按比例白底居中绘制到 targetSize x targetSize 画布上。
    /// </summary>
    public static void DrawCenteredToCanvas(SKBitmap src, SKBitmap targetCanvas, int targetSize, SKColor background)
    {
        targetCanvas.Erase(background);

        int maxDim = Math.Max(src.Width, src.Height);
        float scale = (float)targetSize / maxDim;
        float drawW = src.Width * scale;
        float drawH = src.Height * scale;
        var dest = new SKRect(
            (targetSize - drawW) / 2f,
            (targetSize - drawH) / 2f,
            (targetSize + drawW) / 2f,
            (targetSize + drawH) / 2f);

        using var canvas = new SKCanvas(targetCanvas);
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawBitmap(src, dest, paint);
    }

    /// <summary>
    /// 将 448x448 缩放图像填充为 WD 模型的 NHWC 格式张量: [1, 448, 448, 3], float32, BGR 顺序, 0-255 范围。
    /// </summary>
    public static unsafe void WriteNhwcBgrRaw(SKBitmap resized, DenseTensor<float> tensor, int size)
    {
        byte* ptr = (byte*)resized.GetPixels().ToPointer();
        int stride = resized.RowBytes;
        int bpp = resized.BytesPerPixel;
        bool isBgra = resized.ColorType == SKColorType.Bgra8888;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = y * stride + x * bpp;
                float b, g, r;
                if (isBgra)
                {
                    b = ptr[offset];
                    g = ptr[offset + 1];
                    r = ptr[offset + 2];
                }
                else
                {
                    r = ptr[offset];
                    g = ptr[offset + 1];
                    b = ptr[offset + 2];
                }

                tensor[0, y, x, 0] = b; // B
                tensor[0, y, x, 1] = g; // G
                tensor[0, y, x, 2] = r; // R
            }
        }
    }

    /// <summary>
    /// 将 448x448 缩放图像填充为 PixAI 模型的 NCHW 格式张量: [1, 3, 448, 448], float32, RGB 顺序, 归一化 (x/255 - mean) / std。
    /// </summary>
    public static unsafe void WriteNchwRgbNormalized(
        SKBitmap resized, DenseTensor<float> tensor, int size, float[] mean, float[] std)
    {
        byte* ptr = (byte*)resized.GetPixels().ToPointer();
        int stride = resized.RowBytes;
        int bpp = resized.BytesPerPixel;
        bool isBgra = resized.ColorType == SKColorType.Bgra8888;

        float meanR = mean[0], meanG = mean[1], meanB = mean[2];
        float stdR = std[0], stdG = std[1], stdB = std[2];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = y * stride + x * bpp;
                float r, g, b;
                if (isBgra)
                {
                    b = ptr[offset];
                    g = ptr[offset + 1];
                    r = ptr[offset + 2];
                }
                else
                {
                    r = ptr[offset];
                    g = ptr[offset + 1];
                    b = ptr[offset + 2];
                }

                tensor[0, 0, y, x] = (r / 255f - meanR) / stdR; // R
                tensor[0, 1, y, x] = (g / 255f - meanG) / stdG; // G
                tensor[0, 2, y, x] = (b / 255f - meanB) / stdB; // B
            }
        }
    }
}
