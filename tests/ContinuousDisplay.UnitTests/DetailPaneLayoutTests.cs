using ImageManager.App.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class DetailPaneLayoutTests
{
    [Theory]
    [InlineData(double.NaN, 312)]
    [InlineData(double.PositiveInfinity, 312)]
    [InlineData(double.NegativeInfinity, 312)]
    [InlineData(-1, 248)]
    [InlineData(600, 520)]
    [InlineData(340, 340)]
    public void PreferredWidthUsesFiniteValuesInSupportedRange(double width, double expected)
    {
        Assert.Equal(expected, DetailPaneLayout.NormalizePreferredWidth(width));
    }

    [Fact]
    public void ShrinkingTheWindowReservesGalleryWidthAndRestoresPreferredWidthWhenItGrows()
    {
        Assert.Equal(new DetailPaneLayout(663, 400, 7, false), DetailPaneLayout.Calculate(1280, 210, true, 400));
        Assert.Equal(new DetailPaneLayout(303, 400, 7, false), DetailPaneLayout.Calculate(920, 210, true, 400));
        Assert.Equal(new DetailPaneLayout(270, 359, 7, false), DetailPaneLayout.Calculate(820, 184, true, 400));
        Assert.Equal(new DetailPaneLayout(663, 400, 7, false), DetailPaneLayout.Calculate(1280, 210, true, 400));
    }

    [Theory]
    [InlineData(720, 536, 400)]
    [InlineData(420, 236, 236)]
    public void DrawerOverlaysTheGalleryAndStaysWithinTheWorkspace(double client, double gallery, double pane)
    {
        Assert.Equal(new DetailPaneLayout(gallery, pane, 0, true), DetailPaneLayout.Calculate(client, 184, true, 400));
        Assert.Equal(new DetailPaneLayout(gallery, 0, 0, false), DetailPaneLayout.Calculate(client, 184, false, 400));
    }

    [Fact]
    public void IneffectiveResizeAtNarrowWidthDoesNotOverwritePreference()
    {
        Assert.Equal(520, DetailPaneLayout.AdjustPreferredWidth(750, 184, 520, 305));
        Assert.Equal(273, DetailPaneLayout.AdjustPreferredWidth(750, 184, 520, 273));
        Assert.Equal(400, DetailPaneLayout.AdjustPreferredWidth(1280, 210, 520, 400));
    }
}
