using Avalonia;
using ImageManager.App.Controls;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class ThumbnailPrefetchRangeTests
{
    [Theory]
    [InlineData(ContinuousScrollDirection.None)]
    [InlineData(ContinuousScrollDirection.Down)]
    [InlineData(ContinuousScrollDirection.Up)]
    public void PagedGridPrefetchSelectsAtMost48NearestItemsBeyondTheRealizedBuffer(
        ContinuousScrollDirection direction)
    {
        var items = Enumerable.Repeat(new ImageDisplayItemSize(100, 100), 500).ToArray();
        var geometry = ImageDisplayGeometryIndex.BuildPaged(items,
            new(ImageDisplayLayoutMode.None, 1020, 160, 1), 160, keepPadding: false);
        var viewport = new Rect(0, 6800, 1020, 500);
        const int buffer = 800;
        var retained = geometry.QueryPagedViewport(viewport.Y - buffer, viewport.Height + 2 * buffer);
        var prefetch = VirtualizingWaterfallPanel.GetPagedPrefetchIndexes(geometry, viewport, buffer, retained, direction);

        Assert.Equal(48, prefetch.Length);
        Assert.Empty(prefetch.Intersect(retained));
        var up = direction == ContinuousScrollDirection.Up;
        var edge = up ? viewport.Y - buffer : viewport.Bottom + buffer;
        var distances = prefetch.Select(index =>
        {
            Assert.True(geometry.TryGetItemRect(index, out var rect));
            return up ? edge - rect.Bottom : rect.Y - edge;
        }).ToArray();
        Assert.Equal(distances.Order(), distances);
        Assert.All(distances, distance => Assert.InRange(distance, 0, 3 * viewport.Height));
    }

    [Fact]
    public void PagedMasonryPrefetchSkipsTheRetainedTallCardOverlappingItsQuery()
    {
        var items = Enumerable.Repeat(new ImageDisplayItemSize(100, 100), 300).ToArray();
        items[0] = new ImageDisplayItemSize(1, 50_000);
        var geometry = ImageDisplayGeometryIndex.BuildPaged(items,
            new(ImageDisplayLayoutMode.Vertical, 320, 160, 1), double.NaN, keepPadding: false);
        var viewport = new Rect(0, 9000, 320, 500);
        const int buffer = 800;
        var retained = geometry.QueryPagedViewport(viewport.Y - buffer, viewport.Height + 2 * buffer);
        var prefetch = VirtualizingWaterfallPanel.GetPagedPrefetchIndexes(
            geometry, viewport, buffer, retained, ContinuousScrollDirection.Down);

        Assert.Contains(0, retained);
        Assert.DoesNotContain(0, prefetch);
        Assert.NotEmpty(prefetch);
        Assert.All(prefetch, index =>
        {
            Assert.True(geometry.TryGetItemRect(index, out var rect));
            Assert.InRange(rect.Y, viewport.Bottom + buffer, viewport.Bottom + buffer + 3 * viewport.Height);
        });
    }

    [Theory]
    [InlineData(0, 10, ContinuousScrollDirection.None, 10, 30)]
    [InlineData(200, 40, ContinuousScrollDirection.Down, 240, 64)]
    [InlineData(200, 40, ContinuousScrollDirection.Up, 136, 64)]
    [InlineData(3, 10, ContinuousScrollDirection.Up, 0, 3)]
    [InlineData(990, 8, ContinuousScrollDirection.Down, 998, 2)]
    public void ContinuousPrefetchStartsAtTheVisibleEdgeAndHonorsDirectionAndCap(
        int first, int count, ContinuousScrollDirection direction, int start, int expectedCount)
    {
        Assert.Equal(new ImageDisplayRange(start, expectedCount),
            MainWindowViewModel.GetContinuousPrefetchRange(new ImageDisplayRange(first, count), direction, 1000));
    }
}
