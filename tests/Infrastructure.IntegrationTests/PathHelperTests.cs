using ImageManager.Common.Helpers;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class PathHelperTests : IDisposable
{
    private readonly string _testRoot;

    public PathHelperTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-PathHelperTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("test.jpg", true)]
    [InlineData("test.jpeg", true)]
    [InlineData("test.png", true)]
    [InlineData("test.webp", true)]
    [InlineData("test.gif", true)]
    [InlineData("test.bmp", true)]
    [InlineData("test.txt", false)]
    [InlineData("test.exe", false)]
    [InlineData("test.mp4", false)]
    public void IsImageFile_CorrectlyIdentifiesExtensions(string fileName, bool expected)
    {
        Assert.Equal(expected, PathHelper.IsImageFile(fileName));
    }

    [Fact]
    public void GetNonConflictingPath_NonExistingTarget_ReturnsSamePath()
    {
        var target = Path.Combine(_testRoot, "unique.png");
        var result = PathHelper.GetNonConflictingPath(target);
        Assert.Equal(target, result);
    }

    [Fact]
    public void GetNonConflictingPath_ExistingTarget_IncrementsIndex()
    {
        var target = Path.Combine(_testRoot, "photo.png");
        File.WriteAllText(target, "content0");

        var result1 = PathHelper.GetNonConflictingPath(target);
        Assert.Equal(Path.Combine(_testRoot, "photo (1).png"), result1);

        File.WriteAllText(result1, "content1");
        var result2 = PathHelper.GetNonConflictingPath(target);
        Assert.Equal(Path.Combine(_testRoot, "photo (2).png"), result2);
    }

    [Fact]
    public void NormalizeFolderPath_AppendsDirectorySeparator()
    {
        var raw = _testRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = PathHelper.NormalizeFolderPath(raw);

        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), normalized);
    }

    [Fact]
    public void IsPathInside_RecognizesSubpaths_AndRejectsSiblings()
    {
        var parent = Path.Combine(_testRoot, "parent");
        var child = Path.Combine(parent, "child", "file.png");
        var sibling = Path.Combine(_testRoot, "sibling", "file.png");

        Assert.True(PathHelper.IsPathInside(child, parent));
        Assert.True(PathHelper.IsPathInside(parent, parent)); // exact match
        Assert.False(PathHelper.IsPathInside(sibling, parent));
        Assert.False(PathHelper.IsPathInside(parent, child));
        Assert.False(PathHelper.IsPathInside("", parent));
        Assert.False(PathHelper.IsPathInside(child, ""));
    }

    [Fact]
    public void IsSafeCopyOrMoveDestination_PreventsNestingInsideSelf()
    {
        var sourceDir = Path.Combine(_testRoot, "my_source");
        var nestedDest = Path.Combine(sourceDir, "subfolder");
        var outsideDest = Path.Combine(_testRoot, "other_folder");

        // Moving into self or child of self is unsafe
        Assert.False(PathHelper.IsSafeCopyOrMoveDestination(sourceDir, sourceDir));
        Assert.False(PathHelper.IsSafeCopyOrMoveDestination(sourceDir, nestedDest));

        // Moving outside is safe
        Assert.True(PathHelper.IsSafeCopyOrMoveDestination(sourceDir, outsideDest));
    }
}
