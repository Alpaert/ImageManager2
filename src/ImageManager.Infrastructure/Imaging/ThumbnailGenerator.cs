using SkiaSharp;

namespace ImageManager.Infrastructure.Imaging;

public static class ThumbnailGenerator
{
    /// <summary>
    /// Generate thumbnail bytes (JPEG). Uses SKCodec.GetScaledDimensions for codec-supported
    /// decode size, then resizes to exact target. Avoids full-resolution bitmap in memory.
    /// </summary>
    public static byte[]? Generate(string filePath, int decodeWidth)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;

            int targetWidth = decodeWidth;
            int targetHeight = Math.Max(1, (int)(origH * ((float)decodeWidth / origW)));

            // Don't upscale, but do high-quality downscale if orig is larger than needed
            if (targetWidth >= origW)
            {
                using var original = SKBitmap.Decode(filePath);
                if (original == null) return null;
                // If the original is within 2x of target, return as-is; otherwise downscale
                if (origW <= targetWidth * 2 && origH <= targetHeight * 2)
                {
                    using var image = SKImage.FromBitmap(original);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                    return data.ToArray();
                }
                // Original much larger than target — downscale for clean display
                using var resized = original.Resize(new SKSizeI(targetWidth, targetHeight),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized == null) return null;
                using var rImage = SKImage.FromBitmap(resized);
                using var rData = rImage.Encode(SKEncodedImageFormat.Jpeg, 85);
                return rData.ToArray();
            }

            // JPEG supports 1/1, 1/2, 1/4, 1/8. Pick the smallest scale >= desiredScale
            // so the decode is never smaller than target (avoids upscale blur).
            float desiredScale = (float)targetWidth / origW;
            float jpegScale;
            if (desiredScale <= 0.125f) jpegScale = 0.125f;
            else if (desiredScale <= 0.25f) jpegScale = 0.25f;
            else if (desiredScale <= 0.5f) jpegScale = 0.5f;
            else jpegScale = 1f;

            int decodeW, decodeH;
            if (jpegScale >= 1f)
            {
                decodeW = origW;
                decodeH = origH;
            }
            else
            {
                var decodeSize = codec.GetScaledDimensions(jpegScale);
                decodeW = decodeSize.Width;
                decodeH = decodeSize.Height;
            }

            // Match output format to codec's native color type
            var ci = codec.Info;
            var outColorType = ci.ColorType != SKColorType.Unknown ? ci.ColorType : SKColorType.Rgba8888;
            var outAlphaType = ci.AlphaType != SKAlphaType.Unknown ? ci.AlphaType : SKAlphaType.Premul;

            var decodeInfo = new SKImageInfo(decodeW, decodeH, outColorType, outAlphaType);
            using var decoded = new SKBitmap(decodeInfo);
            if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
                return null;

            SKBitmap final;
            if (decodeW == targetWidth && decodeH == targetHeight)
            {
                final = decoded;
            }
            else
            {
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                final = decoded.Resize(new SKSizeI(targetWidth, targetHeight), sampling);
                if (final == null) return null;
            }

            using var skImage = SKImage.FromBitmap(final);
            using var jpegData = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            if (final != decoded) final.Dispose();
            return jpegData?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get original image dimensions without full decoding
    /// </summary>
    public static (int Width, int Height) GetDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return (1, 1);
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (1, 1);
        }
    }

    /// <summary>Get image dimensions from byte array (no disk I/O)</summary>
    public static (int Width, int Height) GetDimensions(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return (1, 1);
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (1, 1);
        }
    }

    /// <summary>
    /// Decode file at low resolution (max side ≤ maxSize), encode to JPEG.
    /// Returns small byte[] suitable for hash computation — avoids loading full image into memory.
    /// </summary>
    public static byte[]? DecodeForHashInput(string filePath, int maxSize = 256)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;

            int targetW = maxSize;
            int targetH = Math.Max(1, (int)(origH * ((float)maxSize / origW)));
            if (targetH > maxSize)
            {
                targetH = maxSize;
                targetW = Math.Max(1, (int)(origW * ((float)maxSize / origH)));
            }

            SKBitmap? final = null;
            try
            {
                if (targetW >= origW)
                {
                    final = SKBitmap.Decode(filePath);
                    if (final == null) return null;
                    if (origW > targetW * 2 || origH > targetH * 2)
                    {
                        var r = final.Resize(new SKSizeI(targetW, targetH),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        if (r == null) return null;
                        final.Dispose();
                        final = r;
                    }
                }
                else
                {
                    // Same two-step decode as Generate(): native scale → resize
                    float desiredScale = (float)targetW / origW;
                    float jpegScale;
                    if (desiredScale <= 0.125f) jpegScale = 0.125f;
                    else if (desiredScale <= 0.25f) jpegScale = 0.25f;
                    else if (desiredScale <= 0.5f) jpegScale = 0.5f;
                    else jpegScale = 1f;

                    int decodeW, decodeH;
                    if (jpegScale >= 1f) { decodeW = origW; decodeH = origH; }
                    else
                    {
                        var ds = codec.GetScaledDimensions(jpegScale);
                        decodeW = ds.Width;
                        decodeH = ds.Height;
                    }

                    var ci = codec.Info;
                    var outColorType = ci.ColorType != SKColorType.Unknown ? ci.ColorType : SKColorType.Rgba8888;
                    var outAlphaType = ci.AlphaType != SKAlphaType.Unknown ? ci.AlphaType : SKAlphaType.Premul;
                    var decodeInfo = new SKImageInfo(decodeW, decodeH, outColorType, outAlphaType);

                    final = new SKBitmap(decodeInfo);
                    if (codec.GetPixels(decodeInfo, final.GetPixels()) != SKCodecResult.Success)
                        return null;

                    if (decodeW != targetW || decodeH != targetH)
                    {
                        var r = final.Resize(new SKSizeI(targetW, targetH),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        if (r == null) return null;
                        final.Dispose();
                        final = r;
                    }
                }

                using var skImage = SKImage.FromBitmap(final);
                using var jpeg = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
                return jpeg?.ToArray();
            }
            finally
            {
                final?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decode image to SkiaSharp bitmap at reduced size for analysis
    /// </summary>
    public static SKBitmap? DecodeForAnalysis(string filePath, int maxSize)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(filePath);
            if (bitmap == null) return null;

            float scale = Math.Min(1.0f, (float)maxSize / Math.Max(bitmap.Width, bitmap.Height));
            int w = Math.Max(1, (int)(bitmap.Width * scale));
            int h = Math.Max(1, (int)(bitmap.Height * scale));

            return bitmap.Resize(
                new SKSizeI(w, h),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Load grayscale bytes from image file, scaled to maxSize.
    /// </summary>
    public static (byte[]? Pixels, int Width, int Height) LoadGrayBytes(string filePath, int maxSize)
    {
        try
        {
            using var bmp = DecodeForAnalysis(filePath, maxSize);
            if (bmp == null) return (null, 0, 0);

            int w = bmp.Width;
            int h = bmp.Height;
            byte[] pixels = new byte[w * h];

            unsafe
            {
                byte* ptr = (byte*)bmp.GetPixels().ToPointer();
                int stride = bmp.RowBytes;
                int bpp = bmp.BytesPerPixel;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int offset = y * stride + x * bpp;
                        byte gray = (byte)(0.299 * ptr[offset] + 0.587 * ptr[offset + 1] + 0.114 * ptr[offset + 2]);
                        pixels[y * w + x] = gray;
                    }
                }
            }

            return (pixels, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
    }
}
