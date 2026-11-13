using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ReaderNavigationLayoutTests
{
    [Fact]
    public void TheCrossKeepsItsSizeAndDistanceFromTheEdge()
    {
        Assert.Equal(108, ReaderNavigationLayout.NavigationSize);
        Assert.Equal(24, ReaderNavigationLayout.RightInset);
        Assert.Equal(64, ReaderNavigationLayout.NearMargin);
    }

    [Fact]
    public void TheIdleHideDelayHasADefaultAndIsClampedToItsRange()
    {
        Assert.Equal(2000, ReaderNavigationLayout.IdleHideDelayMsDefault);
        Assert.Equal(500, ReaderNavigationLayout.ClampIdleHideDelayMs(0));
        Assert.Equal(500, ReaderNavigationLayout.ClampIdleHideDelayMs(-5));
        Assert.Equal(2000, ReaderNavigationLayout.ClampIdleHideDelayMs(2000));
        Assert.Equal(30_000, ReaderNavigationLayout.ClampIdleHideDelayMs(int.MaxValue));
        Assert.InRange(ReaderNavigationLayout.IdleHideDelayMsDefault, ReaderNavigationLayout.IdleHideDelayMsMinimum, ReaderNavigationLayout.IdleHideDelayMsMaximum);
    }
}
