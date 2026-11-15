using System.Collections.ObjectModel;
using System.Reflection;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class DetailPaneIntegrationTests
{
    [Fact]
    public void FocusFollowsSelectedPathWithoutReplacingMultiSelection()
    {
        using var ctx = new MainWindowTestContext();
        var vm = ctx.ViewModel;
        SetFiles(vm, ["a.jpg", "b.jpg", "c.jpg"]);
        vm.ReplaceSelectedFiles(["a.jpg", "b.jpg"]);
        vm.FocusDetailFile("b.jpg");
        Assert.Equal("b.jpg", vm.DetailsPane.CurrentPath);
        Assert.Equal(2, vm.SelectedFilePaths.Count);
        vm.FocusDetailFile("c.jpg");
        Assert.Equal("b.jpg", vm.DetailsPane.CurrentPath);
        vm.SetFileSelected("b.jpg", false);
        Assert.Equal("a.jpg", vm.DetailsPane.CurrentPath);
        vm.ReplaceSelectedFiles([]);
        Assert.Null(vm.DetailsPane.CurrentPath);
    }

    [Fact]
    public void ReplacingSelectionWithItsOwnLazyProjectionDoesNotEraseIt()
    {
        using var ctx = new MainWindowTestContext();
        SetFiles(ctx.ViewModel, ["a.jpg", "b.jpg"]);
        ctx.ViewModel.ReplaceSelectedFiles(["a.jpg", "b.jpg"]);
        ctx.ViewModel.ReplaceSelectedFiles(ctx.ViewModel.SelectedFilePaths.Where(p => p == "b.jpg"));
        Assert.Equal(["b.jpg"], ctx.ViewModel.SelectedFilePaths);
        Assert.Equal("b.jpg", ctx.ViewModel.DetailsPane.CurrentPath);
    }

    [Fact]
    public void FilterSourceReplacementClearsInvalidDetailWithoutChangingSelection()
    {
        using var ctx = new MainWindowTestContext();
        var vm = ctx.ViewModel;
        SetFiles(vm, ["a.jpg", "b.jpg"]);
        vm.ReplaceSelectedFiles(["a.jpg"]);
        SetFiles(vm, ["b.jpg"]);
        Assert.Null(vm.DetailsPane.CurrentPath);
        Assert.True(vm.IsFileSelected("a.jpg"));
        Assert.Empty(vm.GetValidSelectedFilePaths());
        vm.ReconcileSelectionWithPublishedResults();
        Assert.False(vm.IsFileSelected("a.jpg"));
    }

    [Fact]
    public void PublishedResultsRetainOffPageSelectionButRemoveFilteredOutPaths()
    {
        using var ctx = new MainWindowTestContext();
        var vm = ctx.ViewModel;
        SetFiles(vm, ["a.jpg", "b.jpg", "c.jpg"]);
        vm.ReplaceSelectedFiles(["a.jpg", "c.jpg"]);
        vm.Images = new ObservableCollection<ImageViewItem> { new() { FilePath = "a.jpg" } };
        vm.ReconcileSelectionWithPublishedResults();
        Assert.Equal(2, vm.GetValidSelectedFilePaths().Count);
        SetFiles(vm, ["b.jpg", "c.jpg"]);
        vm.ReconcileSelectionWithPublishedResults();
        Assert.Equal(["c.jpg"], vm.GetValidSelectedFilePaths());
        SetFiles(vm, ["a.jpg", "b.jpg", "c.jpg"]);
        vm.ReconcileSelectionWithPublishedResults();
        Assert.Equal(["c.jpg"], vm.GetValidSelectedFilePaths());
    }

    [Fact]
    public void DetailSelectionDoesNotRealizeContinuousSource()
    {
        using var ctx = new MainWindowTestContext();
        var paths = Enumerable.Range(0, 10_000).Select(i => $"image-{i}.jpg").ToList();
        var source = new ContinuousImageItemSource(paths, paths.Select(_ => new ImageDisplayItemSize(100, 100)).ToList(), _ => []);
        var vm = ctx.ViewModel;
        SetFiles(vm, paths);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ContinuousImageItemSource))!.SetValue(vm, source);
        vm.ReplaceSelectedFiles([paths[^1]]);
        Assert.Equal(paths[^1], vm.DetailsPane.CurrentPath);
        Assert.Equal(0, source.CachedItemCount);
    }

    [Fact]
    public async Task WidthSettingsCoalesceAndFlushTheFinalPreference()
    {
        using var ctx = new MainWindowTestContext();
        var vm = ctx.ViewModel;
        for (var i = 0; i < 50; i++) vm.SetDetailsPaneWidth(300 + i);
        vm.SetDetailsPaneOpen(true);
        await vm.FlushDetailsPaneSettingsAsync();
        Assert.Equal(349, ctx.SettingsRepo.Settings.DetailsPaneWidth);
        Assert.True(ctx.SettingsRepo.Settings.DetailsPaneOpen);
        Assert.Equal(1, ctx.SettingsRepo.SaveCallCount);
        vm.AppSettings.DetailsPaneWidth = double.NaN;
        vm.SyncUISettingsFromAppData();
        Assert.Equal(312, vm.DetailsPane.PreferredWidth);
        Assert.Equal(312, vm.AppSettings.DetailsPaneWidth);
    }

    [Fact]
    public async Task ExternalTagWriteRefreshesCurrentDetailOnlyAndPreservesItsThumbnail()
    {
        using var ctx = new MainWindowTestContext(detailsPaneOpen: true);
        var path = Directory.GetFiles(ctx.CreateTempFolderWithImages(1))[0];
        ctx.MetaProxy.TagMap[path] = ["old"];
        var vm = ctx.ViewModel;
        var thumbnail = await ctx.CacheService.GetOrCreateThumbnailWithDiagnosticsAsync(path, vm.PageManager.ThumbnailDecodeWidth);
        Assert.NotNull(thumbnail.Data);
        Assert.NotNull(thumbnail.Version);
        var bytes = thumbnail.Data;
        vm.Images = new ObservableCollection<ImageViewItem> { new()
        {
            FilePath = path, FileName = Path.GetFileName(path), ThumbnailData = bytes,
            ThumbnailVersion = thumbnail.Version
        } };
        SetFiles(vm, [path]);
        vm.ReplaceSelectedFiles([path]);
        await Until(() => !vm.DetailsPane.IsLoading && vm.DetailsPane.Tags.SequenceEqual(["old"]));
        ctx.MetaProxy.TagMap[path] = ["new"];
        await vm.RefreshTagsAfterExternalWriteAsync([path], refreshCounts: false);
        await Until(() => !vm.DetailsPane.IsLoading && vm.DetailsPane.Tags.SequenceEqual(["new"]));
        Assert.Same(bytes, vm.DetailsPane.ThumbnailItem!.ThumbnailData);
        Assert.Equal([path], vm.SelectedFilePaths);
        vm.ShutdownBackgroundWork();
        Assert.Null(vm.DetailsPane.ThumbnailItem);
    }

    [Fact]
    public async Task DetailTagEditUpdatesPagedAndContinuousProjectionsWithoutChangingSelection()
    {
        using var ctx = new MainWindowTestContext();
        var path = Directory.GetFiles(ctx.CreateTempFolderWithImages(1))[0];
        ctx.MetaProxy.TagMap[path] = ["old"];
        var vm = ctx.ViewModel;
        SetFiles(vm, [path]);
        var paged = new ImageViewItem { FilePath = path, Tags = ["old"] };
        vm.Images = new ObservableCollection<ImageViewItem> { paged };
        var source = new ContinuousImageItemSource([path], [new ImageDisplayItemSize(100, 100)], _ => ["old"]);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ContinuousImageItemSource))!.SetValue(vm, source);
        var continuous = source.GetOrCreateItems(new ImageDisplayRange(0, 1)).Single();
        vm.ReplaceSelectedFiles([path]);
        await vm.SetImageTagsAsync(path, [" new ", "new"]);
        Assert.Equal(["new"], paged.Tags);
        Assert.Equal(["new"], continuous.Tags);
        Assert.Equal([path], vm.SelectedFilePaths);
        Assert.Equal(1, source.CachedItemCount);
    }

    [Fact]
    public async Task ReopeningWaitsForTheCurrentGalleryLoadInsteadOfGeneratingAnotherThumbnail()
    {
        using var ctx = new MainWindowTestContext();
        var path = Directory.GetFiles(ctx.CreateTempFolderWithImages(1))[0];
        var source = new ImageViewItem { FilePath = path, IsLoading = true };
        var vm = ctx.ViewModel;
        vm.Images = new ObservableCollection<ImageViewItem> { source };
        SetFiles(vm, [path]);
        vm.ReplaceSelectedFiles([path]);
        vm.SetDetailsPaneOpen(true);
        await Until(() => !vm.DetailsPane.IsLoading);
        Assert.Null(vm.DetailsPane.ThumbnailItem!.ThumbnailData);
        var thumbnail = await ctx.CacheService.GetOrCreateThumbnailWithDiagnosticsAsync(path, vm.PageManager.ThumbnailDecodeWidth);
        Assert.NotNull(thumbnail.Data);
        Assert.NotNull(thumbnail.Version);
        var bytes = thumbnail.Data;
        source.ThumbnailVersion = thumbnail.Version;
        source.ThumbnailData = bytes;
        await Until(() => vm.DetailsPane.ThumbnailItem?.ThumbnailData != null);
        Assert.Same(bytes, vm.DetailsPane.ThumbnailItem.ThumbnailData);
        await vm.FlushDetailsPaneSettingsAsync();
    }

    [Fact]
    public async Task ContinuousDetailUsesItsCurrentSourceBeforeTheInactivePagedCache()
    {
        using var ctx = new MainWindowTestContext(detailsPaneOpen: true);
        var path = Directory.GetFiles(ctx.CreateTempFolderWithImages(1))[0];
        var vm = ctx.ViewModel;
        var thumbnail = await ctx.CacheService.GetOrCreateThumbnailWithDiagnosticsAsync(path, vm.PageManager.ThumbnailDecodeWidth);
        Assert.NotNull(thumbnail.Data);
        Assert.NotNull(thumbnail.Version);
        var currentBytes = thumbnail.Data;
        var staleBytes = currentBytes.ToArray();
        var version = thumbnail.Version;
        vm.Images = new ObservableCollection<ImageViewItem> { new()
        {
            FilePath = path, ThumbnailData = staleBytes, ThumbnailVersion = version
        } };
        var source = new ContinuousImageItemSource([path], [new ImageDisplayItemSize(100, 100)], _ => []);
        var item = source.GetOrCreateItems(new ImageDisplayRange(0, 1)).Single();
        item.ThumbnailData = currentBytes;
        item.ThumbnailVersion = version;
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ContinuousImageItemSource))!.SetValue(vm, source);
        typeof(MainWindowViewModel).GetField("_displayMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, ImageDisplayMode.Continuous);
        SetFiles(vm, [path]);
        vm.ReplaceSelectedFiles([path]);
        await Until(() => vm.DetailsPane.ThumbnailItem?.ThumbnailData != null);
        Assert.Same(currentBytes, vm.DetailsPane.ThumbnailItem!.ThumbnailData);
    }

    private static void SetFiles(MainWindowViewModel vm, List<string> files)
    {
        typeof(MainWindowViewModel).GetField("_displayFilteredFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, files);
        typeof(MainWindowViewModel).GetMethod("NotifyDisplayFilterState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
