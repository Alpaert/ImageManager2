using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Video;
using SkiaSharp;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class ChineseClipAndVideoServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _cacheDir;

    public ChineseClipAndVideoServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-MediaServiceTests-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_testRoot, "cache");
        Directory.CreateDirectory(_cacheDir);
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

    [Fact]
    public void ChineseClip_ValidateModelFiles_ThrowsFileNotFoundException_WhenMissing()
    {
        using var clipService = new ChineseClipService(_cacheDir);

        Assert.Contains(ChineseClipService.ModelKey, clipService.ModelDirectory);

        var exImage = Assert.Throws<FileNotFoundException>(() => clipService.ValidateImageModelFiles());
        Assert.Contains("Chinese-CLIP 模型文件不存在", exImage.Message);

        var exText = Assert.Throws<FileNotFoundException>(() => clipService.ValidateTextModelFiles());
        Assert.Contains("Chinese-CLIP 模型文件不存在", exText.Message);
    }

    [Fact]
    public async Task ChineseClip_GetTextEmbeddingAsync_NullOrWhitespace_ReturnsEmptyArray()
    {
        using var clipService = new ChineseClipService(_cacheDir);

        var resultEmpty = await clipService.GetTextEmbeddingAsync(string.Empty);
        Assert.Empty(resultEmpty);

        var resultWhitespace = await clipService.GetTextEmbeddingAsync("   \t\r\n  ");
        Assert.Empty(resultWhitespace);
    }

    [Fact]
    public void ChineseClip_SessionReleaseAndDispose_CanBeCalledSafely()
    {
        using var clipService = new ChineseClipService(_cacheDir);

        // Can release without active sessions without error
        clipService.ReleaseImageSession();
        clipService.ReleaseAllSessions();
    }

    [Fact]
    public async Task ChineseClip_InvalidModelProtobuf_ThrowsOnnxRuntimeException()
    {
        var modelDir = Path.Combine(_cacheDir, "models", ChineseClipService.ModelKey);
        var onnxDir = Path.Combine(modelDir, "onnx");
        Directory.CreateDirectory(onnxDir);

        // Create corrupt onnx file and valid vocab file
        File.WriteAllBytes(Path.Combine(onnxDir, "chinese_clip_text_encoder.onnx"), [0x01, 0x02]);
        File.WriteAllLines(Path.Combine(modelDir, "vocab.txt"), ["[CLS]", "[SEP]", "[PAD]", "[UNK]"]);

        using var clipService = new ChineseClipService(_cacheDir);

        // When EnsureTextSession runs inside GetTextEmbeddingAsync, ONNX Runtime fails to parse invalid protobuf
        await Assert.ThrowsAnyAsync<Microsoft.ML.OnnxRuntime.OnnxRuntimeException>(() =>
            clipService.GetTextEmbeddingAsync("hello world"));
    }

    [Fact]
    public void VideoMediaProcessor_CanHandle_RecognizesStandardVideoFormats()
    {
        var frameCache = new VideoOriginalFrameCacheService(_cacheDir);
        var processor = new VideoMediaProcessor(frameCache);

        Assert.True(processor.CanHandle(".mp4"));
        Assert.True(processor.CanHandle(".mkv"));
        Assert.True(processor.CanHandle(".avi"));
        Assert.True(processor.CanHandle(".mov"));
        Assert.True(processor.CanHandle(".wmv"));
        Assert.True(processor.CanHandle(".flv"));
        Assert.True(processor.CanHandle(".webm"));
        Assert.True(processor.CanHandle(".m4v"));

        Assert.False(processor.CanHandle(".png"));
        Assert.False(processor.CanHandle(".jpg"));
        Assert.False(processor.CanHandle(".mp3"));
        Assert.False(processor.CanHandle(".txt"));
    }

    [Fact]
    public async Task VideoMediaProcessor_MissingFile_ReturnsNullOrFallbackDimensions()
    {
        var frameCache = new VideoOriginalFrameCacheService(_cacheDir);
        var processor = new VideoMediaProcessor(frameCache);

        var missingVideo = Path.Combine(_testRoot, "nonexistent.mp4");

        // Missing file dimensions return (1920, 1080)
        var dims = processor.GetDimensions(missingVideo);
        Assert.Equal(1920, dims.Width);
        Assert.Equal(1080, dims.Height);

        // Missing file thumbnail returns null
        var thumb = await processor.ExtractThumbnailAsync(missingVideo, 200, CancellationToken.None);
        Assert.Null(thumb);
    }

    [Fact]
    public async Task VideoMediaProcessor_CachedOriginalFrame_GeneratesThumbnailAndDimensions()
    {
        var frameCache = new VideoOriginalFrameCacheService(_cacheDir);
        var processor = new VideoMediaProcessor(frameCache);

        var fakeVideo = Path.Combine(_testRoot, "test_movie.mp4");
        File.WriteAllBytes(fakeVideo, [0x00, 0x00, 0x00, 0x20]); // dummy file

        // Create an original frame cached image (e.g. 640x360)
        var cachedFramePath = frameCache.GetOriginalFramePath(fakeVideo);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedFramePath)!);

        using (var bitmap = new SKBitmap(640, 360))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.DarkGreen);
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            File.WriteAllBytes(cachedFramePath, data.ToArray());
        }

        // GetDimensions should now return cached frame's dimensions (640, 360)
        var dims = processor.GetDimensions(fakeVideo);
        Assert.Equal(640, dims.Width);
        Assert.Equal(360, dims.Height);

        // ExtractThumbnailAsync should successfully return media result from cached frame
        var result = await processor.ExtractThumbnailAsync(fakeVideo, decodeWidth: 160, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(640, result.Width);
        Assert.Equal(360, result.Height);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Length > 0);
    }

    [Fact]
    public async Task VideoService_HandlesMissingFilesGracefully()
    {
        var videoService = new VideoService();
        var missingPath = Path.Combine(_testRoot, "missing.mp4");

        // Non-existent video returns (0, 0)
        var dims = videoService.GetVideoDimensions(missingPath);
        Assert.Equal(0, dims.Width);
        Assert.Equal(0, dims.Height);

        // Non-existent video thumbnail returns null
        var thumb = await videoService.ExtractThumbnailAsync(missingPath, 200);
        Assert.Null(thumb);
    }
}
