using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class ContinuousDisplayTests
{
    [Fact]
    public void ItemSourceMaterializesAndRetainsOnlyRequestedWindow()
    {
        var paths = Enumerable.Range(0, 6).Select(index => $"image-{index}.png").ToArray();
        var sizes = paths.Select(_ => new ImageDisplayItemSize(100, 100)).ToArray();
        var source = new ContinuousImageItemSource(paths, sizes, _ => new List<string>());

        var materialized = source.GetOrCreateItems(new ImageDisplayRange(2, 2));

        Assert.Equal(2, materialized.Count);
        Assert.Equal(2, source.CachedItemCount);
        Assert.Equal(paths[2], ((ImageViewItem)source[2]!).FilePath);
        Assert.Equal(3, source.IndexOf(materialized[1]));

        source.UpdateRetainedRange(new ImageDisplayRange(2, 2));
        Assert.Equal(2, source.GetCachedItems(new ImageDisplayRange(0, 6)).Count);

        source.UpdateRetainedRange(default);
        Assert.Equal(0, source.CachedItemCount);
    }

    [Fact]
    public void GeometryIndexReturnsRectsAndBoundedViewportRange()
    {
        var sizes = Enumerable.Repeat(new ImageDisplayItemSize(160, 120), 12).ToArray();
        var options = new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.None, 500, 160, 1);
        var geometry = ImageDisplayGeometryIndex.Build(sizes, options);

        Assert.Equal(12, geometry.Count);
        Assert.True(geometry.ExtentHeight > 0);
        Assert.True(geometry.TryGetItemRect(0, out var first));
        Assert.True(first.IsValid);
        Assert.False(geometry.TryGetItemRect(12, out _));

        var range = geometry.QueryViewport(first.Y, first.Height);

        Assert.False(range.IsEmpty);
        Assert.InRange(range.StartIndex, 0, geometry.Count - 1);
        Assert.InRange(range.EndExclusive, range.StartIndex + 1, geometry.Count);
    }
}
