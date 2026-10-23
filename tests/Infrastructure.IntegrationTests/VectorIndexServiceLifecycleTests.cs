using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class VectorIndexServiceLifecycleTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly HashDataVersionTracker _versionTracker;
    private readonly ImageMetaRepository _metaRepo;
    private readonly ImageEmbeddingRepository _embeddingRepo;
    private readonly ChineseClipService _chineseClip;
    private readonly SimilarImageService _similarService;
    private readonly VectorIndexService _indexService;

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class ThrowingSimilarImageService : ISimilarImageService
    {
        public int InvalidateCallCount { get; private set; }
        public bool ShouldThrow { get; set; } = true;

        public void InvalidateVectorSnapshots()
        {
            InvalidateCallCount++;
            if (ShouldThrow)
                throw new ApplicationException("Simulated error inside InvalidateVectorSnapshots");
        }

        public void InvalidatePerceptualSnapshot() { }
        public Task<List<SimilaritySearchResult>> SearchByImageAsync(string baseFilePath, IEnumerable<string> candidates, SimilaritySearchMode mode, int limit = 50, CancellationToken ct = default)
            => Task.FromResult(new List<SimilaritySearchResult>());
        public Task<List<SimilaritySearchResult>> SearchByTextAsync(string query, IEnumerable<string> candidates, int limit = 50, CancellationToken ct = default)
            => Task.FromResult(new List<SimilaritySearchResult>());
    }

    public VectorIndexServiceLifecycleTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-VectorLifecycleTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, _versionTracker);
        var suppressionRepo = new CharacterTagSuppressionRepository(_dbFactory);
        _embeddingRepo = new ImageEmbeddingRepository(_dbFactory, suppressionRepo);
        _chineseClip = new ChineseClipService(Path.Combine(_testRoot, "clip"));
        _similarService = new SimilarImageService(_metaRepo, _embeddingRepo, _chineseClip, _versionTracker);
        _indexService = new VectorIndexService(_embeddingRepo, _metaRepo, _chineseClip, _similarService);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _similarService.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
        return Task.CompletedTask;
    }

    private string CreateTestImage(string name)
    {
        var path = Path.Combine(_testRoot, name);
        using var bitmap = new SkiaSharp.SKBitmap(64, 64);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.MediumSeaGreen);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        encoded.SaveTo(stream);
        return path;
    }

    [Fact]
    public async Task BuildAsync_DoubleStartConcurrently_ThrowsInvalidOperationException()
    {
        var imgPath = CreateTestImage("img1.png");
        await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = imgPath,
            FileHash = "h1",
            PerceptualHash = new string('0', 64),
            Width = 64,
            Height = 64,
            FileSize = 100
        });

        using var pauseGate = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var progress = new SyncProgress<VectorIndexProgress>(p =>
        {
            firstStarted.TrySetResult(true);
            pauseGate.Wait(TimeSpan.FromSeconds(5));
        });

        var firstTask = Task.Run(() => _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All, progress));
        try
        {
            Assert.True(await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(_indexService.IsRunning);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All));

            Assert.Contains("已有向量索引任务正在运行", ex.Message);
        }
        finally
        {
            pauseGate.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(_indexService.IsRunning);
    }

    [Fact]
    public async Task RepairHashesAsync_DoubleStartConcurrently_ThrowsInvalidOperationException()
    {
        CreateTestImage("hash1.png");

        using var pauseGate = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var progress = new SyncProgress<HashRepairProgress>(p =>
        {
            firstStarted.TrySetResult(true);
            pauseGate.Wait(TimeSpan.FromSeconds(5));
        });

        var scope = new VectorIndexScope(_testRoot, true);

        var firstTask = Task.Run(() => _indexService.RepairHashesAsync(scope, progress));
        try
        {
            Assert.True(await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(_indexService.IsRunning);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _indexService.RepairHashesAsync(scope));

            Assert.Contains("已有向量索引或图片指纹任务正在运行", ex.Message);
        }
        finally
        {
            pauseGate.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(_indexService.IsRunning);
    }

    [Fact]
    public async Task BuildAsync_WhileRepairHashesRunning_ThrowsInvalidOperationException()
    {
        CreateTestImage("cross1.png");
        using var pauseGate = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var progress = new SyncProgress<HashRepairProgress>(p =>
        {
            firstStarted.TrySetResult(true);
            pauseGate.Wait(TimeSpan.FromSeconds(5));
        });

        var scope = new VectorIndexScope(_testRoot, true);
        var firstTask = Task.Run(() => _indexService.RepairHashesAsync(scope, progress));
        try
        {
            Assert.True(await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All));
            Assert.Contains("已有向量索引任务正在运行", ex.Message);
        }
        finally
        {
            pauseGate.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(_indexService.IsRunning);
    }

    [Fact]
    public async Task RepairHashesAsync_WhileBuildRunning_ThrowsInvalidOperationException()
    {
        var img = CreateTestImage("cross2.png");
        await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = img,
            FileHash = "cross_h",
            PerceptualHash = new string('0', 64),
            Width = 64,
            Height = 64,
            FileSize = 100
        });

        using var pauseGate = new ManualResetEventSlim(false);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var progress = new SyncProgress<VectorIndexProgress>(p =>
        {
            firstStarted.TrySetResult(true);
            pauseGate.Wait(TimeSpan.FromSeconds(5));
        });

        var firstTask = Task.Run(() => _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All, progress));
        try
        {
            Assert.True(await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _indexService.RepairHashesAsync(new VectorIndexScope(_testRoot, true)));
            Assert.Contains("已有向量索引或图片指纹任务正在运行", ex.Message);
        }
        finally
        {
            pauseGate.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(_indexService.IsRunning);
    }

    [Fact]
    public async Task BuildAsync_AtmosphereKind_BuildsSuccessfully()
    {
        var imgPath = CreateTestImage("atmo_img.png");
        await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = imgPath,
            FileHash = "h_atmo",
            PerceptualHash = new string('0', 64),
            Width = 64,
            Height = 64,
            FileSize = 100
        });

        await _indexService.BuildAsync(VectorIndexKind.Atmosphere, rebuild: true, VectorIndexScope.All);
        Assert.False(_indexService.IsRunning);

        var status = await _indexService.GetStatusAsync(VectorIndexKind.Atmosphere, VectorIndexScope.All);
        Assert.Equal(1, status.IndexedImages);
    }

    [Fact]
    public async Task BuildAsync_Cancellation_DoesNotFlushPartialBatch()
    {
        // 2 images (< 64 batch size)
        var img1 = CreateTestImage("c1.png");
        var img2 = CreateTestImage("c2.png");
        await _metaRepo.UpsertAsync(new ImageMeta { FilePath = img1, FileHash = "c1", PerceptualHash = "0", Width = 64, Height = 64, FileSize = 10 });
        await _metaRepo.UpsertAsync(new ImageMeta { FilePath = img2, FileHash = "c2", PerceptualHash = "0", Width = 64, Height = 64, FileSize = 10 });

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<VectorIndexProgress>(p =>
        {
            if (p.Processed >= 1)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All, progress, cts.Token));

        Assert.False(_indexService.IsRunning);

        // Verify that unflushed partial batch was discarded on cancel (not committed)
        var status = await _indexService.GetStatusAsync(VectorIndexKind.Color, VectorIndexScope.All);
        Assert.Equal(0, status.IndexedImages);
    }

    [Fact]
    public async Task RepairHashesAsync_Cancellation_FlushesRemainingBatch()
    {
        var img1 = CreateTestImage("rh1.png");
        var img2 = CreateTestImage("rh2.png");

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<HashRepairProgress>(p =>
        {
            if (p.Processed >= 1)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _indexService.RepairHashesAsync(new VectorIndexScope(_testRoot, true), progress, cts.Token));

        Assert.False(_indexService.IsRunning);

        // Verify that RepairHashes flushes its batch on cancellation
        var meta1 = await _metaRepo.GetByPathAsync(img1);
        var meta2 = await _metaRepo.GetByPathAsync(img2);
        Assert.True(meta1 != null || meta2 != null);
    }

    [Fact]
    public async Task BuildAsync_WhenInvalidateVectorSnapshotsThrows_StillReleasesRunningAndAllowsNextRun()
    {
        var imgPath = CreateTestImage("fault_img.png");
        await _metaRepo.UpsertAsync(new ImageMeta
        {
            FilePath = imgPath,
            FileHash = "h_fault",
            PerceptualHash = new string('0', 64),
            Width = 64,
            Height = 64,
            FileSize = 100
        });

        var throwingSimilar = new ThrowingSimilarImageService { ShouldThrow = true };
        var serviceWithFault = new VectorIndexService(_embeddingRepo, _metaRepo, _chineseClip, throwingSimilar);

        // Build runs, InvalidateVectorSnapshots throws inside finally, but service catches and resets _running
        await serviceWithFault.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All);

        Assert.True(throwingSimilar.InvalidateCallCount > 0);
        Assert.False(serviceWithFault.IsRunning);

        // Next build must succeed without "已有向量索引任务正在运行"
        throwingSimilar.ShouldThrow = false;
        await serviceWithFault.BuildAsync(VectorIndexKind.Color, rebuild: false, VectorIndexScope.All);
        Assert.False(serviceWithFault.IsRunning);
    }

    [Fact]
    public async Task BuildAsync_SemanticValidationFailure_ReleasesRunningFlag()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _indexService.BuildAsync(VectorIndexKind.Semantic, rebuild: false, VectorIndexScope.All));

        Assert.False(_indexService.IsRunning);

        await _indexService.BuildAsync(VectorIndexKind.Color, rebuild: false, VectorIndexScope.All);
        Assert.False(_indexService.IsRunning);
    }

    [Fact]
    public async Task PauseAndResume_PausesExecutionUntilResumed()
    {
        for (int i = 0; i < 5; i++)
        {
            var imgPath = CreateTestImage($"pause_img_{i}.png");
            await _metaRepo.UpsertAsync(new ImageMeta
            {
                FilePath = imgPath,
                FileHash = $"h_pause_{i}",
                PerceptualHash = new string('0', 64),
                Width = 64,
                Height = 64,
                FileSize = 100
            });
        }

        var pausedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress<VectorIndexProgress>(p =>
        {
            if (p.Processed >= 1 && !pausedTcs.Task.IsCompleted)
            {
                _indexService.Pause();
                pausedTcs.TrySetResult(true);
            }
        });

        using var cts = new CancellationTokenSource();
        var buildTask = Task.Run(() => _indexService.BuildAsync(VectorIndexKind.Color, rebuild: true, VectorIndexScope.All, progress, cts.Token));

        try
        {
            Assert.True(await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            await Task.Delay(100);
            Assert.False(buildTask.IsCompleted);

            _indexService.Resume();
            await buildTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(_indexService.IsRunning);
        }
        finally
        {
            _indexService.Resume();
            cts.Cancel();
            try
            {
                await buildTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }
}
