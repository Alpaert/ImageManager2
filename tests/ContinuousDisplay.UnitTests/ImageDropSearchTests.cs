using System.Text.Json;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class ImageDropSearchTests
{
    [Fact]
    public void SettingsWithoutDropPreferenceDefaultToWholeImageSearch()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        using var context = new MainWindowTestContext();

        Assert.False(settings.UseRegionSearchOnImageDrop);
        Assert.False(context.ViewModel.UseRegionSearchOnImageDrop);
        Assert.True(context.ViewModel.CanSelectWholeImageSearchMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropPreferenceLoadsWithoutSavingAndPreservesOtherSettings(bool savedPreference)
    {
        var repository = new FakeSettingsRepository
        {
            Settings = new AppSettings
            {
                DiskCacheDirectory = ImageManager.App.App.CacheDirectoryPath,
                UseRegionSearchOnImageDrop = savedPreference,
                SimilaritySearchResultLimit = 37,
                PerceptualSearchResultMode = PerceptualSearchResultMode.Jump
            }
        };
        using var context = new MainWindowTestContext(settingsRepo: repository);
        var vm = context.ViewModel;

        await vm.InitializeAsync();
        Assert.Equal(savedPreference, vm.UseRegionSearchOnImageDrop);
        Assert.Equal(!savedPreference, vm.CanSelectWholeImageSearchMode);
        Assert.Equal(0, repository.SaveCallCount);
        vm.SelectSimilarityMode(SimilaritySearchMode.Atmosphere);
        var selectedMode = vm.SelectedSimilarityMode;
        var originalHint = vm.ImageDropSearchHint;

        vm.UseRegionSearchOnImageDrop = !savedPreference;
        Assert.Equal(0, repository.SaveCallCount);
        await vm.SaveImageDropPreferenceAsync();

        Assert.Equal(1, repository.SaveCallCount);
        Assert.Equal(!savedPreference, repository.Settings.UseRegionSearchOnImageDrop);
        Assert.Equal(savedPreference, vm.CanSelectWholeImageSearchMode);
        Assert.NotEqual(originalHint, vm.ImageDropSearchHint);
        Assert.Equal(37, repository.Settings.SimilaritySearchResultLimit);
        Assert.Equal(PerceptualSearchResultMode.Jump, repository.Settings.PerceptualSearchResultMode);
        Assert.Equal(selectedMode, vm.SelectedSimilarityMode);
        Assert.False(vm.IsRegionSearchActive);
    }

    [Fact]
    public async Task DropPreferenceSaveFailureIsShownInStatus()
    {
        using var context = new MainWindowTestContext();
        context.SettingsRepo.OnSave = _ => Task.FromException(new IOException("fixture save failure"));
        var vm = context.ViewModel;
        vm.UseRegionSearchOnImageDrop = true;

        await vm.SaveImageDropPreferenceAsync();

        Assert.Contains("fixture save failure", vm.StatusText);
        Assert.True(vm.UseRegionSearchOnImageDrop);
    }

    [Fact]
    public async Task LateWholeImageResultCannotReplaceNewRegionResults()
    {
        using var context = new MainWindowTestContext();
        var vm = context.ViewModel;
        await vm.LoadFolderAsync(context.CreateTempFolderWithImages(3));
        var paths = vm.Images.Select(item => item.FilePath).ToArray();
        var wholeImageStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWholeImage = new TaskCompletionSource<List<SimilaritySearchResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.SimilarService.OnSearchByImage = (_, _, _, _, token) =>
        {
            wholeImageStarted.SetResult(token);
            return finishWholeImage.Task;
        };
        context.RegionService.Handler = new RegionSearchStub(paths[2]);
        vm.SelectSimilarityMode(SimilaritySearchMode.Semantic);

        var wholeImage = vm.SearchSimilarCommand.ExecuteAsync(paths[0]);
        try
        {
            var token = await wholeImageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.UseRegionSearchOnImageDrop = true;
            vm.BeginRegionSearchSession(0);
            Assert.True(token.IsCancellationRequested);
            await vm.QueueRegionSearchAsync(paths[0], new ImageRegion(5, 10, 20, 30),
                RegionSearchMode.SimilarContent, immediate: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.HasRegionSearchResults);
            Assert.Equal(paths[2], Assert.Single(vm.Images).FilePath);
            var regionStatus = vm.StatusText;

            finishWholeImage.SetResult([new SimilaritySearchResult(paths[1], .8f)]);
            await wholeImage.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsRegionSearchActive);
            Assert.Equal(paths[2], Assert.Single(vm.Images).FilePath);
            Assert.Equal(regionStatus, vm.StatusText);
        }
        finally
        {
            finishWholeImage.TrySetResult([]);
        }
    }

    private sealed class RegionSearchStub(string resultPath) : IRegionSearchService
    {
        public Task<RegionSearchResponse> SearchAsync(string path, ImageRegion? region,
            IReadOnlyCollection<string> candidates, RegionSearchMode mode, int limit, CancellationToken ct = default) =>
            Task.FromResult(new RegionSearchResponse([new SimilaritySearchResult(resultPath, .9f)],
                candidates.Count, candidates.Count));
    }
}
