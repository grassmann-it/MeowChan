using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// M4: the media column of a post lays its tiles out as a grid only for a post that is one; every other post keeps them stacked.
// Constructed panel without a window or a profile: the tiles are plain sized borders.
[Collection("Booru exact navigation")]
public sealed class ReaderMediaPanelTests
{
    private static ReaderMediaPanel Panel(bool grid, int tiles, double tileWidth)
    {
        ReaderMediaPanel panel = new() { DataContext = new ArchivePostRow("1", "", "", []) { MediaGrid = grid } };
        for (int i = 0; i < tiles; i++) panel.Children.Add(new Border { Width = tileWidth, Height = 100 });
        panel.Measure(new Size(376, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        return panel;
    }

    [Fact]
    public void APostWithoutAGridStacksNarrowPortraitTilesOneUnderTheOther() => BooruComponentDispatcher.Run(() =>
    {
        ReaderMediaPanel panel = Panel(grid: false, tiles: 3, tileWidth: 140);
        Assert.False(panel.IsGrid);
        Assert.Equal([0d, 0d, 0d], panel.Children.Select(child => child.Bounds.X));
        Assert.Equal([0d, 100d, 200d], panel.Children.Select(child => child.Bounds.Y));
        Assert.Equal(new Size(140, 300), panel.DesiredSize);
    });

    [Fact]
    public void AGridPostWrapsTwoTilesToALineAndTheDataContextDecidesItLive() => BooruComponentDispatcher.Run(() =>
    {
        ReaderMediaPanel panel = Panel(grid: true, tiles: 3, tileWidth: 176);
        Assert.True(panel.IsGrid);
        Assert.Equal([0d, 184d, 0d], panel.Children.Select(child => child.Bounds.X));
        Assert.Equal([0d, 0d, 100d], panel.Children.Select(child => child.Bounds.Y));
        panel.DataContext = new ArchivePostRow("2", "", "", []) { MediaGrid = false };
        Assert.False(panel.IsGrid);
        panel.Measure(new Size(376, double.PositiveInfinity));
        Assert.Equal(new Size(176, 300), panel.DesiredSize);
    });
}
