using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class DuplicateServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;
    private readonly MediaMoveService _mediaMoveService;
    private readonly DuplicateService _duplicateService;

    public DuplicateServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-DupServiceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "dup.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        var versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, versionTracker);
        var folderRepo = new FolderRepository(_dbFactory, versionTracker);
        var thumbCache = new TestThumbnailCacheService();
        _mediaMoveService = new MediaMoveService(_dbFactory, _metaRepo, folderRepo, thumbCache, versionTracker: versionTracker);
        _duplicateService = new DuplicateService(_metaRepo, _mediaMoveService);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    private string CreateImageWithColor(string name, SkiaSharp.SKColor color)
    {
        var path = Path.Combine(_testRoot, name);
        using var bitmap = new SkiaSharp.SKBitmap(32, 32);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(color);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
        return path;
    }

    [Fact]
    public async Task DetectAndMoveDuplicatesAsync_MovesExactDuplicates()
    {
        var targetDir = Path.Combine(_testRoot, "dup_target");

        // Two identical red images, one blue image
        var red1 = CreateImageWithColor("red1.png", SkiaSharp.SKColors.Red);
        var red2 = CreateImageWithColor("red2.png", SkiaSharp.SKColors.Red);
        var blue = CreateImageWithColor("blue.png", SkiaSharp.SKColors.Blue);

        // Act
        var (exact, fuzzy) = await _duplicateService.DetectAndMoveDuplicatesAsync(
            new[] { red1, red2, blue }, targetDir);

        // Assert
        Assert.Equal(1, exact);

        // First red and blue remain in source
        Assert.True(File.Exists(red1));
        Assert.True(File.Exists(blue));

        // Duplicate red2 moved to target
        Assert.False(File.Exists(red2));
        Assert.True(File.Exists(Path.Combine(targetDir, "red2.png")));
    }

    [Fact]
    public async Task DetectAndMoveDuplicatesAsync_Cancellation_StopsEarly()
    {
        var targetDir = Path.Combine(_testRoot, "dup_cancel_target");
        var img1 = CreateImageWithColor("img1.png", SkiaSharp.SKColors.Red);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Act
        var (exact, fuzzy) = await _duplicateService.DetectAndMoveDuplicatesAsync(
            new[] { img1 }, targetDir, cts.Token);

        // Assert: 0 moved
        Assert.Equal(0, exact);
        Assert.Equal(0, fuzzy);
    }
}
