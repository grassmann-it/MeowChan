using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ReaderNavigationLayoutTests
{
    [Theory]
    [InlineData(320)]
    [InlineData(640)]
    [InlineData(1264)]
    [InlineData(1440)]
    [InlineData(1920)]
    [InlineData(3840)]
    public void TheCrossIsAlwaysAnOverlayWithTailClearanceAtEveryWidth(double width)
    {
        // The layout no longer depends on the surface width: no reserved side lane, only scroll extent at the end of the list.
        Assert.True(width > 0);
        var layout = ReaderNavigationLayout.Resolve(true);
        Assert.Equal(120, layout.TailInset);
        Assert.Equal(ReaderNavigationLayout.OverlayTailInset, layout.TailInset);
    }

    [Fact]
    public void ADockedReaderHasNoCrossAndNoClearance()
    {
        Assert.Equal(default, ReaderNavigationLayout.Resolve(false));
        Assert.Equal(0, ReaderNavigationLayout.Resolve(false).TailInset);
    }

    [Fact]
    public void TheCrossKeepsItsSizeAndDistanceFromTheEdge()
    {
        Assert.Equal(108, ReaderNavigationLayout.NavigationSize);
        Assert.Equal(24, ReaderNavigationLayout.RightInset);
        Assert.Equal(ReaderNavigationLayout.NavigationSize + 12, ReaderNavigationLayout.OverlayTailInset);
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
