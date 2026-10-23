using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class SettingsAndArchivedFoldersViewModelTests
{
    [Fact]
    public async Task ArchivedFoldersViewModel_InitializeAsync_LoadsArchivedFoldersOnly()
    {
        var repo = new FakeFolderRepository();
        await repo.AddAsync(@"C:\folder1");
        await repo.AddAsync(@"C:\folder2");
        await repo.AddAsync(@"C:\folder3");

        var f1 = await repo.GetByPathAsync(@"C:\folder1");
        var f2 = await repo.GetByPathAsync(@"C:\folder2");
        var f3 = await repo.GetByPathAsync(@"C:\folder3");

        await repo.SetArchivedAsync(f2!.Id, true);
        await repo.SetArchivedAsync(f3!.Id, true);

        var vm = new ArchivedFoldersViewModel(repo);

        // Act
        await vm.InitializeAsync();

        // Assert: Only 2 archived folders loaded
        Assert.Equal(2, vm.ArchivedFolders.Count);
        Assert.Contains(vm.ArchivedFolders, f => f.Path == @"C:\folder2");
        Assert.Contains(vm.ArchivedFolders, f => f.Path == @"C:\folder3");
        Assert.False(vm.HasNoArchivedFolders);
        Assert.Equal("共 2 个已归档文件夹", vm.StatusText);

        // Act 2: Restore folder2
        await vm.RestoreCommand.ExecuteAsync(f2);
        Assert.Single(vm.ArchivedFolders);
        Assert.False(f2.IsArchived);
        Assert.Equal("共 1 个已归档文件夹", vm.StatusText);

        // Act 3: Restore folder3
        await vm.RestoreCommand.ExecuteAsync(f3);
        Assert.Empty(vm.ArchivedFolders);
        Assert.False(f3.IsArchived);
        Assert.True(vm.HasNoArchivedFolders);
        Assert.Equal("暂无已归档文件夹", vm.StatusText);
    }

    [Fact]
    public async Task ArchivedFoldersViewModel_InitializeAsync_HandlesExceptionsGracefully()
    {
        var repo = new FakeFolderRepository
        {
            ThrowOnGetByPath = new InvalidOperationException("DB offline")
        };

        // Make GetAllAsync throw
        var throwingRepo = new ThrowingFolderRepository();
        var vm = new ArchivedFoldersViewModel(throwingRepo);

        await vm.InitializeAsync();

        Assert.Empty(vm.ArchivedFolders);
        Assert.Contains("加载失败", vm.StatusText);
        Assert.Contains("DB error", vm.StatusText);
    }

    [Fact]
    public void ThumbnailSettingViewModel_LoadsSettingsAndSavesCorrectly()
    {
        var settings = new AppSettings
        {
            ShowThumbnailFileName = true,
            ShowThumbnailTags = false,
            WaterfallMode = "Vertical",
            ThumbnailBorderColor = "#FF0000",
            ThumbnailOpacity = 0.8,
            ImagesPerPage = 150
        };

        bool changed = false;
        var vm = new ThumbnailSettingViewModel(settings, () => changed = true);

        // Assert loaded state
        Assert.True(vm.ShowFileName);
        Assert.False(vm.ShowTags);
        Assert.Equal(1, vm.WaterfallModeIndex); // 1 = Vertical
        Assert.Equal("#FF0000", vm.BorderColor);
        Assert.Equal("0.8", vm.OpacityText);
        Assert.Equal("150", vm.ImagesPerPageText);

        // Modify in ViewModel
        vm.ShowTags = true;
        vm.WaterfallModeIndex = 2; // 2 = Horizontal
        vm.BorderColor = "#00FF00";
        vm.OpacityText = "0.95";
        vm.ImagesPerPageText = "120";

        // Save
        vm.SaveCommand.Execute(null);

        // Assert saved back to AppSettings
        Assert.True(changed);
        Assert.True(settings.ShowThumbnailTags);
        Assert.Equal("Horizontal", settings.WaterfallMode);
        Assert.Equal("#00FF00", settings.ThumbnailBorderColor);
        Assert.Equal(0.95, settings.ThumbnailOpacity);
        Assert.Equal(120, settings.ImagesPerPage);
    }

    private sealed class ThrowingFolderRepository : IFolderRepository
    {
        public Task<List<FolderInfo>> GetAllAsync() => throw new InvalidOperationException("DB error");
        public Task<FolderInfo?> GetByPathAsync(string path) => throw new InvalidOperationException();
        public Task AddAsync(string path) => Task.CompletedTask;
        public Task UpdateAliasAsync(string path, string? alias) => Task.CompletedTask;
        public Task RemoveAsync(string path) => Task.CompletedTask;
        public Task SetLastPageIndexAsync(string path, int pageIndex) => Task.CompletedTask;
        public Task<int?> GetLastPageIndexAsync(string path) => Task.FromResult<int?>(null);
        public Task SetArchivedAsync(long folderId, bool archived) => Task.CompletedTask;
        public Task RelocateFolderAsync(long folderId, string newFolderPath) => Task.CompletedTask;
    }
}
