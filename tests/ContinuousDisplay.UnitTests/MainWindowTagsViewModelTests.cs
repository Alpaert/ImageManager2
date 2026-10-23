using ImageManager.App.Models;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowTagsViewModelTests
{
    [Fact]
    public async Task AddTagToImageAsync_NormalizesAndAppends_OnlyWhenNotPresent()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var filePath = ctx.ViewModel.Images[0].FilePath;
        ctx.MetaProxy.TagMap[filePath] = new List<string> { "nature" };
        await ctx.ViewModel.RefreshImageTagsAsync(filePath);

        // Act 1: Add new tag with whitespace
        await ctx.ViewModel.AddTagToImageAsync(filePath, "  landscape  ");

        // Assert 1: Normalized and appended
        var tags = ctx.ViewModel.GetTagsForFile(filePath);
        Assert.Equal(2, tags.Count);
        Assert.Contains("nature", tags);
        Assert.Contains("landscape", tags);

        // Act 2: Add same tag with different casing
        var callsBefore = ctx.MetaProxy.SetTagsCalls.Count;
        await ctx.ViewModel.AddTagToImageAsync(filePath, "LANDSCAPE");

        // Assert 2: Not duplicated, no redundant DB write
        Assert.Equal(callsBefore, ctx.MetaProxy.SetTagsCalls.Count);
        tags = ctx.ViewModel.GetTagsForFile(filePath);
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public async Task RemoveTagFromImageAsync_RemovesMatchingTag_UpdatesCacheAndMeta()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(1);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var filePath = ctx.ViewModel.Images[0].FilePath;
        ctx.MetaProxy.TagMap[filePath] = new List<string> { "cat", "dog", "bird" };
        await ctx.ViewModel.RefreshImageTagsAsync(filePath);

        // Act: Remove "DOG" (different case)
        await ctx.ViewModel.RemoveTagFromImageAsync(filePath, "DOG");

        // Assert
        var tags = ctx.ViewModel.GetTagsForFile(filePath);
        Assert.Equal(2, tags.Count);
        Assert.Contains("cat", tags);
        Assert.Contains("bird", tags);
        Assert.DoesNotContain("dog", tags);
        Assert.NotEmpty(ctx.MetaProxy.SetTagsCalls);
    }

    [Fact]
    public async Task SetImageTagsAsync_NormalizesAndFiltersEmpty_AndUpdatesCharacterSuppression()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(1);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var filePath = ctx.ViewModel.Images[0].FilePath;

        // Register character tag in character store
        ctx.CharacterStore.Add("hatsune_miku", new float[128]);

        // Initial setup: image has hatsune_miku and 1girl
        ctx.MetaProxy.TagMap[filePath] = new List<string> { "hatsune_miku", "1girl" };
        await ctx.ViewModel.RefreshImageTagsAsync(filePath);

        // Act 1: Set tags without hatsune_miku, including spaces and empties
        await ctx.ViewModel.SetImageTagsAsync(filePath, new List<string> { "  1girl  ", "", "   ", "vocaloid" });

        // Assert 1: Whitespaces trimmed, empties removed, character removed -> suppression recorded
        var tags = ctx.ViewModel.GetTagsForFile(filePath);
        Assert.Equal(2, tags.Count);
        Assert.Contains("1girl", tags);
        Assert.Contains("vocaloid", tags);
        Assert.DoesNotContain("hatsune_miku", tags);

        Assert.NotEmpty(ctx.SuppressionRepo.SuppressCalls);
        var suppression = ctx.SuppressionRepo.SuppressCalls.Last();
        Assert.Contains("hatsune_miku", suppression.Tags);

        // Act 2: Re-add hatsune_miku
        await ctx.ViewModel.SetImageTagsAsync(filePath, new List<string> { "1girl", "vocaloid", "hatsune_miku" });

        // Assert 2: Unsuppression recorded
        Assert.NotEmpty(ctx.SuppressionRepo.UnsuppressCalls);
        var unsuppression = ctx.SuppressionRepo.UnsuppressCalls.Last();
        Assert.Contains("hatsune_miku", unsuppression.Tags);
    }

    [Fact]
    public async Task BatchTagOperations_AddRemoveClear_AffectsOnlyTargetFiles()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(3);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;
        var file3 = ctx.ViewModel.Images[2].FilePath;

        // Register character tag
        ctx.CharacterStore.Add("reimu_hakurei", new float[128]);

        // Act 1: Batch add character tag to file1 & file2
        await ctx.ViewModel.AddTagToImagesBatchAsync(new List<string> { file1, file2 }, "reimu_hakurei");

        // Assert 1: file1 & file2 have tag, file3 does not
        Assert.Contains("reimu_hakurei", ctx.ViewModel.GetTagsForFile(file1));
        Assert.Contains("reimu_hakurei", ctx.ViewModel.GetTagsForFile(file2));
        Assert.Empty(ctx.ViewModel.GetTagsForFile(file3));

        // Character tag was unsuppressed
        Assert.NotEmpty(ctx.SuppressionRepo.UnsuppressCalls);

        // Act 2: Batch remove from file1 only
        await ctx.ViewModel.RemoveTagFromImagesBatchAsync(new List<string> { file1 }, "reimu_hakurei");

        // Assert 2: file1 removed, file2 retained
        Assert.Empty(ctx.ViewModel.GetTagsForFile(file1));
        Assert.Contains("reimu_hakurei", ctx.ViewModel.GetTagsForFile(file2));
        Assert.NotEmpty(ctx.SuppressionRepo.SuppressCalls);

        // Act 3: Batch clear file2
        await ctx.ViewModel.ClearTagsFromImagesBatchAsync(new List<string> { file2 });

        // Assert 3: file2 cleared
        Assert.Empty(ctx.ViewModel.GetTagsForFile(file2));
        Assert.NotEmpty(ctx.MetaProxy.ClearTagsFromImagesCalls);
    }

    [Fact]
    public async Task PreloadTagsForFilesAsync_CacheMissQueriesMetaRepo_CacheHitSkips()
    {
        using var ctx = new MainWindowTestContext();
        var file1 = @"C:\images\file1.png";
        var file2 = @"C:\images\file2.png";

        // Seed file1 in cache by setting tags
        await ctx.ViewModel.SetImageTagsAsync(file1, new List<string> { "tag1" });

        // Setup file2 in DB proxy
        ctx.MetaProxy.TagMap[file2] = new List<string> { "tag2" };

        int metaQueryCount = 0;
        ctx.MetaProxy.CustomHandler = (method, args) =>
        {
            if (method.Name == "GetTagMapByPathsAsync")
            {
                metaQueryCount++;
                var queryPaths = (IEnumerable<string>)args![0]!;
                // Assert that file1 is not re-queried because it's already in cache
                Assert.DoesNotContain(file1, queryPaths);
                Assert.Contains(file2, queryPaths);
            }
            return null;
        };

        // Act 1: Preload both files
        await ctx.ViewModel.PreloadTagsForFilesAsync(new List<string> { file1, file2 });

        // Assert 1: Only missing file2 was queried
        Assert.Equal(1, metaQueryCount);
        Assert.Equal(new[] { "tag2" }, ctx.ViewModel.GetTagsForFile(file2));

        // Act 2: Preload again
        await ctx.ViewModel.PreloadTagsForFilesAsync(new List<string> { file1, file2 });

        // Assert 2: No additional queries made
        Assert.Equal(1, metaQueryCount);
    }

    [Fact]
    public async Task EnsureTagsLoadedAsync_ReturnsCompleteDictionary_AndFillsMissingFromDatabase()
    {
        using var ctx = new MainWindowTestContext();
        var fileA = @"C:\images\fileA.png";
        var fileB = @"C:\images\fileB.png";
        var fileC = @"C:\images\fileC.png";

        ctx.MetaProxy.TagMap[fileA] = new List<string> { "tagA" };
        ctx.MetaProxy.TagMap[fileB] = new List<string> { "tagB" };
        // fileC is not in DB

        // Act
        var result = await ctx.ViewModel.EnsureTagsLoadedAsync(new List<string> { fileA, fileB, fileC });

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "tagA" }, result[fileA]);
        Assert.Equal(new[] { "tagB" }, result[fileB]);
        Assert.Empty(result[fileC]);

        // Subsequent call hits cache directly
        var cachedTags = ctx.ViewModel.GetTagsForFile(fileC);
        Assert.Empty(cachedTags);
    }

    [Fact]
    public async Task RenameTagAsync_UpdatesTagRepo_MemoryCache_AndSyncsArtistName()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // Register artist in artist store & chinese library
        ctx.ArtistStore.Add("artist_alpha", new float[128]);
        ctx.ChineseLib.Register("artist_alpha", "阿尔法画师");

        // Set tags on both files
        await ctx.ViewModel.SetImageTagsAsync(file1, new List<string> { "artist_alpha", "landscape" });
        await ctx.ViewModel.SetImageTagsAsync(file2, new List<string> { "artist_alpha", "portrait" });

        // Act: Rename tag
        var result = await ctx.ViewModel.RenameTagAsync("artist_alpha", "artist_beta");

        // Assert
        Assert.Equal(RenameResult.Success, result);
        Assert.NotEmpty(ctx.TagRepo.RenameCalls);
        Assert.Equal(("artist_alpha", "artist_beta"), ctx.TagRepo.RenameCalls[0]);

        // Memory cache updated
        Assert.Contains("artist_beta", ctx.ViewModel.GetTagsForFile(file1));
        Assert.DoesNotContain("artist_alpha", ctx.ViewModel.GetTagsForFile(file1));
        Assert.Contains("artist_beta", ctx.ViewModel.GetTagsForFile(file2));
        Assert.DoesNotContain("artist_alpha", ctx.ViewModel.GetTagsForFile(file2));

        // Artist store & Chinese lib synchronized
        Assert.True(ctx.ArtistStore.ArtistsDict.ContainsKey("artist_beta"));
        Assert.False(ctx.ArtistStore.ArtistsDict.ContainsKey("artist_alpha"));
        Assert.True(ctx.ChineseLib.ArtistNames.ContainsKey("artist_beta"));
    }

    [Fact]
    public async Task MergeTagsAsync_ReplacesOrRemovesDuplicates_InMemoryCache()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // File1 already has both source and target tags
        await ctx.ViewModel.SetImageTagsAsync(file1, new List<string> { "old_tag", "target_tag" });
        // File2 only has source tag
        await ctx.ViewModel.SetImageTagsAsync(file2, new List<string> { "old_tag", "unique_tag" });

        // Act: Merge old_tag into target_tag
        await ctx.ViewModel.MergeTagsAsync("old_tag", "target_tag");

        // Assert
        Assert.NotEmpty(ctx.TagRepo.MergeCalls);
        Assert.Equal(("old_tag", "target_tag"), ctx.TagRepo.MergeCalls[0]);

        // File1: old_tag deduplicated away, target_tag preserved
        var tags1 = ctx.ViewModel.GetTagsForFile(file1);
        Assert.Single(tags1);
        Assert.Equal("target_tag", tags1[0]);

        // File2: old_tag renamed to target_tag
        var tags2 = ctx.ViewModel.GetTagsForFile(file2);
        Assert.Equal(2, tags2.Count);
        Assert.Contains("target_tag", tags2);
        Assert.Contains("unique_tag", tags2);
    }

    [Fact]
    public async Task RefreshTagsAfterExternalWriteAsync_UpdatesImagesItemsAndRefreshesTagCounts()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2);
        await ctx.ViewModel.LoadFolderAsync(folder);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // Simulate external AI auto-tag write to DB
        ctx.MetaProxy.TagMap[file1] = new List<string> { "ai_1girl", "ai_solo" };
        ctx.TagRepo.TagCounts = new List<TagCount>
        {
            new() { Name = "ai_1girl", Count = 1 },
            new() { Name = "ai_solo", Count = 1 }
        };

        // Act
        await ctx.ViewModel.RefreshTagsAfterExternalWriteAsync(new List<string> { file1 });

        // Assert: ViewModel.Images[0] tags updated in-place on UI collection
        var imgItem = ctx.ViewModel.Images[0];
        Assert.Equal(2, imgItem.Tags.Count);
        Assert.Contains("ai_1girl", imgItem.Tags);
        Assert.Contains("ai_solo", imgItem.Tags);

        // Tag counts re-queried
        var allCounts = ctx.ViewModel.GetAllTagCounts();
        Assert.Equal(2, allCounts.Count);
    }
}
