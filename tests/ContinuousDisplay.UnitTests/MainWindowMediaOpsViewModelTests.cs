using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[Collection("MainWindowStaticSerial")]
public sealed class MainWindowMediaOpsViewModelTests
{
    [Fact]
    public async Task MoveFilesAsync_BatchMoveSuccess_RefreshesCurrentFolderWhenSourceAffected()
    {
        using var ctx = new MainWindowTestContext();
        var sourceDir = ctx.CreateTempFolderWithImages(2, "src-");
        var targetDir = ctx.CreateEmptyTempFolder("dst-");

        await ctx.ViewModel.LoadFolderAsync(sourceDir);
        Assert.Equal(2, ctx.ViewModel.Images.Count);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // Act: Move both files from sourceDir to targetDir
        await ctx.ViewModel.MoveFilesAsync(new[] { file1, file2 }, targetDir);

        // Assert: Status text reports success
        Assert.Contains("已移动 2 个文件到", ctx.ViewModel.StatusText);
        Assert.Contains(targetDir, ctx.ViewModel.StatusText);

        // Files moved on disk
        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
        Assert.True(File.Exists(Path.Combine(targetDir, Path.GetFileName(file1))));
        Assert.True(File.Exists(Path.Combine(targetDir, Path.GetFileName(file2))));

        // Because source folder was current folder, it reloaded and is now empty
        Assert.Empty(ctx.ViewModel.Images);
    }

    [Fact]
    public async Task MoveFilesAsync_Cancellation_SetsStatusAndDoesNotThrow()
    {
        using var ctx = new MainWindowTestContext();
        var sourceDir = ctx.CreateTempFolderWithImages(2);
        var targetDir = ctx.CreateEmptyTempFolder();

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Act
        await ctx.ViewModel.MoveFilesAsync(new[] { Path.Combine(sourceDir, "img_0.png") }, targetDir, cts.Token);

        // Assert
        Assert.Equal("文件移动已取消", ctx.ViewModel.StatusText);
    }

    [Fact]
    public void FormatMoveBatchResult_FormatsVariousOutcomesAccurately()
    {
        var target = @"C:\destination";

        // 1. All success
        var successResult = new MediaMoveBatchResult(
            new List<MediaMoveItemResult>
            {
                new(@"C:\src\1.png", @"C:\destination\1.png", MediaMoveItemStatus.Success)
            },
            TotalCount: 1, SuccessCount: 1, FailedCount: 0, SkippedCount: 0, CancelledCount: 0, CompensationFailedCount: 0);
        Assert.Equal(@"已移动 1 个文件到: C:\destination", MainWindowViewModel.FormatMoveBatchResult(successResult, target));

        // 2. Partial failure
        var failedResult = new MediaMoveBatchResult(
            new List<MediaMoveItemResult>
            {
                new(@"C:\src\1.png", @"C:\destination\1.png", MediaMoveItemStatus.Success),
                new(@"C:\src\2.png", null, MediaMoveItemStatus.Failed, "Access denied")
            },
            TotalCount: 2, SuccessCount: 1, FailedCount: 1, SkippedCount: 0, CancelledCount: 0, CompensationFailedCount: 0);
        var failedText = MainWindowViewModel.FormatMoveBatchResult(failedResult, target);
        Assert.Contains("失败 1 个", failedText);
        Assert.Contains("2.png", failedText);
        Assert.Contains("Access denied", failedText);

        // 3. Compensation failure
        var compResult = new MediaMoveBatchResult(
            new List<MediaMoveItemResult>
            {
                new(@"C:\src\broken.png", null, MediaMoveItemStatus.CompensationFailed, "Disk I/O error")
            },
            TotalCount: 1, SuccessCount: 0, FailedCount: 0, SkippedCount: 0, CancelledCount: 0, CompensationFailedCount: 1);
        var compText = MainWindowViewModel.FormatMoveBatchResult(compResult, target);
        Assert.Contains("移动遇到异常需人工核查", compText);
        Assert.Contains("待处理 1", compText);
        Assert.Contains("broken.png", compText);

        // 4. Cancelled
        var cancelledResult = new MediaMoveBatchResult(
            new List<MediaMoveItemResult>
            {
                new(@"C:\src\1.png", @"C:\destination\1.png", MediaMoveItemStatus.Success),
                new(@"C:\src\2.png", null, MediaMoveItemStatus.Cancelled)
            },
            TotalCount: 2, SuccessCount: 1, FailedCount: 0, SkippedCount: 0, CancelledCount: 1, CompensationFailedCount: 0);
        var cancelText = MainWindowViewModel.FormatMoveBatchResult(cancelledResult, target);
        Assert.Contains("移动已取消: 已移动 1 个文件，未执行 1 个", cancelText);
    }

    [Fact]
    public async Task MoveDirectoryAsync_Success_RefreshesTreeAndSetsStatus()
    {
        using var ctx = new MainWindowTestContext();
        var rootDir = ctx.CreateEmptyTempFolder("root-");
        var subDir = Path.Combine(rootDir, "subfolder");
        Directory.CreateDirectory(subDir);
        var targetParent = ctx.CreateEmptyTempFolder("dest-parent-");

        // Act
        var result = await ctx.ViewModel.MoveDirectoryAsync(subDir, targetParent);

        // Assert
        Assert.Equal(MediaMoveItemStatus.Success, result.Status);
        Assert.Contains("已移动文件夹到", ctx.ViewModel.StatusText);
        Assert.False(Directory.Exists(subDir));
        Assert.True(Directory.Exists(Path.Combine(targetParent, "subfolder")));
    }

    [Fact]
    public async Task CopyFilesAsync_CopiesFiles_AndResolvesNameConflicts()
    {
        using var ctx = new MainWindowTestContext();
        var sourceDir = ctx.CreateTempFolderWithImages(1, "copy-src-");
        var targetDir = ctx.CreateEmptyTempFolder("copy-dst-");

        var srcFile = Path.Combine(sourceDir, "img_0.png");

        // Pre-create conflicting file in targetDir with same name
        var conflictingDst = Path.Combine(targetDir, "img_0.png");
        File.WriteAllText(conflictingDst, "pre-existing content");

        // Act: Copy srcFile into targetDir
        await ctx.ViewModel.CopyFilesAsync(new[] { srcFile }, targetDir);

        // Assert: Success message
        Assert.Contains("已复制 1 个文件到", ctx.ViewModel.StatusText);

        // Original conflicting file remains intact
        Assert.Equal("pre-existing content", File.ReadAllText(conflictingDst));

        // Non-conflicting file was created with suffix " (1)"
        var resolvedFile = Path.Combine(targetDir, "img_0 (1).png");
        Assert.True(File.Exists(resolvedFile));
    }

    [Fact]
    public async Task CopyFilesAsync_Cancellation_SetsStatusText()
    {
        using var ctx = new MainWindowTestContext();
        var sourceDir = ctx.CreateTempFolderWithImages(1);
        var targetDir = ctx.CreateEmptyTempFolder();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await ctx.ViewModel.CopyFilesAsync(new[] { Path.Combine(sourceDir, "img_0.png") }, targetDir, cts.Token);

        // Assert
        Assert.Equal("文件复制已取消", ctx.ViewModel.StatusText);
    }

    [Fact]
    public async Task DeleteFilesAsync_DeletesFromDisk_AndRemovesFromView()
    {
        using var ctx = new MainWindowTestContext();
        var folder = ctx.CreateTempFolderWithImages(2, "del-");
        await ctx.ViewModel.LoadFolderAsync(folder);
        Assert.Equal(2, ctx.ViewModel.Images.Count);

        var file1 = ctx.ViewModel.Images[0].FilePath;
        var file2 = ctx.ViewModel.Images[1].FilePath;

        // Act: Delete file1
        await ctx.ViewModel.DeleteFilesAsync(new[] { file1 });

        // Assert
        Assert.Equal("已删除 1 个文件", ctx.ViewModel.StatusText);
        Assert.False(File.Exists(file1));
        Assert.True(File.Exists(file2));

        // Only file2 remains in Images collection
        Assert.Single(ctx.ViewModel.Images);
        Assert.Equal(file2, ctx.ViewModel.Images[0].FilePath);
    }

    [Fact]
    public async Task DeleteFilesAsync_NonExistentFile_CompletesGracefully()
    {
        using var ctx = new MainWindowTestContext();
        var missingPath = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".png");

        // Act
        await ctx.ViewModel.DeleteFilesAsync(new[] { missingPath });

        // Assert
        Assert.Equal("已删除 0 个文件", ctx.ViewModel.StatusText);
    }
}
