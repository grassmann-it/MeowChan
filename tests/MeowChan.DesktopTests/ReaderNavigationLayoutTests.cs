using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ReaderNavigationLayoutTests
{
    [Theory]
    [InlineData(320)]
    [InlineData(640)]
    [InlineData(760)]
    [InlineData(1263.9)]
    public void NarrowReaderKeepsFullViewportInsteadOfReservingBottomOrSideLane(double width)
    {
        var layout = ReaderNavigationLayout.Resolve(true, width, 1120);
        Assert.Equal(default, layout.PostMargin);
        Assert.Equal(120, layout.TailInset);
    }

    [Fact]
    public void OnlyGenuinelyFreeWideSpaceReservesTheSideLane()
    {
        var layout = ReaderNavigationLayout.Resolve(true, 1264, 1120);
        Assert.Equal(new Thickness(0, 0, 144, 0), layout.PostMargin);
        Assert.Equal(0, layout.TailInset);
        Assert.Equal(default, ReaderNavigationLayout.Resolve(false, 1440, 1120));
    }

    [Theory]
    [InlineData(640, 640, 24)]
    [InlineData(1260, 1190, 94)]
    [InlineData(640, 650, 24)]
    public void OverlayAlignsInsideTheCenteredListAndKeepsItsScrollbarClear(double surface, double postRight, double inset)
    {
        var layout = ReaderNavigationLayout.Resolve(true, surface, 1120);
        Assert.Equal(inset, layout.RightInset(surface, postRight));
    }

    [Fact]
    public void TailSpaceIsNotCompoundedAndRestoresRecycledContainers()
    {
        ReaderPostTailSpace space = new();
        Border first = new() { Margin = new Thickness(1, 2, 3, 4) };
        Border second = new() { Margin = new Thickness(5, 6, 7, 8) };
        space.Update(first, 120);
        space.Update(first, 120);
        Assert.Equal(new Thickness(1, 2, 3, 124), first.Margin);
        space.Update(first, 80);
        Assert.Equal(new Thickness(1, 2, 3, 84), first.Margin);
        space.Release(second);
        Assert.Equal(new Thickness(1, 2, 3, 84), first.Margin);
        space.Update(second, 120);
        Assert.Equal(new Thickness(1, 2, 3, 4), first.Margin);
        Assert.Equal(new Thickness(5, 6, 7, 128), second.Margin);
        space.Release(second);
        Assert.Equal(new Thickness(5, 6, 7, 8), second.Margin);
    }

    [Fact]
    public void ReturningToDockedOrWideLayoutRemovesOnlyTheOwnedTailSpace()
    {
        ReaderPostTailSpace space = new();
        Border item = new() { Margin = new Thickness(3) };
        space.Update(item, 120);
        space.Update(item, 0);
        Assert.Equal(new Thickness(3), item.Margin);
        space.Update(item, 120);
        space.Update(null, 0);
        Assert.Equal(new Thickness(3), item.Margin);
    }
}
