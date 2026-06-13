using ImageManager.Common.Constants;
using ImageManager.Core.Services;
using SkiaSharp;

namespace ImageManager.Infrastructure.Imaging;

/// <summary>图片媒体处理器</summary>
public class ImageMediaProcessor : IMediaProcessor
{
    public bool CanHandle(string extension)
        => FileTypeConstants.ImageExtensions.Contains(extension);

    public async Task<MediaResult?> ExtractThumbnailAsync(
        string filePath,
        int decodeWidth,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                // 第一步：打开文件读取原始尺寸（仅头信息，不解码像素）
                using var stream = File.OpenRead(filePath);
                using var codec = SKCodec.Create(stream);
                if (codec == null) return null;

                int origWidth = codec.Info.Width;
                int origHeight = codec.Info.Height;

                // 第二步：调用现有 ThumbnailGenerator 生成缩略图
                // （内部会再次打开文件，但逻辑已验证稳定，风险最小）
                var thumbnailData = ThumbnailGenerator.Generate(filePath, decodeWidth);
                if (thumbnailData == null) return null;

                return new MediaResult
                {
                    Data = thumbnailData,
                    Width = origWidth,
                    Height = origHeight
                };
            }
            catch
            {
                return null;
            }
        }, ct);
    }

    public (int Width, int Height) GetDimensions(string filePath)
    {
        return ThumbnailGenerator.GetDimensions(filePath);
    }
}
