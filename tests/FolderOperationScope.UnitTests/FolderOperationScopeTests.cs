using ImageManager.App.Services;
using Xunit;

namespace FolderOperationScope.UnitTests;

public sealed class FolderOperationScopeTests
{
    [Fact]
    public void RecursiveParentOverlapsChildButNonRecursiveParentDoesNot()
    {
        const string parent = @"C:\Pictures\Events";
        const string child = @"C:\Pictures\Events\2026";

        Assert.True(ImageManager.App.Services.FolderOperationScope.ForFolder(parent, true)
            .Overlaps(ImageManager.App.Services.FolderOperationScope.ForFolder(child, false)));
        Assert.False(ImageManager.App.Services.FolderOperationScope.ForFolder(parent, false)
            .Overlaps(ImageManager.App.Services.FolderOperationScope.ForFolder(child, false)));
    }

    [Fact]
    public void SimilarPathPrefixIsNotInsideDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ImageManagerScopeRegression");

        Assert.True(FolderContentOperations.IsPathInside(Path.Combine(parent, "child"), parent));
        Assert.False(FolderContentOperations.IsPathInside(parent + "2", parent));
    }
}
