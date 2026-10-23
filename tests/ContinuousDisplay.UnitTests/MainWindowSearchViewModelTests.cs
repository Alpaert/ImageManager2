using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowSearchViewModelTests
{
    [Fact]
    public async Task SearchSimilarAsync_SuccessfulSearch_UpdatesStatusAndDisplay()
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(imageCount: 3);
        await context.ViewModel.LoadFolderAsync(folder);

        var firstFile = context.ViewModel.Images[0].FilePath;
        var secondFile = context.ViewModel.Images[1].FilePath;

        context.SimilarService.ResultsToReturn = new List<SimilaritySearchResult>
        {
            new(firstFile, 1.0f, SimilarityMatchKind.PerceptualStrict),
            new(secondFile, 0.95f, SimilarityMatchKind.PerceptualStrict)
        };

        await context.ViewModel.SearchSimilarCommand.ExecuteAsync(firstFile);

        Assert.True(context.ViewModel.IsShowingSearchResult);
        Assert.True(context.ViewModel.HasSearchResults);
        Assert.NotEmpty(context.ViewModel.SearchResultInfo);
        Assert.Contains("相似", context.ViewModel.SearchResultInfo);
        Assert.Contains("2", context.ViewModel.StatusText);
        Assert.Equal(2, context.ViewModel.Images.Count);
    }

    [Fact]
    public async Task SearchSimilarAsync_WhenCancelled_SetsCancelledStatus()
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(imageCount: 2);
        await context.ViewModel.LoadFolderAsync(folder);

        var targetFile = context.ViewModel.Images[0].FilePath;

        var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SimilarService.OnSearchByImage = async (path, candidates, mode, limit, ct) =>
        {
            searchStarted.SetResult();
            var tcs = new TaskCompletionSource<List<SimilaritySearchResult>>();
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task;
            }
        };

        var searchTask = context.ViewModel.SearchSimilarCommand.ExecuteAsync(targetFile);
        await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel the search operation
        context.ViewModel.StopSearchCommand.Execute(null);
        await searchTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("搜索已取消", context.ViewModel.StatusText);
        Assert.False(context.ViewModel.IsShowingSearchResult);
    }

    [Fact]
    public async Task SearchSimilarAsync_WhenThrows_SetsErrorStatusAndRecovers()
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(imageCount: 2);
        await context.ViewModel.LoadFolderAsync(folder);

        var targetFile = context.ViewModel.Images[0].FilePath;

        // 1. Service throws an exception
        context.SimilarService.ThrowOnSearch = new InvalidOperationException("Inference engine offline");

        await context.ViewModel.SearchSimilarCommand.ExecuteAsync(targetFile);

        Assert.Contains("相似搜索失败", context.ViewModel.StatusText);
        Assert.False(context.ViewModel.IsShowingSearchResult);

        // 2. Recover on next search
        context.SimilarService.ThrowOnSearch = null;
        context.SimilarService.ResultsToReturn = new List<SimilaritySearchResult>
        {
            new(targetFile, 1.0f, SimilarityMatchKind.PerceptualStrict)
        };

        await context.ViewModel.SearchSimilarCommand.ExecuteAsync(targetFile);

        Assert.True(context.ViewModel.IsShowingSearchResult);
        Assert.Contains("相似", context.ViewModel.SearchResultInfo);
        Assert.Contains("1", context.ViewModel.StatusText);
    }

    [Fact]
    public async Task ReturnFromSearchToFolderNavigation_CleansUpSearchResultState()
    {
        using var context = new MainWindowTestContext();
        var folderA = context.CreateTempFolderWithImages(imageCount: 3, prefix: "SearchA_");
        var folderB = context.CreateTempFolderWithImages(imageCount: 2, prefix: "NavB_");

        await context.ViewModel.LoadFolderAsync(folderA);
        var targetFile = context.ViewModel.Images[0].FilePath;

        context.SimilarService.ResultsToReturn = new List<SimilaritySearchResult>
        {
            new(targetFile, 1.0f, SimilarityMatchKind.PerceptualStrict)
        };

        await context.ViewModel.SearchSimilarCommand.ExecuteAsync(targetFile);
        Assert.True(context.ViewModel.IsShowingSearchResult);
        Assert.True(context.ViewModel.HasSearchResults);

        // Add a node to folder tree and simulate search highlight
        var node = new FolderTreeNode { Path = folderA, DisplayName = "FolderA", IsSearchHighlight = true };
        context.ViewModel.FolderTree.Add(node);
        Assert.True(node.IsSearchHighlight);

        // Now navigate to another folder
        await context.ViewModel.LoadFolderAsync(folderB);

        // Assert search state is cleaned up
        Assert.False(context.ViewModel.IsShowingSearchResult);
        Assert.False(context.ViewModel.HasSearchResults);
        Assert.Empty(context.ViewModel.SearchResultInfo);
        Assert.Equal(folderB, context.ViewModel.CurrentFolder);
        Assert.Equal(2, context.ViewModel.Images.Count);
    }
}
