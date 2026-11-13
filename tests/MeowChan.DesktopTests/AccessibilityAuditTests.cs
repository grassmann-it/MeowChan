using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>Heading roles and screen fitting from the 1.1.0 accessibility audit (M10, M5).</summary>
public sealed class AccessibilityAuditTests
{
    [Fact]
    public void PageAndSectionTitlesAreHeadingLevelsOneAndTwoForEveryTextBlockKind()
    {
        IReadOnlyList<Style> styles = HeadingLevels.Create();
        Assert.Equal(2, styles.Count);
        (string Class, int Level)[] expected = { ("page-title", 1), ("section-title", 2) };
        for (int index = 0; index < expected.Length; index++)
        {
            Setter setter = Assert.Single(styles[index].Setters.OfType<Setter>());
            Assert.Same(AutomationProperties.HeadingLevelProperty, setter.Property);
            Assert.Equal(expected[index].Level, setter.Value);
            Assert.Contains(expected[index].Class, styles[index].Selector!.ToString());
            // Is<TextBlock>: SelectableTextBlock titles (a plain TextBlock selector would skip them) are covered as well.
            Assert.Contains("TextBlock", styles[index].Selector!.ToString());
            Assert.StartsWith(":is(", styles[index].Selector!.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallingAddsTheHeadingStylesOnce()
    {
        Styles target = new();
        HeadingLevels.Install(target);
        Assert.Equal(2, target.Count);
    }

    [Theory]
    // A 1000 x 760 window on a 1080p screen (working area 1920 x 1040 physical pixels) at 100, 150 and 200 %.
    [InlineData(1.0, 1000, 760)]
    [InlineData(1.5, 1000, 661)]
    [InlineData(2.0, 928, 488)]
    public void AWindowThatDoesNotFitIsShrunkToTheWorkingAreaLessTheMargin(double scaling, double expectedWidth, double expectedHeight)
    {
        PixelRect area = new(0, 0, 1920, 1040);
        WindowStartupFit fit = DialogFit.Compute(new Size(1000, 760), null, new Size(600, 520), area, scaling, new PixelPoint(100, 100));
        Assert.Equal(expectedWidth, fit.Client.Width);
        Assert.Equal(expectedHeight, fit.Client.Height);
        Assert.True(fit.Client.Height * scaling <= 1040 - DialogFit.MarginDip * scaling + 1);
        Assert.True(fit.Minimum.Height <= fit.Client.Height && fit.Minimum.Width <= fit.Client.Width);
    }

    [Fact]
    public void ATallMinimumIsLoweredTooAndThePositionStaysInsideTheWorkingArea()
    {
        PixelRect area = new(0, 0, 1920, 1040);
        WindowStartupFit fit = DialogFit.Compute(new Size(800, 660), null, new Size(440, 700), area, 2.0, new PixelPoint(1500, 900));
        Assert.True(fit.Minimum.Height < 700);
        Assert.True(fit.Position.X + fit.Client.Width * 2.0 <= area.Right);
        Assert.True(fit.Position.Y + fit.Client.Height * 2.0 <= area.Bottom);
    }

    [Fact]
    public void AWindowThatFitsKeepsItsSizeAndMinimum()
    {
        PixelRect area = new(0, 0, 3840, 2160);
        WindowStartupFit fit = DialogFit.Compute(new Size(1000, 760), null, new Size(600, 520), area, 1.0, new PixelPoint(200, 100));
        Assert.Equal(new Size(1000, 760), fit.Client);
        Assert.Equal(new Size(600, 520), fit.Minimum);
        Assert.Equal(new PixelPoint(200, 100), fit.Position);
    }
}
