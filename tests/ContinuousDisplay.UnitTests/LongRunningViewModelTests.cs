using System;
using System.Threading;
using System.Threading.Tasks;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class LongRunningViewModelTests
{
    private sealed class MockLibraryBuilderService : ILibraryBuilderService
    {
        public bool ShouldDelay { get; set; }
        public int TotalItemsToReport { get; set; } = 10;
        public Action? OnBuildStarted { get; set; }

        public async Task<LibraryBuildResult> BuildLibraryAsync(
            LibraryBuildRequest request,
            IProgress<LibraryBuildProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            OnBuildStarted?.Invoke();
            for (int i = 1; i <= TotalItemsToReport; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new LibraryBuildResult(
                        TotalItems: TotalItemsToReport,
                        SkippedCount: 0,
                        BuiltCount: i - 1,
                        TotalInStore: i - 1,
                        Completed: false,
                        Cancelled: true);
                }

                progress?.Report(new LibraryBuildProgress(
                    CurrentIndex: i,
                    TotalCount: TotalItemsToReport,
                    SkippedCount: 0,
                    BuiltCount: i,
                    FailedCount: 0,
                    CurrentItemName: $"Item_{i}",
                    Stage: "处理中"));

                if (ShouldDelay)
                {
                    try
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return new LibraryBuildResult(
                            TotalItems: TotalItemsToReport,
                            SkippedCount: 0,
                            BuiltCount: i - 1,
                            TotalInStore: i - 1,
                            Completed: false,
                            Cancelled: true);
                    }
                }
            }

            return new LibraryBuildResult(
                TotalItems: TotalItemsToReport,
                SkippedCount: 0,
                BuiltCount: TotalItemsToReport,
                TotalInStore: TotalItemsToReport,
                Completed: true,
                Cancelled: false);
        }
    }

    [Fact]
    public void TargetChanged_SwitchesTitlesAndDescriptions()
    {
        var vm = new ArtistDbBuilderViewModel();
        Assert.Equal(LibraryBuildTarget.Artist, vm.Target);
        Assert.Contains("画师", vm.ReferenceDescription);

        vm.Target = LibraryBuildTarget.Character;
        Assert.Equal(LibraryBuildTarget.Character, vm.Target);
        Assert.Contains("角色", vm.ReferenceDescription);
        Assert.Equal("当前角色库", vm.LibraryTitle);

        vm.Target = LibraryBuildTarget.Artist;
        Assert.Contains("画师", vm.ReferenceDescription);
        Assert.Equal("当前画师库", vm.LibraryTitle);
    }

    [Fact]
    public async Task BuildAsync_ReportsProgressCorrectly()
    {
        var mock = new MockLibraryBuilderService { TotalItemsToReport = 5 };
        var vm = new ArtistDbBuilderViewModel(mock)
        {
            ReferenceDir = "C:\\dummy\\path"
        };

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Equal(5, vm.Progress);
        Assert.Equal(5, vm.Total);
        Assert.Contains("完成", vm.StatusText);
    }

    [Fact]
    public async Task Cancel_CancelsRunningBuildAndUpdatesStatus()
    {
        var buildStarted = new TaskCompletionSource<bool>();
        var mock = new MockLibraryBuilderService
        {
            ShouldDelay = true,
            TotalItemsToReport = 50,
            OnBuildStarted = () => buildStarted.TrySetResult(true)
        };

        var vm = new ArtistDbBuilderViewModel(mock)
        {
            ReferenceDir = "C:\\dummy\\path"
        };

        var buildTask = vm.BuildCommand.ExecuteAsync(null);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel while running
        vm.Cancel();
        await buildTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsRunning);
        Assert.Contains("取消", vm.StatusText);
    }
}
