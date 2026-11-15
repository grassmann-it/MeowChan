using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// The focused-post enlargement changes only the picture (review 2026-11-15, A4): the text stays beside the media like on the
// neighbouring cards, and the factor is bounded by what the card's width leaves beside the body.
[Collection("Booru exact navigation")]
public sealed class ReaderPostLayoutTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void FactorIsBoundedByTheRoomBesideTheBody()
    {
        // Large reader: 360 dip thumbnail, 384 dip body room, 16 dip media padding.
        double bounded = ReaderPostLayout.BoundedFocusFactor(2, 360, 1120, largeReader: true);
        Assert.True(bounded <= (1120d - 384 - 12) / 360 + Tolerance, $"factor {bounded}");
        Assert.Equal((1120d - 384 - 16) / 360, bounded, Tolerance);
        Assert.Equal((1000d - 384 - 16) / 360, ReaderPostLayout.BoundedFocusFactor(3, 360, 1000, largeReader: true), Tolerance);
        // A roomy card keeps the configured factor; the factor never goes below 1.
        Assert.Equal(1.5, ReaderPostLayout.BoundedFocusFactor(1.5, 360, 1600, largeReader: true), Tolerance);
        Assert.Equal(1, ReaderPostLayout.BoundedFocusFactor(2, 360, 760, largeReader: true), Tolerance);
        // Ordinary reader: 240 dip thumbnail, 332 dip body room, 28 dip media padding.
        Assert.Equal((900d - 332 - 28) / 240, ReaderPostLayout.BoundedFocusFactor(4, 240, 900, largeReader: false), Tolerance);
    }

    [Fact]
    public void ACardTooNarrowForTextBesideKeepsTheFactorAndAnUnenlargedCardIsUntouched()
    {
        Assert.Equal(2, ReaderPostLayout.BoundedFocusFactor(2, 360, 700, largeReader: true), Tolerance);
        Assert.Equal(2, ReaderPostLayout.BoundedFocusFactor(2, 360, double.PositiveInfinity, largeReader: true), Tolerance);
        Assert.Equal(1, ReaderPostLayout.BoundedFocusFactor(1, 360, 1120, largeReader: true), Tolerance);
    }

    [Fact]
    public void EnlargedFocusedCardKeepsItsTextBesideTheMedia() => BooruComponentDispatcher.Run(() =>
    {
        (ReaderPostLayout layout, Border media, Border body) = Build();
        layout.SetValue(ReaderPostLayout.FocusScalePercentProperty, 200);
        Measure(layout, 1098);
        Assert.True(body.Bounds.X > 0 && body.Bounds.Y == 0, "unfocused card: text beside the media");
        double ordinaryMedia = media.DesiredSize.Width;
        Assert.Equal(360 + 16, ordinaryMedia, Tolerance);

        layout.SetValue(ReaderPostLayout.FocusedProperty, true);
        Measure(layout, 1098);
        Assert.True(body.Bounds.X > 0 && body.Bounds.Y == 0, "focused card: text still beside the media");
        // The media grew, but only as far as the body keeps its room (1098 - 384 = 714 dip for media and padding).
        Assert.True(media.DesiredSize.Width > ordinaryMedia, $"media {media.DesiredSize.Width}");
        Assert.Equal(1098 - 384, media.DesiredSize.Width, Tolerance);
        Assert.True(body.Bounds.Width >= 384 - 12 - Tolerance, $"body {body.Bounds.Width}");

        // A roomy card gets the full configured factor.
        Measure(layout, 1600);
        Assert.Equal(360 * 2 + 16, media.DesiredSize.Width, Tolerance);
        Assert.True(body.Bounds.X > 0 && body.Bounds.Y == 0);
    });

    private static (ReaderPostLayout Layout, Border Media, Border Body) Build()
    {
        // The media child takes all the width it is offered (as an enlarged picture would), the body is a fixed-height block.
        Border media = new() { Width = 100_000, Height = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Border body = new() { Height = 40, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        ReaderPostLayout layout = new() { Children = { media, body } };
        layout.SetValue(ReaderPostLayout.LargeReaderProperty, true);
        return (layout, media, body);
    }

    private static void Measure(ReaderPostLayout layout, double width)
    {
        layout.Measure(new Size(width, double.PositiveInfinity));
        layout.Arrange(new Rect(0, 0, width, layout.DesiredSize.Height));
    }
}
