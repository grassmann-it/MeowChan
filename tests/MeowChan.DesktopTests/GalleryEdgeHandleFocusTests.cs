using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Theming;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace MeowChan.DesktopTests;

// A11y review AX-03: the keyboard focus indicator of the two gallery edge handles. Before, focus only made the 39 % grey chevron visible: about
// 1.9:1 over a black picture, 1.2:1 over mid grey and 1.5:1 over white. Now the handle draws a dark disc with a white ring and a white chevron.
// The template is built without an Avalonia application; the contrast is calculated from the colours (WCAG 2.2), composited over pictures.
public sealed class GalleryEdgeHandleFocusTests
{
    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    private static string Rgb(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static string Grey(int level) => $"#{level:X2}{level:X2}{level:X2}";

    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static (Button Handle, Border Face, AvaloniaPath Chevron) Build(bool post)
    {
        Button handle = GalleryViewer.CreateEdgeIndicator(post);
        handle.ApplyTemplate();
        Border face = Assert.Single(handle.GetVisualDescendants().OfType<Border>(), border => border.Child is AvaloniaPath);
        return (handle, face, Assert.Single(handle.GetVisualDescendants().OfType<AvaloniaPath>()));
    }

    private static void SetFocusVisible(Button handle, bool on) => ((IPseudoClasses)handle.Classes).Set(GalleryEdgeHandleVisual.FocusVisiblePseudoClass, on);

    [Theory, InlineData(false), InlineData(true)]
    public void AtRestTheFaceIsEmptyAndTheChevronIsTheFaintHint(bool post)
    {
        var (handle, face, chevron) = Build(post);
        Assert.Null(face.Background);
        Assert.Null(face.BorderBrush);
        Assert.Equal(GalleryEdgeHandleVisual.RestGlyph, SolidColor(chevron.Stroke));
        Assert.False(face.IsHitTestVisible);
        // The 44 dip transparent hit surface and the localized name are unchanged.
        Assert.Equal(44, handle.Width);
        Assert.Equal(44, handle.Height);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void KeyboardFocusDrawsDiscRingAndWhiteChevronAndLossRestoresTheRestLook(bool post)
    {
        var (handle, face, chevron) = Build(post);
        SetFocusVisible(handle, true);
        Assert.Equal(GalleryEdgeHandleVisual.FocusDisc, SolidColor(face.Background));
        Assert.Equal(GalleryEdgeHandleVisual.FocusRing, SolidColor(face.BorderBrush));
        Assert.Equal(GalleryEdgeHandleVisual.FocusRing, SolidColor(chevron.Stroke));
        Assert.Equal(new Avalonia.Thickness(GalleryEdgeHandleVisual.RingThickness), face.BorderThickness);
        SetFocusVisible(handle, false);
        Assert.Null(face.Background);
        Assert.Null(face.BorderBrush);
        Assert.Equal(GalleryEdgeHandleVisual.RestGlyph, SolidColor(chevron.Stroke));
    }

    // Both tones of the indicator are measured against the picture behind the handle: the disc (90 % dark over the picture) and the white ring.
    // On a light picture the disc carries the 3:1, on a dark picture the ring; the worst picture is a mid grey (about 4.0:1). The chevron and the
    // ring read on the disc itself.
    [Fact]
    public void TheFocusIndicatorReadsOnEveryGreyPictureAtLeastThreeToOne()
    {
        string disc = Hex(GalleryEdgeHandleVisual.FocusDisc), ring = Rgb(GalleryEdgeHandleVisual.FocusRing);
        double worst = double.MaxValue;
        for (int level = 0; level <= 255; level += 5)
        {
            string picture = Grey(level);
            string shown = ContrastRules.CompositeOver(disc, picture);
            double best = Math.Max(ContrastRules.Contrast(shown, picture), ContrastRules.Contrast(ring, picture));
            worst = Math.Min(worst, best);
            Assert.True(best >= 3.0, $"picture {picture}: disc {ContrastRules.Contrast(shown, picture):0.00}:1, ring {ContrastRules.Contrast(ring, picture):0.00}:1");
            Assert.True(ContrastRules.Contrast(ring, shown) >= 4.5, $"picture {picture}: ring and chevron on the disc");
        }
        Assert.True(worst >= 3.0);
        // The two reference pictures of the review: white and black.
        Assert.True(ContrastRules.Contrast(ContrastRules.CompositeOver(disc, "#FFFFFF"), "#FFFFFF") >= 3.0);
        Assert.True(ContrastRules.Contrast(ring, "#000000") >= 3.0);
    }

    [Fact]
    public void TheRestHintIsStillTheOldFaintChevronNotAFocusIndicator()
    {
        // 39 % grey over black, mid grey and white is under 2:1 everywhere: it never counts as the focus indicator.
        foreach (string picture in new[] { "#000000", "#777777", "#FFFFFF" })
        {
            string shown = ContrastRules.CompositeOver(Hex(GalleryEdgeHandleVisual.RestGlyph), picture);
            Assert.True(ContrastRules.Contrast(shown, picture) < 2.0, picture);
        }
    }
}
