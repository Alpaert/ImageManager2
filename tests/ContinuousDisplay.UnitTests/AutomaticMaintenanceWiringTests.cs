using System.Reflection;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public class AutomaticMaintenanceWiringTests
{
    [Fact]
    public void ScrollAndPagingDoNotCancelTheManualHashToken()
    {
        using var manual = new CancellationTokenSource();
        using var context = new MainWindowTestContext();
        typeof(MainWindowViewModel).GetField("_hashCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context.ViewModel, manual);
        context.ViewModel.NotifyGalleryInteraction();
        typeof(MainWindowViewModel).GetMethod("DeferHashPrecomputeForPaging", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.ViewModel, null);
        Assert.False(manual.IsCancellationRequested);
        Assert.False(context.ViewModel.CurrentFolderWorkToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsBeforeNewWriteButCompletesTagsForAnAlreadyInsertedDuplicate(bool cancelDuringInsert)
    {
        using var context = new MainWindowTestContext();
        var folder = context.CreateTempFolderWithImages(1);
        var source = Directory.GetFiles(context.CreateTempFolderWithImages(1))[0];
        using var cancel = new CancellationTokenSource();
        var inserts = 0;
        context.MetaProxy.CustomHandler = (method, _) =>
        {
            if (method.Name == "GetByFolderIdAsync") throw new Xunit.Sdk.XunitException("Sync must not hydrate metadata and tags.");
            if (method.Name == "GetFilePathsByFolderIdAsync") return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            if (method.Name == "GetByFileHashAsync")
            {
                if (!cancelDuringInsert) cancel.Cancel();
                return Task.FromResult<ImageMeta?>(new ImageMeta
                {
                    Id = 10, FilePath = source, Tags = [new TagCount { Name = "preserved-tag" }]
                });
            }
            if (method.Name == "UpsertAsync")
            {
                inserts++;
                cancel.Cancel();
                return Task.FromResult(20L);
            }
            return null;
        };
        var sync = (Task<bool>)typeof(MainWindowViewModel)
            .GetMethod("SyncFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.ViewModel, [folder, 1L, new[] { ".png" }, cancel.Token, false, null, true])!;
        if (cancelDuringInsert)
        {
            await sync;
            Assert.Equal(1, inserts);
            var write = Assert.Single(context.MetaProxy.SetTagsCalls);
            Assert.Equal(20L, write.ImageId);
            Assert.Equal(new[] { "preserved-tag" }, write.Tags);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
            Assert.Equal(0, inserts);
            Assert.Empty(context.MetaProxy.SetTagsCalls);
        }
    }
}
