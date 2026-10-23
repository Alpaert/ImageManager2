using ImageManager.Infrastructure.Imaging;
using SkiaSharp;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class ImagingAndThumbnailTests : IDisposable
{
    private readonly string _testRoot;

    public ImagingAndThumbnailTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-ImagingTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    private string CreateTestImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var ext = format == SKEncodedImageFormat.Jpeg ? ".jpg" : ".png";
        var path = Path.Combine(_testRoot, $"test_{width}x{height}_{Guid.NewGuid():N}{ext}");
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void GetDimensions_ValidImage_ReturnsAccurateDimensions()
    {
        var imagePath = CreateTestImage(320, 160);

        var (w, h) = ThumbnailGenerator.GetDimensions(imagePath);
        Assert.Equal(320, w);
        Assert.Equal(160, h);

        var fileBytes = File.ReadAllBytes(imagePath);
        var (wb, hb) = ThumbnailGenerator.GetDimensions(fileBytes);
        Assert.Equal(320, wb);
        Assert.Equal(160, hb);
    }

    [Fact]
    public void GetDimensions_CorruptAndMissingFiles_ReturnsFallbackOneByOne()
    {
        var missingPath = Path.Combine(_testRoot, "non_existent.png");
        var (w1, h1) = ThumbnailGenerator.GetDimensions(missingPath);
        Assert.Equal(1, w1);
        Assert.Equal(1, h1);

        var corruptPath = Path.Combine(_testRoot, "corrupt.png");
        File.WriteAllBytes(corruptPath, [0x00, 0xFF, 0x12, 0x34]);
        var (w2, h2) = ThumbnailGenerator.GetDimensions(corruptPath);
        Assert.Equal(1, w2);
        Assert.Equal(1, h2);

        var (w3, h3) = ThumbnailGenerator.GetDimensions([0xAA, 0xBB]);
        Assert.Equal(1, w3);
        Assert.Equal(1, h3);
    }

    [Fact]
    public void GetDimensionsOrUnknown_DistinguishesValidFromCorruptOrMissing()
    {
        var validPath = CreateTestImage(240, 120);
        var (w1, h1) = ThumbnailGenerator.GetDimensionsOrUnknown(validPath);
        Assert.Equal(240, w1);
        Assert.Equal(120, h1);

        var missingPath = Path.Combine(_testRoot, "missing.png");
        var (w2, h2) = ThumbnailGenerator.GetDimensionsOrUnknown(missingPath);
        Assert.Equal(0, w2);
        Assert.Equal(0, h2);

        var corruptPath = Path.Combine(_testRoot, "corrupt.png");
        File.WriteAllBytes(corruptPath, [0x01, 0x02, 0x03]);
        var (w3, h3) = ThumbnailGenerator.GetDimensionsOrUnknown(corruptPath);
        Assert.Equal(0, w3);
        Assert.Equal(0, h3);
    }

    [Fact]
    public void Generate_FromFileAndFromBytes_ProducesResizedThumbnail()
    {
        var imagePath = CreateTestImage(400, 200);

        // Generate from file path
        var thumbBytes1 = ThumbnailGenerator.Generate(imagePath, decodeWidth: 100);
        Assert.NotNull(thumbBytes1);
        Assert.True(thumbBytes1.Length > 0);

        var (tw1, th1) = ThumbnailGenerator.GetDimensions(thumbBytes1);
        Assert.Equal(100, tw1);
        Assert.Equal(50, th1);

        // Generate from raw image bytes
        var sourceBytes = File.ReadAllBytes(imagePath);
        var thumbBytes2 = ThumbnailGenerator.Generate(sourceBytes, decodeWidth: 80);
        Assert.NotNull(thumbBytes2);
        Assert.True(thumbBytes2.Length > 0);

        var (tw2, th2) = ThumbnailGenerator.GetDimensions(thumbBytes2);
        Assert.Equal(80, tw2);
        Assert.Equal(40, th2);
    }

    [Fact]
    public void Generate_CorruptOrMissing_ReturnsNullGracefully()
    {
        var missingPath = Path.Combine(_testRoot, "does_not_exist.jpg");
        Assert.Null(ThumbnailGenerator.Generate(missingPath, 100));

        var corruptPath = Path.Combine(_testRoot, "garbage.jpg");
        File.WriteAllBytes(corruptPath, [0x01, 0x02]);
        Assert.Null(ThumbnailGenerator.Generate(corruptPath, 100));

        Assert.Null(ThumbnailGenerator.Generate([0x01, 0x02], 100));
    }

    [Fact]
    public void NormalizeJpeg_ValidAndInvalidInputs_HandledCorrectly()
    {
        var imagePath = CreateTestImage(600, 300);
        var rawBytes = File.ReadAllBytes(imagePath);

        var normalized = ThumbnailGenerator.NormalizeJpeg(rawBytes, maxLongestEdge: 300);
        Assert.NotNull(normalized);
        Assert.True(normalized.Length > 0);

        var (nw, nh) = ThumbnailGenerator.GetDimensions(normalized);
        Assert.Equal(300, Math.Max(nw, nh));

        // Invalid input returns null
        Assert.Null(ThumbnailGenerator.NormalizeJpeg([0x12, 0x34], 100));
    }

    [Fact]
    public void DecodeRawPixels_ValidImage_ReturnsBgraPixelsAndExpectedSize()
    {
        var imagePath = CreateTestImage(200, 100);

        var (pixels, w, h) = ThumbnailGenerator.DecodeRawPixels(imagePath, maxWidth: 100);
        Assert.NotNull(pixels);
        Assert.Equal(100, w);
        Assert.Equal(50, h);
        Assert.Equal(100 * 50 * 4, pixels.Length);

        // Missing file returns null, 0, 0
        var (missingPixels, mw, mh) = ThumbnailGenerator.DecodeRawPixels(Path.Combine(_testRoot, "none.png"), 100);
        Assert.Null(missingPixels);
        Assert.Equal(0, mw);
        Assert.Equal(0, mh);
    }

    [Fact]
    public void DecodeForHashInput_ProducesLowResolutionJpegBytes()
    {
        var imagePath = CreateTestImage(800, 600);

        var hashInput = ThumbnailGenerator.DecodeForHashInput(imagePath, maxSize: 128);
        Assert.NotNull(hashInput);

        var (hw, hh) = ThumbnailGenerator.GetDimensions(hashInput);
        Assert.True(Math.Max(hw, hh) <= 128);
        Assert.True(Math.Max(hw, hh) > 0);

        // Non-existent file
        Assert.Null(ThumbnailGenerator.DecodeForHashInput(Path.Combine(_testRoot, "fake.jpg"), 128));
    }

    [Fact]
    public async Task ImageMediaProcessor_CanHandle_And_ExtractThumbnailAsync()
    {
        var processor = new ImageMediaProcessor();

        // CanHandle checks
        Assert.True(processor.CanHandle(".jpg"));
        Assert.True(processor.CanHandle(".jpeg"));
        Assert.True(processor.CanHandle(".png"));
        Assert.True(processor.CanHandle(".webp"));
        Assert.True(processor.CanHandle(".bmp"));
        Assert.True(processor.CanHandle(".gif"));
        Assert.False(processor.CanHandle(".mp4"));
        Assert.False(processor.CanHandle(".txt"));
        Assert.False(processor.CanHandle(".mkv"));

        // Valid image
        var imagePath = CreateTestImage(400, 200);
        var dimensions = processor.GetDimensions(imagePath);
        Assert.Equal(400, dimensions.Width);
        Assert.Equal(200, dimensions.Height);

        var result = await processor.ExtractThumbnailAsync(imagePath, decodeWidth: 100, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(400, result.Width);
        Assert.Equal(200, result.Height);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Length > 0);

        // Missing file returns null
        var missingResult = await processor.ExtractThumbnailAsync(
            Path.Combine(_testRoot, "not_there.png"),
            100,
            CancellationToken.None);
        Assert.Null(missingResult);

        // Corrupt file returns null
        var corruptPath = Path.Combine(_testRoot, "broken.png");
        File.WriteAllBytes(corruptPath, [0x00, 0x11, 0x22]);
        var corruptResult = await processor.ExtractThumbnailAsync(corruptPath, 100, CancellationToken.None);
        Assert.Null(corruptResult);
    }
}
