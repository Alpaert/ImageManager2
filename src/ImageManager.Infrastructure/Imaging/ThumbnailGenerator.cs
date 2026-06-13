using ImageManager.Common.Constants;
using SkiaSharp;

namespace ImageManager.Infrastructure.Imaging;


/// <summary>Decoded GIF frame with JPEG data and display duration.</summary>
public sealed class GifFrame
{
    public byte[] JpegData { get; }
    public int DurationMs { get; }

    public GifFrame(byte[] jpegData, int durationMs)
    {
        JpegData = jpegData;
        DurationMs = durationMs;
    }
}
public static class ThumbnailGenerator
{
    /// <summary>
    /// Generate thumbnail bytes (JPEG). Always uses SKCodec-based decode to avoid
    /// loading full-resolution bitmaps into memory, even for images with moderate
    /// pixel dimensions but large file sizes (e.g. 200 MB+ high-bit-depth TIFFs).
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

            // JPEG supports 1/1, 1/2, 1/4, 1/8. Pick the smallest scale >= desiredScale
            // so the decode is never smaller than target (avoids upscale blur).
            float desiredScale = (float)targetWidth / origW;

            bool useDirectDecode = false;
            int decodeW, decodeH;

            if (desiredScale >= 1f)
            {
                // Target >= original: decode at full size (small image fits in memory)
                decodeW = origW;
                decodeH = origH;
            }
            else
            {
                float jpegScale;
                if (desiredScale <= 0.125f) jpegScale = 0.125f;
                else if (desiredScale <= 0.25f) jpegScale = 0.25f;
                else if (desiredScale <= 0.5f) jpegScale = 0.5f;
                else jpegScale = 1f;

                if (jpegScale >= 1f)
                {
                    // No native scale-down available for this ratio.
                    // If original is more than 2x target, decode directly at target
                    // size to avoid loading a huge full-resolution bitmap.
                    if (origW > targetWidth * 2 || origH > targetHeight * 2)
                    {
                        useDirectDecode = true;
                        decodeW = targetWidth;
                        decodeH = targetHeight;
                    }
                    else
                    {
                        decodeW = origW;
                        decodeH = origH;
                    }
                }
                else
                {
                    var decodeSize = codec.GetScaledDimensions(jpegScale);
                    decodeW = decodeSize.Width;
                    decodeH = decodeSize.Height;

                    // Format doesn't support native scale-down (PNG, TIFF, etc.)
                    if (decodeW == 0 || decodeH == 0)
                    {
                        useDirectDecode = true;
                        decodeW = targetWidth;
                        decodeH = targetHeight;
                    }
                }
            }

            if (useDirectDecode)
            {
                // Re-open file for a fresh codec
                using var stream2 = File.OpenRead(filePath);
                using var codec2 = SKCodec.Create(stream2);
                if (codec2 == null) return null;

                var cInfo = codec2.Info;
                var ct = cInfo.ColorType != SKColorType.Unknown ? cInfo.ColorType : SKColorType.Rgba8888;
                var at = cInfo.AlphaType != SKAlphaType.Unknown ? cInfo.AlphaType : SKAlphaType.Premul;
                var info = new SKImageInfo(targetWidth, targetHeight, ct, at);

                using var bitmap = new SKBitmap(info);
                if (codec2.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                    return null;
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                return data.ToArray();
            }

            // Match output format to codec''s native color type
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
            using var jpeg = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            return jpeg?.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get raw image dimensions from file header only (no pixel decode).
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
    /// Decode file at low resolution (max side <= maxSize), encode to JPEG.
    /// Returns small byte[] suitable for hash computation ?? avoids loading full image into memory.
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

            float desiredScale = (float)targetW / origW;
            bool useDirectDecode = false;
            int decodeW, decodeH;

            if (desiredScale >= 1f)
            {
                decodeW = origW;
                decodeH = origH;
            }
            else
            {
                float jpegScale;
                if (desiredScale <= 0.125f) jpegScale = 0.125f;
                else if (desiredScale <= 0.25f) jpegScale = 0.25f;
                else if (desiredScale <= 0.5f) jpegScale = 0.5f;
                else jpegScale = 1f;

                if (jpegScale >= 1f)
                {
                    if (origW > targetW * 2 || origH > targetH * 2)
                    {
                        useDirectDecode = true;
                        decodeW = targetW;
                        decodeH = targetH;
                    }
                    else
                    {
                        decodeW = origW;
                        decodeH = origH;
                    }
                }
                else
                {
                    var ds = codec.GetScaledDimensions(jpegScale);
                    decodeW = ds.Width;
                    decodeH = ds.Height;

                    if (decodeW == 0 || decodeH == 0)
                    {
                        useDirectDecode = true;
                        decodeW = targetW;
                        decodeH = targetH;
                    }
                }
            }

            SKBitmap? final = null;
            try
            {
                if (useDirectDecode)
                {
                    using var stream2 = File.OpenRead(filePath);
                    using var codec2 = SKCodec.Create(stream2);
                    if (codec2 == null) return null;

                    var cInfo = codec2.Info;
                    var ct = cInfo.ColorType != SKColorType.Unknown ? cInfo.ColorType : SKColorType.Rgba8888;
                    var at = cInfo.AlphaType != SKAlphaType.Unknown ? cInfo.AlphaType : SKAlphaType.Premul;
                    var info = new SKImageInfo(targetW, targetH, ct, at);

                    final = new SKBitmap(info);
                    if (codec2.GetPixels(info, final.GetPixels()) != SKCodecResult.Success)
                        return null;
                }
                else
                {
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
    /// Decode image to SkiaSharp bitmap at reduced size for analysis.
    /// Uses SKCodec-based decoding to avoid full-resolution bitmap allocation.
    /// </summary>
    public static SKBitmap? DecodeForAnalysis(string filePath, int maxSize)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;

            int maxDim = Math.Max(origW, origH);
            float scale = Math.Min(1.0f, (float)maxSize / maxDim);
            int targetW = Math.Max(1, (int)(origW * scale));
            int targetH = Math.Max(1, (int)(origH * scale));

            // Try native JPEG scale first
            float jpegScale;
            if (scale >= 1f) jpegScale = 1f;
            else if (scale >= 0.5f) jpegScale = 0.5f;
            else if (scale >= 0.25f) jpegScale = 0.25f;
            else jpegScale = 0.125f;

            var scaledDims = codec.GetScaledDimensions(jpegScale);
            int decodeW = scaledDims.Width;
            int decodeH = scaledDims.Height;

            if (decodeW == 0 || decodeH == 0)
            {
                // No native scaling (PNG, TIFF, etc.): decode directly at target size
                using var stream2 = File.OpenRead(filePath);
                using var codec2 = SKCodec.Create(stream2);
                if (codec2 == null) return null;

                var cInfo = codec2.Info;
                var ct = cInfo.ColorType != SKColorType.Unknown ? cInfo.ColorType : SKColorType.Rgba8888;
                var at = cInfo.AlphaType != SKAlphaType.Unknown ? cInfo.AlphaType : SKAlphaType.Premul;
                var info = new SKImageInfo(targetW, targetH, ct, at);
                var bitmap = new SKBitmap(info);
                if (codec2.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                {
                    bitmap.Dispose();
                    return null;
                }
                return bitmap;
            }

            // Decode at native scale, then resize to exact target
            var ci = codec.Info;
            var outCT = ci.ColorType != SKColorType.Unknown ? ci.ColorType : SKColorType.Rgba8888;
            var outAT = ci.AlphaType != SKAlphaType.Unknown ? ci.AlphaType : SKAlphaType.Premul;
            var decodeInfo = new SKImageInfo(decodeW, decodeH, outCT, outAT);
            var decoded = new SKBitmap(decodeInfo);
            if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
            {
                decoded.Dispose();
                return null;
            }

            if (decodeW == targetW && decodeH == targetH)
                return decoded;

            var resized = decoded.Resize(new SKSizeI(targetW, targetH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            decoded.Dispose();
            return resized;
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

    /// <summary>
    /// Decode all frames of an animated GIF, resize each to fit within maxSize
    /// (longest edge), and encode as JPEG. Returns null on failure or if not animated.
    /// </summary>
    
    /// <summary>
    /// Decode only the first frame of a GIF. Fast path for showing initial preview
    /// before background decoding of remaining frames.
    /// </summary>
    public static GifFrame? DecodeFirstGifFrame(string filePath, int maxSize)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;
            if (codec.FrameCount < 1) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;
            int maxDim = Math.Max(origW, origH);
            float scale = maxDim > maxSize ? (float)maxSize / maxDim : 1f;
            int targetW = Math.Max(1, (int)(origW * scale));
            int targetH = Math.Max(1, (int)(origH * scale));

            var ci = codec.Info;
            var colorType = ci.ColorType != SKColorType.Unknown ? ci.ColorType : SKColorType.Rgba8888;
            var alphaType = ci.AlphaType != SKAlphaType.Unknown ? ci.AlphaType : SKAlphaType.Premul;
            var fullInfo = new SKImageInfo(origW, origH, colorType, alphaType);

            using var frameBitmap = new SKBitmap(fullInfo);
            if (codec.GetPixels(fullInfo, frameBitmap.GetPixels(), new SKCodecOptions(0))
                != SKCodecResult.Success)
                return null;

            int durationMs = codec.FrameInfo[0].Duration;
            if (durationMs <= 0) durationMs = 100;
            if (durationMs > 5000) durationMs = 5000;

            SKBitmap? resized = null;
            try
            {
                resized = scale >= 1f
                    ? frameBitmap.Copy()
                    : frameBitmap.Resize(new SKSizeI(targetW, targetH),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized == null) return null;

                using var skImage = SKImage.FromBitmap(resized);
                using var jpeg = skImage.Encode(SKEncodedImageFormat.Jpeg, 80);
                var jpegData = jpeg?.ToArray();
                return jpegData != null ? new GifFrame(jpegData, durationMs) : null;
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    public static List<GifFrame>? DecodeGifFrames(string filePath, int maxSize)
    {
        try
        {
            var fileBytes = File.ReadAllBytes(filePath);

            using var stream = new MemoryStream(fileBytes);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            int frameCount = codec.FrameCount;
            if (frameCount <= 1) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;
            int maxDim = Math.Max(origW, origH);
            float scale = maxDim > maxSize ? (float)maxSize / maxDim : 1f;
            int targetW = Math.Max(1, (int)(origW * scale));
            int targetH = Math.Max(1, (int)(origH * scale));

            var frames = new List<GifFrame>(frameCount);

            var ci = codec.Info;
            var colorType = ci.ColorType != SKColorType.Unknown ? ci.ColorType : SKColorType.Rgba8888;
            var alphaType = ci.AlphaType != SKAlphaType.Unknown ? ci.AlphaType : SKAlphaType.Premul;
            var fullInfo = new SKImageInfo(origW, origH, colorType, alphaType);

            for (int i = 0; i < frameCount; i++)
            {
                var frameInfo = codec.FrameInfo[i];
                int durationMs = frameInfo.Duration;
                if (durationMs <= 0) durationMs = 100;
                if (durationMs > 5000) durationMs = 5000;

                using var frameStream = new MemoryStream(fileBytes);
                using var frameCodec = SKCodec.Create(frameStream);
                if (frameCodec == null) return null;

                using var frameBitmap = new SKBitmap(fullInfo);
                if (frameCodec.GetPixels(fullInfo, frameBitmap.GetPixels(), new SKCodecOptions(i))
                        != SKCodecResult.Success)
                    continue;

                SKBitmap? resized = null;
                try
                {
                    if (scale >= 1f)
                        resized = frameBitmap.Copy();
                    else
                        resized = frameBitmap.Resize(new SKSizeI(targetW, targetH),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (resized == null) continue;

                    using var skImage = SKImage.FromBitmap(resized);
                    using var jpeg = skImage.Encode(SKEncodedImageFormat.Jpeg, 80);
                    var jpegData = jpeg?.ToArray();
                    if (jpegData != null)
                        frames.Add(new GifFrame(jpegData, durationMs));
                }
                finally
                {
                    resized?.Dispose();
                }
            }

            return frames.Count > 0 ? frames : null;
        }
        catch
        {
            return null;
        }
    }
}
