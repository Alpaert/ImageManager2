using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowNavigationViewModelTests
{
    [Fact]
    public async Task LoadFolderAsync_ValidFolder_PopulatesImagesAndStatus()
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(imageCount: 3);

        await context.ViewModel.LoadFolderAsync(folder);

        Assert.Equal(folder, context.ViewModel.CurrentFolder);
        Assert.NotEmpty(context.ViewModel.Images);
        Assert.Equal(3, context.ViewModel.Images.Count);
        Assert.True(context.ViewModel.TotalPages >= 1);
        Assert.Contains("3", context.ViewModel.StatusText);
    }

    [Fact]
    public async Task LoadFolderAsync_NonExistentFolder_SetsStatusAndDoesNotLeaveOldImages()
    {
        using var context = new MainWindowTestContext();
        var validFolder = context.CreateTempFolderWithImages(imageCount: 2);
        await context.ViewModel.LoadFolderAsync(validFolder);

        Assert.NotEmpty(context.ViewModel.Images);
        Assert.Equal(validFolder, context.ViewModel.CurrentFolder);
        Assert.Equal(2, context.ViewModel.Images.Count);

        var nonExistent = Path.Combine(Path.GetTempPath(), "IM2-NonExistent-" + Guid.NewGuid().ToString("N"));
        await context.ViewModel.LoadFolderAsync(nonExistent);

        Assert.Equal("文件夹不存在", context.ViewModel.StatusText);
        // Preserves previous view state when target does not exist
        Assert.Equal(validFolder, context.ViewModel.CurrentFolder);
        Assert.Equal(2, context.ViewModel.Images.Count);
        Assert.Equal(1, context.ViewModel.TotalPages);

        // Continuous item source is invalidated and discarded
        Assert.Null(context.ViewModel.ContinuousImageItemSource);

        // When navigating to an empty folder, existing images are cleared completely
        var emptyFolder = context.CreateEmptyTempFolder();
        await context.ViewModel.LoadFolderAsync(emptyFolder);

        Assert.Equal("该文件夹内没有媒体文件", context.ViewModel.StatusText);
        Assert.Empty(context.ViewModel.Images);
        Assert.Equal(0, context.ViewModel.TotalPages);
    }

    [Fact]
    public async Task LoadFolderAsync_EmptyFolder_SetsEmptyStatus()
    {
        using var context = new MainWindowTestContext();
        var emptyFolder = context.CreateEmptyTempFolder();

        await context.ViewModel.LoadFolderAsync(emptyFolder);

        Assert.Equal("该文件夹内没有媒体文件", context.ViewModel.StatusText);
        Assert.Empty(context.ViewModel.Images);
        Assert.Equal(0, context.ViewModel.TotalPages);
    }

    [Fact]
    public async Task LoadFolderAsync_RaceCondition_LatestRequestWins()
    {
        using var context = new MainWindowTestContext();
        var folderA = context.CreateTempFolderWithImages(imageCount: 2, prefix: "A_");
        var folderB = context.CreateTempFolderWithImages(imageCount: 4, prefix: "B_");

        var folderAEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var folderAGate = new TaskCompletionSource<FolderInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        context.FolderRepo.GetByPathFunc = path =>
        {
            if (string.Equals(path, folderA, StringComparison.OrdinalIgnoreCase))
            {
                folderAEntered.TrySetResult(true);
                return folderAGate.Task;
            }

            return Task.FromResult<FolderInfo?>(new FolderInfo
            {
                Id = 2,
                Path = path
            });
        };

        Task? taskA = null;
        Task? taskB = null;
        try
        {
            // Start LoadFolderAsync for Folder A
            taskA = context.ViewModel.LoadFolderAsync(folderA);

            // Wait until Folder A has definitely entered its database query and paused
            Assert.True(await folderAEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // Start LoadFolderAsync for Folder B (which completes right away)
            taskB = context.ViewModel.LoadFolderAsync(folderB);
            await taskB.WaitAsync(TimeSpan.FromSeconds(5));

            // UI has finished displaying Folder B
            Assert.Equal(folderB, context.ViewModel.CurrentFolder);
            Assert.Equal(4, context.ViewModel.Images.Count);
            Assert.All(context.ViewModel.Images, item => Assert.StartsWith(folderB, item.FilePath, StringComparison.OrdinalIgnoreCase));

            // Now release Folder A's delayed database query
            folderAGate.TrySetResult(new FolderInfo
            {
                Id = 1,
                Path = folderA
            });
            try { await taskA.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }

            // Delayed Folder A completion MUST NOT overwrite Folder B!
            Assert.Equal(folderB, context.ViewModel.CurrentFolder);
            Assert.Equal(4, context.ViewModel.Images.Count);
            Assert.All(context.ViewModel.Images, item => Assert.StartsWith(folderB, item.FilePath, StringComparison.OrdinalIgnoreCase));
            Assert.False(context.ViewModel.IsDisplayFilterBusy);
        }
        finally
        {
            folderAGate.TrySetCanceled();
            if (taskA != null) { try { await taskA.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (taskB != null) { try { await taskB.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        }
    }

    [Fact]
    public async Task LoadFolderAsync_WhenCancelled_ResetsLoadingState()
    {
        using var context = new MainWindowTestContext();
        var folderA = context.CreateTempFolderWithImages(imageCount: 2, prefix: "CancelA_");
        var folderB = context.CreateTempFolderWithImages(imageCount: 3, prefix: "CancelB_");

        var folderAEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var folderAGate = new TaskCompletionSource<FolderInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FolderRepo.GetByPathFunc = path =>
        {
            if (string.Equals(path, folderA, StringComparison.OrdinalIgnoreCase))
            {
                folderAEntered.TrySetResult(true);
                return folderAGate.Task;
            }

            return Task.FromResult<FolderInfo?>(null);
        };

        Task? loadATask = null;
        Task? loadBTask = null;
        try
        {
            loadATask = context.ViewModel.LoadFolderAsync(folderA);
            Assert.True(await folderAEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // Cancel folder A by switching folders to folder B
            loadBTask = context.ViewModel.LoadFolderAsync(folderB);
            folderAGate.TrySetCanceled();

            await loadBTask.WaitAsync(TimeSpan.FromSeconds(5));
            try { await loadATask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }

            // Navigation state successfully recovered and displays Folder B
            Assert.Equal(folderB, context.ViewModel.CurrentFolder);
            Assert.Equal(3, context.ViewModel.Images.Count);
            Assert.False(context.ViewModel.IsDisplayFilterBusy);
        }
        finally
        {
            folderAGate.TrySetCanceled();
            if (loadATask != null) { try { await loadATask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (loadBTask != null) { try { await loadBTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        }
    }

    [Fact]
    public async Task LoadFolderAsync_WhenRepositoryThrows_DisplaysErrorAndRecoversOnNextNavigation()
    {
        using var context = new MainWindowTestContext();
        var folderFail = context.CreateTempFolderWithImages(imageCount: 2, prefix: "Fail_");
        var folderOk = context.CreateTempFolderWithImages(imageCount: 3, prefix: "Ok_");

        // Simulate repository failure during navigation query
        context.FolderRepo.ThrowOnGetByPath = new IOException("Database connection failure");

        var thrown = await Record.ExceptionAsync(() => context.ViewModel.LoadFolderAsync(folderFail));
        Assert.NotNull(thrown);

        // Recover on next navigation
        context.FolderRepo.ThrowOnGetByPath = null;
        await context.ViewModel.LoadFolderAsync(folderOk);

        Assert.Equal(folderOk, context.ViewModel.CurrentFolder);
        Assert.Equal(3, context.ViewModel.Images.Count);
        Assert.Contains("3", context.ViewModel.StatusText);
    }
}
