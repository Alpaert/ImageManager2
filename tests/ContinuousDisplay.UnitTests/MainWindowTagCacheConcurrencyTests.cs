using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowTagCacheConcurrencyTests
{
    [Theory]
    [InlineData(false, "add")]
    [InlineData(false, "replace")]
    [InlineData(false, "remove")]
    [InlineData(false, "clear")]
    [InlineData(true, "add")]
    [InlineData(true, "replace")]
    [InlineData(true, "remove")]
    [InlineData(true, "clear")]
    public async Task MissingTagLoad_LateDatabaseResult_PreservesInterveningEdit(
        bool ensureLoaded, string edit)
    {
        using var ctx = new MainWindowTestContext();
        const string path = @"C:\images\edited.png";
        const string otherPath = @"C:\images\other.png";
        var original = edit == "add" ? new List<string>() : new List<string> { "old", "keep" };
        ctx.MetaProxy.TagMap[path] = original.ToList();
        ctx.MetaProxy.TagMap[otherPath] = new List<string> { "other" };
        var item = new ImageViewItem { FilePath = path };
        ctx.ViewModel.Images.Add(item);

        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRead = new TaskCompletionSource<Dictionary<string, List<string>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        ctx.MetaProxy.CustomHandler = (method, _) =>
        {
            if (method.Name != "GetTagMapByPathsAsync" || Interlocked.Increment(ref reads) != 1)
                return null;
            readStarted.TrySetResult();
            return staleRead.Task;
        };

        var paths = new List<string> { path, otherPath };
        Task<Dictionary<string, List<string>>>? ensureTask = ensureLoaded
            ? ctx.ViewModel.EnsureTagsLoadedAsync(paths) : null;
        Task pendingLoad = ensureTask is not null ? ensureTask : ctx.ViewModel.PreloadTagsForFilesAsync(paths);
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            switch (edit)
            {
                case "add":
                    await ctx.ViewModel.AddTagToImagesBatchAsync(new List<string> { path }, "new");
                    break;
                case "replace":
                    await ctx.ViewModel.SetImageTagsAsync(path, new List<string> { "new" });
                    break;
                case "remove":
                    // A foreground read can finish while the earlier background read is pending.
                    await ctx.ViewModel.EnsureTagsLoadedAsync(new List<string> { path });
                    await ctx.ViewModel.RemoveTagFromImagesBatchAsync(new List<string> { path }, "old");
                    break;
                case "clear":
                    await ctx.ViewModel.ClearTagsFromImagesBatchAsync(new List<string> { path });
                    break;
            }
        }
        finally
        {
            staleRead.TrySetResult(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = original,
                [otherPath] = new List<string> { "other" }
            });
            await pendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var expected = edit switch
        {
            "clear" => Array.Empty<string>(),
            "remove" => new[] { "keep" },
            _ => new[] { "new" }
        };
        // Check persistence independently, so a cache failure cannot masquerade as a write failure.
        Assert.Equal(expected, ctx.MetaProxy.TagMap[path]);
        Assert.Equal(expected, ctx.ViewModel.GetTagsForFile(path));
        Assert.Equal(new[] { "other" }, ctx.ViewModel.GetTagsForFile(otherPath));
        if (ensureTask is not null)
            Assert.Equal(expected, (await ensureTask)[path]);
        else
            Assert.Equal(expected, item.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureTagsLoaded_MixedHitsAndMisses_ReturnsLatestCachedTags(bool clear)
    {
        using var ctx = new MainWindowTestContext();
        const string cachedPath = @"C:\images\cached.png";
        const string missingPath = @"C:\images\missing.png";
        await ctx.ViewModel.SetImageTagsAsync(cachedPath, new List<string> { "old" });
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<Dictionary<string, List<string>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.MetaProxy.CustomHandler = (method, _) =>
        {
            if (method.Name != "GetTagMapByPathsAsync") return null;
            readStarted.TrySetResult();
            return read.Task;
        };

        var load = ctx.ViewModel.EnsureTagsLoadedAsync(new List<string> { cachedPath, missingPath });
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (clear)
                await ctx.ViewModel.ClearTagsFromImagesBatchAsync(new List<string> { cachedPath });
            else
                await ctx.ViewModel.SetImageTagsAsync(cachedPath, new List<string> { "new" });
        }
        finally
        {
            read.TrySetResult(new Dictionary<string, List<string>> { [missingPath] = new() { "loaded" } });
            await load.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var expected = clear ? Array.Empty<string>() : new[] { "new" };
        Assert.Equal(expected, ctx.ViewModel.GetTagsForFile(cachedPath));
        Assert.Equal(expected, (await load)[cachedPath]);
        Assert.Equal(new[] { "loaded" }, (await load)[missingPath]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreloadTags_QueuedUiUpdate_UsesTagsAfterInterveningEdit(bool clear)
    {
        using var ctx = new MainWindowTestContext();
        const string path = @"C:\images\edited.png";
        ctx.MetaProxy.TagMap[path] = new List<string> { "old" };
        var item = new ImageViewItem { FilePath = path };
        ctx.ViewModel.Images.Add(item);
        var uiQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Dispatcher.InvokeActionAsync = async action =>
        {
            ctx.Dispatcher.InvokeActionAsync = null;
            uiQueued.TrySetResult();
            await releaseUi.Task.WaitAsync(TimeSpan.FromSeconds(10));
            action();
        };

        var preload = ctx.ViewModel.PreloadTagsForFilesAsync(new List<string> { path });
        try
        {
            await uiQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (clear)
                await ctx.ViewModel.ClearTagsFromImagesBatchAsync(new List<string> { path });
            else
                await ctx.ViewModel.SetImageTagsAsync(path, new List<string> { "new" });
        }
        finally
        {
            releaseUi.TrySetResult();
            await preload.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var expected = clear ? Array.Empty<string>() : new[] { "new" };
        Assert.Equal(expected, ctx.MetaProxy.TagMap[path]);
        Assert.Equal(expected, ctx.ViewModel.GetTagsForFile(path));
        Assert.Equal(expected, item.Tags);
    }
}
