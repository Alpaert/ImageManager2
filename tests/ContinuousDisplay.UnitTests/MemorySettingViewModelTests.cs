using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class MemorySettingViewModelTests
{
    [Fact]
    public async Task CacheUsageScan_DoesNotRunDuringConstruction_AndRunsAsynchronously()
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowScanToFinish = new ManualResetEventSlim();
        var viewModel = new MemorySettingViewModel(
            "cache",
            string.Empty,
            tagMode: 1,
            ensembleMaxTags: 75,
            pixaiMinConfidence: 0.3,
            artistMatchThreshold: 0.35,
            enableCharacterRecognition: true,
            characterMatchThreshold: 0.35,
            characterMaxMatches: 1,
            singleModelMinConfidence: 0.15,
            getDiskUsage: _ =>
            {
                scanStarted.TrySetResult();
                allowScanToFinish.Wait();
                return 2 * 1024 * 1024;
            },
            onSave: null);

        Assert.False(scanStarted.Task.IsCompleted);

        var refreshTask = viewModel.RefreshDiskUsageHintAsync();
        try
        {
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refreshTask.IsCompleted);
            Assert.Equal("正在统计缓存占用…", viewModel.DiskUsageHint);
        }
        finally
        {
            allowScanToFinish.Set();
        }

        await refreshTask;
        Assert.Equal("磁盘缓存占用约 2.0 MB", viewModel.DiskUsageHint);
    }
}
