using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// The focused post's enlarged medium must fit the visible reader: whole, at its aspect, never above its percentage or the column.
[Collection("Localization")]
public sealed class ReaderMediaFitTests : IDisposable
{
    public ReaderMediaFitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // The attachment view builds icon buttons and needs the compiled assets and a render platform: the owned-dispatcher harness of the Booru tests.
    private static void OnPlatform(Action test) => BooruComponentDispatcher.Run(test);

    private const double Tolerance = 1e-6;

    private static void AssertSize(double width, double height, Size actual)
    {
        Assert.Equal(width, actual.Width, Tolerance);
        Assert.Equal(height, actual.Height, Tolerance);
    }

    [Fact]
    public void RoomyViewportEnlargesToTheConfiguredPercentage()
    {
        AssertSize(480, 270, ReaderMediaFit.ShownSize(1000, 100, new Size(240, 135), 200, 1000));
        AssertSize(360, 202.5, ReaderMediaFit.ShownSize(1000, 100, new Size(240, 135), 150, 1000));
    }

    [Fact]
    public void ViewportHeightMinusHeaderCapsTheMediumAndKeepsTheAspect()
    {
        // 500 high viewport, 150 of it header, file line and paddings: 350 left for a picture that would be 540 high at 300 percent.
        Size shown = ReaderMediaFit.ShownSize(500, 150, new Size(120, 180), 300, 1000);
        AssertSize(350d * 120 / 180, 350, shown);
        Assert.Equal(120d / 180, shown.Width / shown.Height, Tolerance);
    }

    [Fact]
    public void PercentageIsNeverExceededEvenWhenTheViewportIsHuge()
    {
        Size shown = ReaderMediaFit.ShownSize(5000, 100, new Size(240, 135), 200, 5000);
        AssertSize(480, 270, shown);
        // An option value at or below 100 percent leaves the ordinary size.
        AssertSize(240, 135, ReaderMediaFit.ShownSize(5000, 100, new Size(240, 135), 100, 5000));
        AssertSize(240, 135, ReaderMediaFit.ShownSize(5000, 100, new Size(240, 135), 50, 5000));
    }

    [Fact]
    public void ColumnWidthCapsTheMedium()
    {
        Size shown = ReaderMediaFit.ShownSize(2000, 100, new Size(240, 135), 300, 300);
        AssertSize(300, 168.75, shown);
    }

    [Fact]
    public void ATinyViewportNeverShrinksTheMediumBelowItsOrdinarySize()
    {
        AssertSize(240, 135, ReaderMediaFit.ShownSize(150, 100, new Size(240, 135), 200, 1000));
        // The viewport that is smaller than the header alone has no visible height; the same floor applies.
        AssertSize(240, 135, ReaderMediaFit.ShownSize(60, 100, new Size(240, 135), 200, 1000));
    }

    [Fact]
    public void UnknownViewportOnlyAppliesThePercentageAndTheColumn()
    {
        AssertSize(480, 270, ReaderMediaFit.ShownSize(double.NaN, 100, new Size(240, 135), 200, 1000));
        AssertSize(480, 270, ReaderMediaFit.ShownSize(0, 100, new Size(240, 135), 200, 1000));
        Assert.True(double.IsNaN(ReaderMediaFit.VisibleHeight(double.NaN, 50)));
        Assert.True(double.IsNaN(ReaderMediaFit.VisibleHeight(-5, 50)));
        Assert.Equal(1, ReaderMediaFit.VisibleHeight(40, 50));
        Assert.Equal(350, ReaderMediaFit.VisibleHeight(500, 150));
    }

    // A fully capped card aligned by a jump (top at AlignedTop) keeps its bottom edge and the scroll air in view (review 2026-11-15, A3).
    [Fact]
    public void VisibleHeightLeavesTheAlignmentAirFree()
    {
        Assert.Equal(350 - ReaderMediaFit.AlignedAir, ReaderMediaFit.VisibleHeight(500, 150, ReaderMediaFit.AlignedAir));
        Assert.Equal(350, ReaderMediaFit.VisibleHeight(500, 150, double.NaN));
        Assert.Equal(1, ReaderMediaFit.VisibleHeight(160, 150, ReaderMediaFit.AlignedAir));
        double viewport = 641, chrome = 130;
        double frame = ReaderMediaFit.VisibleHeight(viewport, chrome, ReaderMediaFit.AlignedAir);
        double cardBottom = ReaderMediaFit.AlignedTop + chrome + frame;
        Assert.True(cardBottom + ReaderMediaFit.CardMarginBelow + ReaderMediaFit.ScrollMargin <= viewport + Tolerance);
        // The medium inside that card needs no further scroll once the card is aligned.
        double frameTop = ReaderMediaFit.AlignedTop + 60;
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(frameTop, frameTop + frame, viewport));
    }

    // A5(a): a card that was not arranged yet caps its frame with the list's last measured chrome, not the fixed estimate.
    [Fact]
    public void ChromeMemoUsesTheLastMeasurementForACardNotArrangedYet()
    {
        ReaderMediaFit.ChromeMemo memo = new();
        Assert.Equal(ReaderMediaFit.HeaderEstimate, memo.Header(double.NaN)); // nothing measured yet
        Assert.Equal(ReaderMediaFit.HeaderEstimate, memo.Header(0));          // a layout at its card's origin is no measurement
        Assert.Equal(41, memo.Header(41));
        Assert.Equal(41, memo.Header(0));
        Assert.Equal(41, memo.Header(double.NaN));
        Assert.Equal(4, memo.Column(4, isMeasurement: false));  // before any measurement the caller's own value stays
        Assert.Equal(24, memo.Column(24, isMeasurement: true));
        Assert.Equal(24, memo.Column(4, isMeasurement: false));
        Assert.Equal(30, memo.Column(30, isMeasurement: true));
    }

    [Fact]
    public void UnusablePictureSizeIsReturnedUnchanged()
    {
        Assert.Equal(new Size(0, 0), ReaderMediaFit.ShownSize(500, 100, new Size(0, 0), 200, 1000));
        Assert.Equal(new Size(100, 0), ReaderMediaFit.ShownSize(500, 100, new Size(100, 0), 200, 1000));
    }

    // The frame the reader really draws is MediaSlotGeometry.Fit with the cap the attachment view derives; both must agree.
    [Theory]
    [InlineData(16d / 9, 1000, 1000, 200, false)]
    [InlineData(16d / 9, 1000, 200, 200, false)]   // viewport cap bites on a landscape picture
    [InlineData(0.5, 1000, 300, 200, false)]       // portrait, cap bites
    [InlineData(0.5, 1000, 2000, 300, false)]      // roomy
    [InlineData(16d / 9, 300, 2000, 300, false)]   // column narrower than the enlarged width
    [InlineData(16d / 9, 1000, 100, 200, true)]    // tiny viewport: floor at the ordinary picture
    [InlineData(2.4, 1000, 260, 250, false)]
    public void DrawnFrameEqualsTheShownSize(double aspect, double column, double visible, int percent, bool tiny)
    {
        double ordinaryWidth = 240, ordinaryHeight = 180, factor = percent / 100d;
        Size ordinary = MediaSlotGeometry.Fit(aspect, column, ordinaryWidth, ordinaryHeight);
        double cap = ReaderMediaFit.HeightCap(ordinaryHeight, factor, visible, MediaSlotGeometry.Fit(aspect, double.PositiveInfinity, ordinaryWidth, ordinaryHeight).Height);
        Size drawn = MediaSlotGeometry.Fit(aspect, column, ordinaryWidth * factor, cap);
        // ShownSize gets the header as the difference between the viewport and the visible height.
        Size expected = ReaderMediaFit.ShownSize(visible + 100, 100, ordinary, percent, column);
        AssertSize(expected.Width, expected.Height, drawn);
        Assert.True(drawn.Width <= ordinaryWidth * factor + Tolerance && drawn.Width <= column + Tolerance);
        if (!tiny) Assert.True(drawn.Height <= Math.Max(visible, ordinary.Height) + Tolerance);
        Assert.Equal(aspect, drawn.Width / drawn.Height, 1e-3);
    }

    [Fact]
    public void HeightCapKeepsTheOrdinaryCapTimesFactorWithoutAViewport()
    {
        Assert.Equal(360, ReaderMediaFit.HeightCap(180, 2, double.NaN, 135));
        Assert.Equal(360, ReaderMediaFit.HeightCap(180, 2, 1000, 135));
        Assert.Equal(200, ReaderMediaFit.HeightCap(180, 2, 200, 135));
        Assert.Equal(135, ReaderMediaFit.HeightCap(180, 2, 100, 135));
        Assert.Equal(180, ReaderMediaFit.HeightCap(180, 1, 100, 400)); // never above the wanted height
    }

    // The scroll intent: positive means the list moves further down.
    [Fact]
    public void MediumAlreadyInViewNeedsNoScroll()
    {
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(40, 340, 600));
    }

    [Fact]
    public void MediumCutOffBelowScrollsDownJustEnough()
    {
        // A 300 high medium whose bottom is 100 dip under the edge of a 500 high viewport, plus 8 dip of air.
        Assert.Equal(108, ReaderMediaFit.ScrollDelta(300, 600, 500), Tolerance);
    }

    [Fact]
    public void MediumCutOffAboveScrollsUp()
    {
        Assert.Equal(-48, ReaderMediaFit.ScrollDelta(-40, 260, 600), Tolerance);
    }

    [Fact]
    public void MediumTallerThanTheViewportAlignsItsTop()
    {
        Assert.Equal(120, ReaderMediaFit.ScrollDelta(120, 900, 600));
        Assert.Equal(-30, ReaderMediaFit.ScrollDelta(-30, 700, 600));
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(0, 600, 600)); // exactly as tall: already aligned
    }

    [Fact]
    public void AirShrinksToFitATightViewportAndBadInputScrollsNothing()
    {
        // 590 of 600: only 5 dip of air each side, so the medium is placed with that.
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(5, 595, 600), Tolerance);
        Assert.Equal(5, ReaderMediaFit.ScrollDelta(10, 600, 600), Tolerance);
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(double.NaN, 100, 600));
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(10, 100, 0));
        Assert.Equal(0, ReaderMediaFit.ScrollDelta(100, 10, 600));
    }

    [Fact]
    public void ScrollingByTheIntentLeavesTheMediumFullyVisible()
    {
        foreach (double top in new[] { -200d, -5, 0, 4, 100, 330, 480, 700 })
        {
            double height = 280, viewport = 500;
            double delta = ReaderMediaFit.ScrollDelta(top, top + height, viewport);
            double newTop = top - delta;
            Assert.True(newTop >= -Tolerance && newTop + height <= viewport + Tolerance, $"top {top}: {newTop}");
        }
    }

    // The attachment view feeds the cap to its frame: only while its card is focused and enlarged, and from the list's visible height.
    [Fact]
    public void AttachmentViewCapsTheEnlargedFrameByTheVisibleReaderHeight() => OnPlatform(() =>
    {
        ArchiveAttachmentView view = new()
        {
            DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "m-portrait", OriginalFileName: "tall.png", ExpectedSize: 1000))
        };
        MediaFramePanel frame = view.GetLogicalDescendants().OfType<MediaFramePanel>().Single();
        frame.Aspect = 0.5; // portrait: ordinary 90 x 180, enlarged to 200 percent wants 360 high

        // Not focused: the legacy limits stay (no height override at all).
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, 400d);
        Assert.True(double.IsNaN(frame.MaximumHeightOverride));

        view.SetValue(ReaderPostLayout.FocusScalePercentProperty, 200);
        view.SetValue(ReaderPostLayout.FocusedProperty, true);
        double visible = view.VisibleFrameHeight();
        Assert.True(double.IsFinite(visible) && visible < 400 && visible > 180, $"visible {visible}");
        Assert.Equal(visible, frame.MaximumHeightOverride, Tolerance);
        Assert.Equal(480, frame.MaximumWidthOverride); // the width stays at the percentage; the column limits it through the measure

        // A taller reader leaves the wanted 360; a resize re-applies the cap to the same view.
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, 2000d);
        Assert.Equal(360, frame.MaximumHeightOverride, Tolerance);
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, 350d);
        Assert.Equal(view.VisibleFrameHeight(), frame.MaximumHeightOverride, Tolerance);

        // A viewport that cannot hold the header keeps the ordinary picture (floor) rather than collapsing it.
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, 60d);
        Assert.Equal(180, frame.MaximumHeightOverride, Tolerance);

        // Unknown height: no cap beyond the percentage.
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, double.NaN);
        Assert.Equal(360, frame.MaximumHeightOverride, Tolerance);

        // Focus gone: back to the legacy limits.
        view.SetValue(ReaderPostLayout.FocusedProperty, false);
        Assert.True(double.IsNaN(frame.MaximumHeightOverride));
    });

    [Fact]
    public void EnlargedPortraitFrameFitsTheVisibleHeightWhenMeasured() => OnPlatform(() =>
    {
        ArchiveAttachmentView view = new()
        {
            DataContext = new ArchiveAttachmentRow(new CapturedAttachment(0, "m-portrait", OriginalFileName: "tall.png", ExpectedSize: 1000))
        };
        MediaFramePanel frame = view.GetLogicalDescendants().OfType<MediaFramePanel>().Single();
        frame.Aspect = 0.5;
        view.SetValue(ReaderPostLayout.VisibleHeightProperty, 300d);
        view.SetValue(ReaderPostLayout.FocusScalePercentProperty, 300);
        view.SetValue(ReaderPostLayout.FocusedProperty, true);
        double visible = view.VisibleFrameHeight();
        frame.Measure(new Size(1000, double.PositiveInfinity));
        Assert.True(frame.DesiredSize.Height <= visible + Tolerance, $"{frame.DesiredSize.Height} > {visible}");
        Assert.Equal(0.5, frame.DesiredSize.Width / frame.DesiredSize.Height, 0.01); // layout rounding to whole pixels
    });
}
