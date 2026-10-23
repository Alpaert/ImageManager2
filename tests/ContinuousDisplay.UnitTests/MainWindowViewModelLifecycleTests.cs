using ImageManager.App.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowViewModelLifecycleTests
{
    [Fact]
    public async Task ShutdownBackgroundWork_WithActiveTasksAndWatcher_CancelsTokensExitsTasksAndStopsWatching()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.DuplicateService.OnDetect = async (files, dir, ct) =>
        {
            startedTcs.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return (1, 1);
        };

        var detectTask = ctx.ViewModel.DetectDuplicatesAsync(@"C:\some\target");
        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var folderWorkToken = ctx.ViewModel.CurrentFolderWorkToken;

        // Act 1: Shutdown background work while tasks and watcher are active
        ctx.ViewModel.ShutdownBackgroundWork();

        // Assert 1: Background tokens are cancelled and active task exits
        Assert.True(folderWorkToken.IsCancellationRequested);
        await detectTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("查重已取消", ctx.ViewModel.StatusText);

        // Act 2: Second call ensures idempotence (no ObjectDisposedException or crash)
        ctx.ViewModel.ShutdownBackgroundWork();
    }

    [Fact]
    public async Task CancelActiveBackgroundTasks_CancelsRunningOperations_AndRecoversOnSubsequentOperation()
    {
        using var ctx = new MainWindowTestContext();
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.DuplicateService.OnDetect = async (files, dir, ct) =>
        {
            startedTcs.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return (1, 1);
        };

        // Act 1: Start detect duplicates in background
        var detectTask = ctx.ViewModel.DetectDuplicatesAsync(@"C:\some\target");

        // Wait until it enters OnDetect
        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel while it's running
        ctx.ViewModel.CancelActiveBackgroundTasks();
        await detectTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: Status was set to cancelled
        Assert.Equal("查重已取消", ctx.ViewModel.StatusText);

        // Act 2: Subsequent operation recovery - execute normal operation cleanly
        ctx.DuplicateService.OnDetect = (files, dir, ct) => Task.FromResult((2, 1));
        await ctx.ViewModel.DetectDuplicatesAsync(@"C:\recovery\target").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("查重完成: 精确 2 张, 模糊 1 张", ctx.ViewModel.StatusText);
    }

    [Fact]
    public async Task SelectionState_SynchronizesWithRealizedImageViewItems()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // Initially no items selected
        Assert.False(ctx.ViewModel.IsFileSelected(file1));
        Assert.False(ctx.ViewModel.IsFileSelected(file2));
        Assert.False(ctx.ViewModel.Images[0].IsSelected);
        Assert.False(ctx.ViewModel.Images[1].IsSelected);

        // 1. Select file1
        ctx.ViewModel.SetFileSelected(file1, true);
        Assert.True(ctx.ViewModel.IsFileSelected(file1));
        Assert.True(ctx.ViewModel.Images[0].IsSelected);
        Assert.False(ctx.ViewModel.Images[1].IsSelected);

        // 2. Replace selection with file2
        ctx.ViewModel.ReplaceSelectedFiles(new[] { file2 });
        Assert.False(ctx.ViewModel.IsFileSelected(file1));
        Assert.True(ctx.ViewModel.IsFileSelected(file2));
        Assert.False(ctx.ViewModel.Images[0].IsSelected);
        Assert.True(ctx.ViewModel.Images[1].IsSelected);

        var selectedItems = ctx.ViewModel.GetSelectedRealizedItems();
        Assert.Single(selectedItems);
        Assert.Equal(file2, selectedItems[0].FilePath);

        // 3. Deselect file2
        ctx.ViewModel.SetFileSelected(file2, false);
        Assert.Empty(ctx.ViewModel.SelectedFilePaths);
        Assert.False(ctx.ViewModel.Images[1].IsSelected);
    }

    [Fact]
    public async Task SyncUISettingsFromAppData_RestoresDisplayAndSortMode()
    {
        using var ctx = new MainWindowTestContext();

        ctx.SettingsRepo.Settings.WaterfallMode = "Vertical";
        ctx.SettingsRepo.Settings.VerticalZoomLevel = 4.5;
        ctx.SettingsRepo.Settings.SearchToolbarLayout = "C";

        // Act: InitializeAsync loads from repository and invokes SyncUISettingsFromAppData
        await ctx.ViewModel.InitializeAsync();

        // Assert
        Assert.Equal("Vertical", ctx.ViewModel.WaterfallMode);
        Assert.Equal(4.5, ctx.ViewModel.ZoomTick);
        Assert.Equal("C", ctx.ViewModel.SearchToolbarLayout);
        Assert.True(ctx.ViewModel.IsSearchToolbarLayoutC);

        // Switch to Horizontal
        ctx.ViewModel.SwitchWaterfallMode("Horizontal");
        Assert.Equal("Horizontal", ctx.ViewModel.WaterfallMode);

        // Set SearchToolbarLayout back to A
        ctx.ViewModel.SetSearchToolbarLayout("A");
        Assert.Equal("A", ctx.ViewModel.SearchToolbarLayout);
        Assert.True(ctx.ViewModel.IsSearchToolbarLayoutA);
    }
}
