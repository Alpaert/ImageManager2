using ImageManager.Core.Models;
using ImageManager.Infrastructure.Services;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class FolderDetailsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ImageManager-folder-details-" + Guid.NewGuid().ToString("N"));
    public FolderDetailsServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CountsPhysicalFilesAndSubfoldersSeparatelyFromMedia()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "one.jpg"), new byte[12]);
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "hello");
        var child = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(child, "two.mp4"), new byte[20]);
        using var service = new FolderDetailsService();
        var result = await service.GetAsync(_root);
        Assert.Equal(FolderStatisticsState.Complete, result.State);
        Assert.Equal(2, result.DirectFileCount);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(2, result.MediaCount);
        Assert.Equal(1, result.DirectoryCount);
        Assert.Equal(37, result.TotalBytes);
    }

    [Fact]
    public async Task InvalidatingAChildInvalidatesItsAncestorsButNotSiblingPrefixes()
    {
        using var service = new FolderDetailsService();
        var file = Path.Combine(_root, "a.txt");
        var first = await service.GetAsync(_root);
        await File.WriteAllTextAsync(file, "abc");
        Assert.Same(first, await service.GetAsync(_root));
        service.Invalidate(_root + "-other\\file.txt");
        Assert.Same(first, await service.GetAsync(_root));
        service.Invalidate(file);
        Assert.Equal(3, (await service.GetAsync(_root)).TotalBytes);
    }

    [Fact]
    public async Task MissingFolderIsNotReportedAsEmptyAndCanceledReadsDoNotPublish()
    {
        using var service = new FolderDetailsService();
        Assert.Equal(FolderStatisticsState.Unavailable, (await service.GetAsync(Path.Combine(_root, "missing"))).State);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(_root, cts.Token));
        Assert.Equal(FolderStatisticsState.Complete, (await service.GetAsync(_root)).State);
    }

    [Fact]
    public async Task ProvisionalProgressCanCancelWithoutCachingAnIncompleteCount()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "abc");
        using var service = new FolderDetailsService();
        using var cts = new CancellationTokenSource();
        var reports = new List<FolderDetailsSnapshot>();
        var progress = new InlineProgress(snapshot =>
        {
            reports.Add(snapshot);
            cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(_root, progress, cts.Token));
        Assert.Single(reports);
        Assert.Equal(FolderStatisticsState.Scanning, reports[0].State);
        var complete = await service.GetAsync(_root);
        Assert.Equal(FolderStatisticsState.Complete, complete.State);
        Assert.Equal(1, complete.FileCount);
    }

    private sealed class InlineProgress(Action<FolderDetailsSnapshot> report) : IProgress<FolderDetailsSnapshot>
    {
        public void Report(FolderDetailsSnapshot value) => report(value);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
