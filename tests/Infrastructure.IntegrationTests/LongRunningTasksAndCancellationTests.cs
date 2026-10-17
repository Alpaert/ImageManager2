using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class LongRunningTasksAndCancellationTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly string _cacheDir;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;
    private readonly ImageEmbeddingRepository _embeddingRepo;
    private readonly CharacterEmbeddingStore _characterStore;
    private readonly ArtistEmbeddingStore _artistStore;
    private readonly ChineseTagLibrary _chineseLib;
    private readonly TestThumbnailCacheService _thumbCache;

    public LongRunningTasksAndCancellationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-LongRunningTests-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_testRoot, "cache");
        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_cacheDir);

        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using (var conn = _dbFactory.CreateConnection())
        {
            DatabaseInitializer.Initialize(conn);
        }

        var versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, versionTracker);
        var suppressionRepo = new CharacterTagSuppressionRepository(_dbFactory);
        _embeddingRepo = new ImageEmbeddingRepository(_dbFactory, suppressionRepo);

        _characterStore = new CharacterEmbeddingStore();
        _artistStore = new ArtistEmbeddingStore();
        _chineseLib = new ChineseTagLibrary();
        _thumbCache = new TestThumbnailCacheService(_cacheDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
        return Task.CompletedTask;
    }

    private string CreateFakeImageFile(string subDir, string fileName)
    {
        var dir = Path.Combine(_testRoot, subDir);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, fileName);
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.LightCoral);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
        return path;
    }

    private static float[] CreateVector(int seed, int dimension = 1024)
    {
        var vec = new float[dimension];
        var rnd = new Random(seed);
        for (int i = 0; i < dimension; i++)
        {
            vec[i] = (float)rnd.NextDouble();
        }
        // Normalize
        float norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
        {
            for (int i = 0; i < dimension; i++)
                vec[i] /= norm;
        }
        return vec;
    }

    #region Character Matching Tests

    [Fact]
    public async Task MatchCharactersAsync_WhenStoreEmpty_ReturnsErrorWithoutWriting()
    {
        // Character store is empty
        _characterStore.Clear();
        var folder = Path.Combine(_testRoot, "EmptyStoreTest");
        Directory.CreateDirectory(folder);
        CreateFakeImageFile("EmptyStoreTest", "img1.jpg");

        var service = new CharacterMatchingService(_metaRepo, _embeddingRepo, _characterStore);
        var result = await service.MatchCharactersAsync(new CharacterMatchRequest(folder));

        Assert.False(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Contains("为空", result.ErrorMessage);
        Assert.Equal(0, result.WrittenTagsCount);
    }

    [Fact]
    public async Task MatchCharactersAsync_WhenNoImagesFound_ReturnsZeroResult()
    {
        _characterStore.Add("CharA", CreateVector(1), 1);
        var folder = Path.Combine(_testRoot, "NoImagesFolder");
        Directory.CreateDirectory(folder);

        var service = new CharacterMatchingService(_metaRepo, _embeddingRepo, _characterStore);
        var result = await service.MatchCharactersAsync(new CharacterMatchRequest(folder));

        Assert.True(result.Completed);
        Assert.Equal(0, result.TotalImages);
        Assert.Equal(0, result.WrittenTagsCount);
    }

    [Fact]
    public async Task MatchCharactersAsync_MissingEmbeddings_AccuratelyReportedAndSkipped()
    {
        var vecTarget = CreateVector(42);
        _characterStore.Clear();
        _characterStore.Add("TargetChar", vecTarget, 1);

        var folder = Path.Combine(_testRoot, "MissingEmbeddingsFolder");
        Directory.CreateDirectory(folder);

        // 5 images in folder
        var imgPaths = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            imgPaths.Add(CreateFakeImageFile("MissingEmbeddingsFolder", $"pic_{i}.jpg"));
        }

        // Only 2 images have embeddings in DB
        for (int i = 0; i < 2; i++)
        {
            var metaId = await _metaRepo.UpsertAsync(new ImageMeta
            {
                FilePath = imgPaths[i],
                FileHash = Guid.NewGuid().ToString("N"),
                FileSize = 100,
                LastWriteTicks = DateTime.UtcNow.Ticks
            });
            await _embeddingRepo.UpsertAsync(metaId, Guid.NewGuid().ToString("N"), vecTarget, "pixai", "v0.9");
        }

        var service = new CharacterMatchingService(_metaRepo, _embeddingRepo, _characterStore);
        var result = await service.MatchCharactersAsync(new CharacterMatchRequest(folder, MatchThreshold: 0.1));

        Assert.True(result.Completed);
        Assert.Equal(5, result.TotalImages);
        Assert.Equal(2, result.WithEmbeddings);
        Assert.Equal(3, result.MissingEmbeddings);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(2, result.WrittenTagsCount);
    }

    [Fact]
    public async Task MatchCharactersAsync_PartialMatch_AcrossMultipleBatches()
    {
        var charVec = CreateVector(100);
        var otherVec = CreateVector(200); // Orthogonal or far
        _characterStore.Clear();
        _characterStore.Add("Asuka", charVec, 1);

        var folder = Path.Combine(_testRoot, "MultiBatchFolder");
        Directory.CreateDirectory(folder);

        // Create 25 images: 15 match Asuka, 10 do not. BatchSize = 10 -> will require 2 full batches + 1 flush
        int total = 25;
        int matching = 15;
        for (int i = 0; i < total; i++)
        {
            var path = CreateFakeImageFile("MultiBatchFolder", $"item_{i:D3}.jpg");
            var metaId = await _metaRepo.UpsertAsync(new ImageMeta
            {
                FilePath = path,
                FileHash = Guid.NewGuid().ToString("N"),
                FileSize = 100,
                LastWriteTicks = DateTime.UtcNow.Ticks
            });

            var vec = (i < matching) ? charVec : otherVec;
            await _embeddingRepo.UpsertAsync(metaId, Guid.NewGuid().ToString("N"), vec, "pixai", "v0.9");
        }

        var progressReports = new List<CharacterMatchProgress>();
        var progress = new Progress<CharacterMatchProgress>(p => progressReports.Add(p));

        var service = new CharacterMatchingService(_metaRepo, _embeddingRepo, _characterStore);
        var result = await service.MatchCharactersAsync(
            new CharacterMatchRequest(folder, MatchThreshold: 0.8, BatchSize: 10),
            progress);

        Assert.True(result.Completed);
        Assert.Equal(total, result.TotalImages);
        Assert.Equal(total, result.WithEmbeddings);
        Assert.Equal(0, result.MissingEmbeddings);
        Assert.Equal(matching, result.MatchedCount);
        Assert.Equal(matching, result.WrittenTagsCount);

        // Verify tags written in DB
        using (var conn = _dbFactory.CreateConnection())
        {
            var tags = (await conn.QueryAsync<(long ImageMetaId, string TagName)>(
                "SELECT it.ImageMetaId, t.Name as TagName FROM ImageTag it JOIN Tag t ON it.TagId = t.Id WHERE it.Source = 'CharacterEmbedding'")).ToList();
            Assert.Equal(matching, tags.Count);
            Assert.All(tags, t => Assert.Equal("Asuka", t.TagName));
        }
    }

    [Fact]
    public async Task MatchCharactersAsync_CancellationMidFlight_RetainsCommittedBatches()
    {
        var charVec = CreateVector(77);
        _characterStore.Clear();
        _characterStore.Add("Rei", charVec, 1);

        var folder = Path.Combine(_testRoot, "CancelMatchFolder");
        Directory.CreateDirectory(folder);

        // 30 matching images, batch size = 10.
        int total = 30;
        for (int i = 0; i < total; i++)
        {
            var path = CreateFakeImageFile("CancelMatchFolder", $"cancel_item_{i:D3}.jpg");
            var metaId = await _metaRepo.UpsertAsync(new ImageMeta
            {
                FilePath = path,
                FileHash = Guid.NewGuid().ToString("N"),
                FileSize = 100,
                LastWriteTicks = DateTime.UtcNow.Ticks
            });

            await _embeddingRepo.UpsertAsync(metaId, Guid.NewGuid().ToString("N"), charVec, "pixai", "v0.9");
        }

        using var cts = new CancellationTokenSource();
        var service = new CharacterMatchingService(_metaRepo, _embeddingRepo, _characterStore);

        // Cancel after 1st batch is committed
        var progress = new Progress<CharacterMatchProgress>(p =>
        {
            if (p.WrittenTagsCount >= 10)
            {
                cts.Cancel();
            }
        });

        var result = await service.MatchCharactersAsync(
            new CharacterMatchRequest(folder, MatchThreshold: 0.5, BatchSize: 10),
            progress,
            cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Completed);
        Assert.True(result.WrittenTagsCount >= 10);
        Assert.True(result.WrittenTagsCount < total);

        // Verify that the committed batch remains in DB (not rolled back)
        using (var conn = _dbFactory.CreateConnection())
        {
            var tags = (await conn.QueryAsync<(long ImageMetaId, string TagName)>(
                "SELECT it.ImageMetaId, t.Name as TagName FROM ImageTag it JOIN Tag t ON it.TagId = t.Id WHERE it.Source = 'CharacterEmbedding'")).ToList();
            Assert.Equal(result.WrittenTagsCount, tags.Count);
        }
    }

    #endregion

    #region Library Building Tests

    [Fact]
    public async Task BuildLibraryAsync_WhenModelNotLoaded_ReturnsFailedResult()
    {
        var mockExtractor = new TestEmbeddingExtractor { IsModelLoaded = false, CanLoadModel = false };
        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var request = new LibraryBuildRequest(Path.Combine(_testRoot, "NonExistentDir"), LibraryBuildTarget.Artist);
        var result = await service.BuildLibraryAsync(request);

        Assert.False(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Contains("模型未加载", result.ErrorMessage);
    }

    [Fact]
    public async Task BuildLibraryAsync_WhenModelInitiallyNotLoaded_AutoLoadsModelAndSucceeds()
    {
        var mockExtractor = new TestEmbeddingExtractor
        {
            IsModelLoaded = false,
            CanLoadModel = true,
            VectorGenerator = _ => CreateVector(321)
        };

        var refDir = Path.Combine(_testRoot, "AutoLoadRefDir");
        Directory.CreateDirectory(refDir);
        CreateFakeImageFile(Path.Combine("AutoLoadRefDir", "AutoArtist"), "a1.jpg");

        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var result = await service.BuildLibraryAsync(
            new LibraryBuildRequest(refDir, LibraryBuildTarget.Artist, BatchSize: 2));

        Assert.True(result.Completed);
        Assert.True(mockExtractor.IsModelLoaded);
        Assert.Equal(1, result.BuiltCount);
        Assert.Equal(1, _artistStore.Count);
    }

    [Fact]
    public async Task BuildLibraryAsync_SuccessfulBuild_AtomicallyReplacesTargetFileAndPopulatesStore()
    {
        var mockExtractor = new TestEmbeddingExtractor
        {
            IsModelLoaded = true,
            VectorGenerator = _ => CreateVector(123)
        };

        var refDir = Path.Combine(_testRoot, "ArtistRefDir");
        Directory.CreateDirectory(refDir);

        // Artist 1 with 3 images
        CreateFakeImageFile(Path.Combine("ArtistRefDir", "ArtistAlpha"), "a1.jpg");
        CreateFakeImageFile(Path.Combine("ArtistRefDir", "ArtistAlpha"), "a2.jpg");
        CreateFakeImageFile(Path.Combine("ArtistRefDir", "ArtistAlpha"), "a3.jpg");

        // Artist 2 with 2 images
        CreateFakeImageFile(Path.Combine("ArtistRefDir", "ArtistBeta"), "b1.jpg");
        CreateFakeImageFile(Path.Combine("ArtistRefDir", "ArtistBeta"), "b2.jpg");

        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var progressList = new List<LibraryBuildProgress>();
        var progress = new Progress<LibraryBuildProgress>(p => progressList.Add(p));

        var result = await service.BuildLibraryAsync(
            new LibraryBuildRequest(refDir, LibraryBuildTarget.Artist, BatchSize: 4),
            progress);

        Assert.True(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.BuiltCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(2, _artistStore.Count);
        Assert.Equal(3, _artistStore.GetImageCount("ArtistAlpha"));
        Assert.Equal(2, _artistStore.GetImageCount("ArtistBeta"));

        // Verify models directory files
        var modelsDir = Path.Combine(_cacheDir, "models");
        var targetFile = Path.Combine(modelsDir, "artist_embeddings.bin");
        Assert.True(File.Exists(targetFile));

        // Verify that temporary files were cleaned up
        var tmpFiles = Directory.GetFiles(modelsDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task BuildLibraryAsync_CancellationMidFlight_PreservesOriginalStoreAndDeletesTmp()
    {
        // 1. Pre-populate existing library with an artist
        var modelsDir = Path.Combine(_cacheDir, "models");
        Directory.CreateDirectory(modelsDir);
        var targetFile = Path.Combine(modelsDir, "character_embeddings.bin");

        var initialStore = new CharacterEmbeddingStore();
        initialStore.Add("ExistingCharacter", CreateVector(999), 5);
        initialStore.Save(targetFile);
        _characterStore.Load(targetFile);
        Assert.Equal(1, _characterStore.Count);

        // 2. Set up new reference dir with 5 new characters
        var refDir = Path.Combine(_testRoot, "CharRefDir");
        Directory.CreateDirectory(refDir);
        for (int i = 0; i < 5; i++)
        {
            CreateFakeImageFile(Path.Combine("CharRefDir", $"NewChar_{i}"), "ref.jpg");
        }

        using var cts = new CancellationTokenSource();
        var mockExtractor = new TestEmbeddingExtractor
        {
            IsModelLoaded = true,
            VectorGenerator = _ => CreateVector(555),
            OnEmbeddingExtracted = _ =>
            {
                // Cancel after first extraction
                cts.Cancel();
            }
        };

        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var result = await service.BuildLibraryAsync(
            new LibraryBuildRequest(refDir, LibraryBuildTarget.Character, BatchSize: 1),
            cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Completed);

        // Original store and file must remain untouched
        Assert.Equal(1, _characterStore.Count);
        Assert.Equal(5, _characterStore.GetImageCount("ExistingCharacter"));
        Assert.Equal(-1, _characterStore.GetImageCount("NewChar_0"));

        // Temporary files must be cleaned up
        var tmpFiles = Directory.GetFiles(modelsDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task BuildLibraryAsync_BatchInferenceFailure_FallsBackToSingleImageExtraction()
    {
        // Mock extractor throws on batch calls (simulating memory pressure / OOM)
        // but succeeds on single-image extraction
        var mockExtractor = new TestEmbeddingExtractor
        {
            IsModelLoaded = true,
            SimulateBatchMemoryPressure = true,
            VectorGenerator = _ => CreateVector(888)
        };

        var refDir = Path.Combine(_testRoot, "FallbackRefDir");
        Directory.CreateDirectory(refDir);
        CreateFakeImageFile(Path.Combine("FallbackRefDir", "CharFallback"), "f1.jpg");
        CreateFakeImageFile(Path.Combine("FallbackRefDir", "CharFallback"), "f2.jpg");

        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var result = await service.BuildLibraryAsync(
            new LibraryBuildRequest(refDir, LibraryBuildTarget.Character, BatchSize: 2));

        Assert.True(result.Completed);
        Assert.Equal(1, result.BuiltCount);
        Assert.Equal(1, _characterStore.Count);
        Assert.Equal(2, _characterStore.GetImageCount("CharFallback"));
        Assert.True(mockExtractor.SingleImageFallbacksCount > 0, "Single-image fallback should have been invoked");
    }

    [Fact]
    public async Task BuildLibraryAsync_CorruptImageSkipped_ContinuesRemaining()
    {
        var mockExtractor = new TestEmbeddingExtractor
        {
            IsModelLoaded = true,
            VectorGenerator = path =>
            {
                if (path.Contains("corrupt"))
                    throw new InvalidOperationException("Simulated corrupted image decode failure");
                return CreateVector(777);
            }
        };

        var refDir = Path.Combine(_testRoot, "CorruptRefDir");
        Directory.CreateDirectory(refDir);
        CreateFakeImageFile(Path.Combine("CorruptRefDir", "CharWithCorrupt"), "valid1.jpg");
        CreateFakeImageFile(Path.Combine("CorruptRefDir", "CharWithCorrupt"), "corrupt.jpg");
        CreateFakeImageFile(Path.Combine("CorruptRefDir", "CharWithCorrupt"), "valid2.jpg");

        var service = new LibraryBuilderService(
            mockExtractor, _artistStore, _characterStore, _chineseLib, _thumbCache);

        var result = await service.BuildLibraryAsync(
            new LibraryBuildRequest(refDir, LibraryBuildTarget.Character, BatchSize: 1));

        Assert.True(result.Completed);
        Assert.Equal(1, result.BuiltCount);
        Assert.Equal(1, _characterStore.Count);
        // Valid 2 images were used
        Assert.Equal(2, _characterStore.GetImageCount("CharWithCorrupt"));
    }

    #endregion

    #region Helper Test Classes

    private sealed class TestThumbnailCacheService : IThumbnailCacheService
    {
        public string CacheDirectory { get; set; }
        public long EstimatedMemoryBytes => 0;

        public TestThumbnailCacheService(string cacheDir)
        {
            CacheDirectory = cacheDir;
        }

        public Task<(byte[]? Data, int Width, int Height)> GetOrCreateThumbnailAsync(string filePath, int decodeWidth, CancellationToken ct = default)
            => Task.FromResult<(byte[]?, int, int)>((null, 0, 0));

        public Task ClearAsync() => Task.CompletedTask;
        public void Trim(long maxBytes, string? protectedKey = null) { }
        public void MoveDiskCache(string oldPath, string newPath) { }
        public void InvalidateThumbnail(string filePath) { }
        public (int Width, int Height) GetDimensions(string filePath) => (100, 100);
        public (int Width, int Height) GetDimensionsOrUnknown(string filePath) => (100, 100);
        public byte[]? DecodeForHashInput(string filePath, int maxSize = 256) => null;
        public Task<Dictionary<string, (int Width, int Height)>> GetCachedDimensionsAsync(IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, (int Width, int Height)>());
    }

    private sealed class TestEmbeddingExtractor : IImageEmbeddingExtractor
    {
        public bool IsModelLoaded { get; set; } = true;
        public bool CanLoadModel { get; set; } = true;
        public bool SimulateBatchMemoryPressure { get; set; } = false;
        public int SingleImageFallbacksCount { get; private set; }
        public Func<string, float[]>? VectorGenerator { get; set; }
        public Action<string>? OnEmbeddingExtracted { get; set; }

        public Task<float[]?> GetEmbeddingAsync(string imagePath)
        {
            SingleImageFallbacksCount++;
            OnEmbeddingExtracted?.Invoke(imagePath);
            var vec = VectorGenerator?.Invoke(imagePath) ?? new float[1024];
            return Task.FromResult<float[]?>(vec);
        }

        public Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> imagePaths)
        {
            if (SimulateBatchMemoryPressure)
            {
                throw new OutOfMemoryException("Simulated BFCArena available memory pressure");
            }

            var list = new List<float[]>();
            foreach (var path in imagePaths)
            {
                OnEmbeddingExtracted?.Invoke(path);
                list.Add(VectorGenerator?.Invoke(path) ?? new float[1024]);
            }
            return Task.FromResult(list);
        }

        public Task LoadModelAsync(string modelPath, CancellationToken ct = default)
        {
            if (CanLoadModel)
            {
                IsModelLoaded = true;
            }
            return Task.CompletedTask;
        }
    }

    #endregion
}
