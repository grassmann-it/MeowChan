using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ReaderNavigationLayoutTests
{
    [Fact]
    public void TheBarKeepsItsSizeAndDistanceFromTheEdge()
    {
        Assert.Equal(40, ReaderNavigationLayout.BarHeight);
        Assert.Equal(320, ReaderNavigationLayout.BarMaxWidth);
        Assert.Equal(56, ReaderNavigationLayout.CounterMinWidth);
        Assert.Equal(700, ReaderNavigationLayout.CompactBelowSurfaceWidth);
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

    [Theory]
    [InlineData(1440, true, false)]
    [InlineData(700, true, false)]
    [InlineData(699.9, true, true)]
    [InlineData(640, true, true)]
    [InlineData(0, true, true)]
    [InlineData(1440, false, true)]
    [InlineData(700, false, true)]
    public void TheBarIsCompactBelowTheBreakWidthOrWithoutThePositionDisplay(double surfaceWidth, bool showPosition, bool compact) =>
        Assert.Equal(compact, ReaderNavigationLayout.IsCompact(surfaceWidth, showPosition));

    [Theory]
    [InlineData(0, 27, "1 / 27")]
    [InlineData(2, 27, "3 / 27")]
    [InlineData(11, 405, "12 / 405")]
    [InlineData(26, 27, "27 / 27")]
    [InlineData(0, 1, "1 / 1")]
    [InlineData(-1, 27, "–")]
    [InlineData(-1, 0, "–")]
    [InlineData(0, 0, "–")]
    [InlineData(27, 27, "–")]
    public void TheCounterIsOneBasedDigitsAndADashWhenThePositionIsNotInTheList(int index, int count, string expected) =>
        Assert.Equal(expected, ReaderNavigationLayout.CounterText(index, count));

    [Fact]
    public void TheCounterDigitsDoNotDependOnTheCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("1234 / 5678", ReaderNavigationLayout.CounterText(1233, 5678));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
