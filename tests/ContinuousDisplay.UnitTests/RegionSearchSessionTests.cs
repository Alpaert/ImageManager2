using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class RegionSearchSessionTests
{
    [Fact]
    public async Task ModelLeaseIsSharedWithinSessionAndReleasedOnExitAndShutdown()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(1));
        var stub = new SearchStub();
        using var services = Install(context, stub);
        var vm = context.ViewModel;
        vm.BeginRegionSearchSession(0);
        vm.BeginRegionSearchSession(0);
        Assert.Equal(1, stub.ActiveSessions);
        vm.StopRegionSearch();
        Assert.Equal(1, stub.ActiveSessions);
        await vm.ExitRegionSearchSessionAsync();
        Assert.Equal(0, stub.ActiveSessions);
        vm.BeginRegionSearchSession(0);
        Assert.Equal(1, stub.ActiveSessions);
        vm.ShutdownBackgroundWork();
        Assert.Equal(0, stub.ActiveSessions);
    }

    [Fact]
    public async Task RemovingResultFiltersEntrySnapshotAndInvalidatesPendingRequest()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(3));
        var vm = context.ViewModel;
        var paths = vm.Images.Select(x => x.FilePath).ToArray();
        vm.SelectSimilarityMode(SimilaritySearchMode.Semantic);
        context.SimilarService.ResultsToReturn = new() { new(paths[0], .9f), new(paths[1], .8f) };
        await vm.SearchSimilarCommand.ExecuteAsync(paths[0]);
        vm.ReplaceSelectedFiles(new[] { paths[0] });
        var stub = new SearchStub();
        using var services = Install(context, stub);
        vm.BeginRegionSearchSession(20);
        await vm.QueueRegionSearchAsync(paths[0], null, RegionSearchMode.FindOriginal, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RegionSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.Search = (_, _) => { started.TrySetResult(); return release.Task; };
        var pending = vm.QueueRegionSearchAsync(paths[0], null, RegionSearchMode.FindOriginal, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.RemoveFilesFromViewAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { paths[0] });
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.ExitRegionSearchSessionAsync();
        release.SetResult(Response(paths[0]));
        Assert.True(vm.IsShowingSearchResult);
        Assert.Equal(paths[1], Assert.Single(vm.Images).FilePath);
        Assert.DoesNotContain(paths[0], vm.SelectedFilePaths);
        Assert.Contains("0.800", vm.SearchResultInfo);
        Assert.False(vm.IsRegionSearchBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitCannotRestoreSelectionStatusOrScrollOverNewIntent(bool navigate)
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(3);
        var nextFolder = context.CreateTempFolderWithImages(2);
        var vm = context.ViewModel;
        await vm.LoadFolderAsync(folder);
        var paths = vm.Images.Select(x => x.FilePath).ToArray();
        await vm.ApplyDisplayFilterAsync(new DisplayFilterOptions { Orientation = MediaOrientation.Square });
        vm.ReplaceSelectedFiles(new[] { paths[0] });
        vm.BeginRegionSearchSession(123);
        var filterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFilter = new TaskCompletionSource<Dictionary<string, (int Width, int Height)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        context.MetaProxy.CustomHandler = (method, _) =>
        {
            if (method.Name == "GetDimensionsByPathsAsync" && Interlocked.Increment(ref calls) == 1)
            {
                filterStarted.TrySetResult();
                return releaseFilter.Task;
            }
            return null;
        };
        var exiting = vm.ExitRegionSearchSessionAsync();
        await filterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (navigate) await vm.LoadFolderAsync(nextFolder);
        else
        {
            vm.SelectSimilarityMode(SimilaritySearchMode.Semantic);
            context.SimilarService.ResultsToReturn = new() { new(paths[2], .7f) };
            await vm.SearchSimilarCommand.ExecuteAsync(paths[2]);
        }
        var currentPath = vm.Images[0].FilePath;
        vm.ReplaceSelectedFiles(new[] { currentPath });
        var currentStatus = vm.StatusText;
        releaseFilter.SetResult(paths.ToDictionary(path => path, _ => (100, 100)));
        await exiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { currentPath }, vm.SelectedFilePaths);
        Assert.Equal(currentStatus, vm.StatusText);
        Assert.False(vm.CanRestoreRegionSearchScroll);
        Assert.Equal(navigate ? nextFolder : folder, vm.CurrentFolder);
    }

    [Fact]
    public async Task CompletedExitScrollPermissionExpiresWhenSessionReopens()
    {
        using var context = new MainWindowTestContext();
        var vm = context.ViewModel;
        await vm.LoadFolderAsync(context.CreateTempFolderWithImages(1));
        vm.BeginRegionSearchSession(123);
        await vm.ExitRegionSearchSessionAsync();
        Assert.True(vm.CanRestoreRegionSearchScroll);
        vm.BeginRegionSearchSession(456);
        Assert.False(vm.CanRestoreRegionSearchScroll);
    }

    private sealed class SearchStub : IRegionSearchService
    {
        public int ActiveSessions;
        public IDisposable BeginSession()
        {
            ActiveSessions++;
            return new Lease(this);
        }
        private sealed class Lease(SearchStub owner) : IDisposable
        {
            private SearchStub? _owner = owner;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _owner, null) is { } released) released.ActiveSessions--;
            }
        }
        public Func<string, int, Task<RegionSearchResponse>> Search { get; set; } =
            (path, _) => Task.FromResult(Response(path));
        public Task<RegionSearchResponse> SearchAsync(string path, ImageRegion? region,
            IReadOnlyCollection<string> candidates, RegionSearchMode mode, int limit, CancellationToken ct = default)
            => Search(path, limit);
    }

    private static RegionSearchResponse Response(string path) =>
        new(new[] { new SimilaritySearchResult(path, .9f) }, 3, 3);

    private static ServiceProvider Install(MainWindowTestContext context, SearchStub stub)
    {
        context.RegionService.Handler = stub;
        var provider = new ServiceCollection().AddSingleton(context.PageManager)
            .AddSingleton<IRegionSearchService>(stub).BuildServiceProvider();
        typeof(ImageManager.App.App).GetProperty("Services")!.SetValue(null, provider);
        return provider;
    }

    [Fact]
    public async Task ReplacingRequest_SerializesNativeWork_AndPublishesOnlyLatest()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(3));
        var paths = context.ViewModel.Images.Select(x => x.FilePath).ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RegionSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var stub = new SearchStub { Search = (path, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); return release.Task; }
            return Task.FromResult(Response(path));
        }};
        using var services = Install(context, stub);
        var vm = context.ViewModel;
        vm.BeginRegionSearchSession(42);
        var first = vm.QueueRegionSearchAsync(paths[0], null, RegionSearchMode.FindOriginal, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = vm.QueueRegionSearchAsync(paths[1], null, RegionSearchMode.FindOriginal, true);
        var last = vm.QueueRegionSearchAsync(paths[2], null, RegionSearchMode.FindOriginal, true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        release.SetResult(Response(paths[0]));
        await last.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.Single(vm.Images);
        Assert.Equal(paths[2], vm.Images[0].FilePath);
        Assert.False(vm.IsRegionSearchBusy);
    }

    [Fact]
    public async Task StopAndExit_DoNotWaitForNativeWork_AndRestoreBrowsingSelection()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(3));
        var vm = context.ViewModel;
        var paths = vm.Images.Select(x => x.FilePath).ToArray();
        vm.ReplaceSelectedFiles(new[] { paths[1] });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RegionSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = Install(context, new SearchStub { Search = (_, _) => { started.SetResult(); return release.Task; } });
        vm.BeginRegionSearchSession(123);
        var request = vm.QueueRegionSearchAsync(paths[0], null, RegionSearchMode.FindOriginal, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.StopRegionSearch();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.ExitRegionSearchSessionAsync();
        release.SetResult(Response(paths[0]));
        Assert.False(vm.IsRegionSearchActive);
        Assert.False(vm.IsShowingSearchResult);
        Assert.Equal(123, vm.RegionSearchRestoreOffset);
        Assert.Equal(paths, vm.Images.Select(x => x.FilePath));
        Assert.Equal(new[] { paths[1] }, vm.SelectedFilePaths);
    }

    [Fact]
    public async Task FailureKeepsCommittedResults_AndExitRestoresPreviousSimilaritySearch()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(3));
        var vm = context.ViewModel;
        var paths = vm.Images.Select(x => x.FilePath).ToArray();
        vm.SelectSimilarityMode(SimilaritySearchMode.Semantic);
        context.SimilarService.ResultsToReturn = new() { new(paths[0], .8f) };
        await vm.SearchSimilarCommand.ExecuteAsync(paths[0]);
        var oldInfo = vm.SearchResultInfo;
        var stub = new SearchStub();
        using var services = Install(context, stub);
        vm.BeginRegionSearchSession(55);
        await vm.QueueRegionSearchAsync(paths[1], null, RegionSearchMode.FindOriginal, true);
        Assert.Equal(paths[1], Assert.Single(vm.Images).FilePath);
        stub.Search = (_, _) => throw new InvalidOperationException("fixture failure");
        await vm.QueueRegionSearchAsync(paths[2], null, RegionSearchMode.FindOriginal, true);
        Assert.Equal(paths[1], Assert.Single(vm.Images).FilePath);
        Assert.Contains("fixture failure", vm.RegionSearchStatus);
        Assert.True(vm.HasRegionSearchResults);
        await vm.ExitRegionSearchSessionAsync();
        Assert.True(vm.IsShowingSearchResult);
        Assert.Equal(paths[0], Assert.Single(vm.Images).FilePath);
        Assert.Equal(oldInfo, vm.SearchResultInfo);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(70, 70)]
    [InlineData(700, 500)]
    public async Task SessionUsesSharedClampedLimit(int configured, int expected)
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(1));
        var vm = context.ViewModel;
        var path = vm.Images[0].FilePath;
        var actual = 0;
        using var services = Install(context, new SearchStub { Search = (p, limit) =>
        { actual = limit; return Task.FromResult(Response(p)); } });
        vm.AppSettings.SimilaritySearchResultLimit = configured;
        vm.BeginRegionSearchSession(0);
        await vm.QueueRegionSearchAsync(path, null, RegionSearchMode.FindOriginal, true);
        Assert.Equal(expected, actual);
        Assert.Contains(expected.ToString(), vm.RegionSearchResultLimitLabel);
    }

    [Fact]
    public async Task DebounceExecutesOnlyFinalInput_AndRecursiveScopeStaysSessionLocal()
    {
        using var context = new MainWindowTestContext();
        await context.ViewModel.LoadFolderAsync(context.CreateTempFolderWithImages(1));
        var vm = context.ViewModel;
        var path = vm.Images[0].FilePath;
        var calls = 0;
        using var services = Install(context, new SearchStub { Search = (p, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Response(p)); } });
        vm.SearchScope = 0;
        vm.BeginRegionSearchSession(0);
        vm.RegionSearchRecursive = true;
        var first = vm.QueueRegionSearchAsync(path, null, RegionSearchMode.FindOriginal);
        var second = vm.QueueRegionSearchAsync(path, null, RegionSearchMode.SimilarContent);
        await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(0, vm.SearchScope);
    }
}
