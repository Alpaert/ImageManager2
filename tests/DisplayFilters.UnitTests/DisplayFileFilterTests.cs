using ImageManager.App.Services;
using ImageManager.Core.Models;
using Xunit;

namespace DisplayFilters.UnitTests;

public sealed class DisplayFileFilterTests
{
    [Fact]
    public async Task TypeFilterIsCaseInsensitiveAndPreservesOrder()
    {
        var result = await DisplayFileFilter.ApplyAsync(
            new[] { "a.JPG", "b.mp4", "c.txt", "d.PNG" },
            new DisplayFilterOptions("image"),
            (_, _) => Task.FromResult(new Dictionary<string, (int Width, int Height)>()),
            (_, _) => Task.FromResult(new Dictionary<string, int>()),
            _ => throw new InvalidOperationException("image header should not be read"));

        Assert.Equal(new[] { "a.JPG", "d.PNG" }, result.Files);
    }

    [Fact]
    public async Task OrientationFilterReportsUnknownDimensions()
    {
        var result = await DisplayFileFilter.ApplyAsync(
            new[] { "unknown.jpg", "landscape.jpg", "portrait.jpg" },
            new DisplayFilterOptions(null, MediaOrientation.Landscape, IncludeUnknownDimensions: false),
            (_, _) => Task.FromResult(new Dictionary<string, (int Width, int Height)>
            {
                ["unknown.jpg"] = (0, 0),
                ["landscape.jpg"] = (16, 9),
                ["portrait.jpg"] = (9, 16)
            }),
            (_, _) => Task.FromResult(new Dictionary<string, int>()),
            _ => (0, 0));

        Assert.Equal(new[] { "landscape.jpg" }, result.Files);
        Assert.Equal(1, result.UnknownDimensionsCount);
    }
}
