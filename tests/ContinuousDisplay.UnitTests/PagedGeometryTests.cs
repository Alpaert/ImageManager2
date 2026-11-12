using ImageManager.App.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class PagedGeometryTests
{
    [Fact]
    public void GridSlotIncludesImageMinimumHeightAndTemplateChrome()
    {
        var geometry = ImageDisplayGeometryIndex.BuildPaged(
            [new(100, 100), new(100, 100), new(100, 100)],
            new(ImageDisplayLayoutMode.None, 340, 160, 1), 40, keepPadding: true);

        Assert.True(geometry.TryGetItemRect(2, out var card));
        Assert.Equal(new ImageDisplayItemRect(0, 96, 170, 96), card);
        Assert.Equal(192, geometry.ExtentHeight);
        Assert.Equal([2], geometry.QueryPagedViewport(96, 1));
    }

    [Fact]
    public void VerticalViewportDoesNotRetainInterveningCardsBehindTallCard()
    {
        var sizes = new ImageDisplayItemSize[] { new(1, 10_000), default, new(100, 100), new(100, 100), new(100, 100) };
        var geometry = ImageDisplayGeometryIndex.BuildPaged(sizes,
            new(ImageDisplayLayoutMode.Vertical, 320, 160, 1), double.NaN, keepPadding: false);

        Assert.True(geometry.TryGetItemRect(0, out var tallCard));
        Assert.Equal(1_600_042, tallCard.Height);
        Assert.True(geometry.TryGetItemRect(1, out var unknownCard));
        Assert.Equal(new ImageDisplayItemRect(160, 0, 160, 202), unknownCard);
        Assert.Equal([0, 3], geometry.QueryPagedViewport(410, 100));
    }

    [Fact]
    public void HorizontalFinalRowUsesPriorHeightAndLegacyMeasuredExtent()
    {
        var geometry = ImageDisplayGeometryIndex.BuildPaged([new(100, 100), new(100, 100), default],
            new(ImageDisplayLayoutMode.Horizontal, 400, 160, 1), double.NaN, keepPadding: false);

        var firstRowHeight = 180d * 400 / 380;
        Assert.True(geometry.TryGetItemRect(0, out var firstCard));
        Assert.Equal(200, firstCard.Width, precision: 10);
        Assert.Equal(firstRowHeight, firstCard.Height, precision: 10);
        Assert.True(geometry.TryGetItemRect(2, out var finalCard));
        Assert.Equal(firstRowHeight, finalCard.Y, precision: 10);
        Assert.Equal(firstRowHeight, finalCard.Height, precision: 10);
        Assert.Equal(firstRowHeight * 0.75, finalCard.Width, precision: 10);
        Assert.Equal(firstRowHeight + 180d * 400 / 135, geometry.ExtentHeight, precision: 10);
    }
}
