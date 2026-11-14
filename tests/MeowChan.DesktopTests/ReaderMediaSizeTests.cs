using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ReaderMediaSizeTests
{
    [Fact]
    public void DefaultsTurnLargerImagesOnAndLeaveFocusAndHoverOff()
    {
        Config config = new();
        Assert.True(config.ReaderTextlessLargeImages);
        Assert.False(config.ReaderFocusedMediaEnlarge);
        Assert.False(config.ReaderHoverEnlarge);
        Assert.Equal(ReaderMediaPreferences.Default, ReaderMediaPreferences.From(config));
        Assert.Equal(10, config.ReadingHistoryLimit);
        Assert.Equal(new ReaderDisplayPreferences().Media, ReaderMediaPreferences.Default);
    }

    [Fact]
    public void ConfigCloneAndCopyKeepEveryMediaValue()
    {
        Config source = new()
        {
            ReaderTextlessLargeImages = false, ReaderTextlessMediaWidth = 500, ReaderFocusedMediaEnlarge = true, ReaderFocusedMediaScalePercent = 250,
            ReaderHoverEnlarge = true, ReaderHoverEnlargeSize = 800, ReaderHoverEnlargeDelayMs = 600, ReadingHistoryLimit = 7
        };
        Config clone = source.Clone();
        Config copy = new();
        copy.CopyFrom(source);
        foreach (Config config in new[] { clone, copy })
        {
            Assert.Equal(ReaderMediaPreferences.From(source), ReaderMediaPreferences.From(config));
            Assert.Equal(7, config.ReadingHistoryLimit);
            Assert.Equal(new ReaderMediaPreferences(false, 500, true, 250, true, 800, 600), ReaderMediaPreferences.From(config));
        }
    }

    [Fact]
    public void OutOfRangeValuesAreClampedAndValidationRejectsThem()
    {
        ReaderMediaPreferences clamped = new ReaderMediaPreferences(true, -1, true, 5000, true, 1, 999999).Clamped();
        Assert.Equal(ReaderDisplayPreferences.TextlessMediaWidthMinimum, clamped.TextlessWidth);
        Assert.Equal(ReaderDisplayPreferences.FocusedMediaScaleMaximum, clamped.FocusedScalePercent);
        Assert.Equal(ReaderDisplayPreferences.HoverEnlargeSizeMinimum, clamped.HoverSize);
        Assert.Equal(ReaderDisplayPreferences.HoverEnlargeDelayMaximum, clamped.HoverDelayMs);
        Assert.Equal(clamped, new ReaderDisplayPreferences { Media = new ReaderMediaPreferences(true, -1, true, 5000, true, 1, 999999) }.Media);

        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReadingHistoryLimit = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReadingHistoryLimit = 26 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderTextlessMediaWidth = 119 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderFocusedMediaScalePercent = 99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderHoverEnlargeSize = 239 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderHoverEnlargeDelayMs = 99 }.Validate());
    }

    [Theory]
    [InlineData(120, false)]
    [InlineData(240, false)]
    [InlineData(400, false)]
    [InlineData(240, true)]
    [InlineData(400, true)]
    public void OrdinaryWidthsDecodeExactlyAsBefore(int configured, bool large)
    {
        int width = ReaderDisplayPreferences.EffectiveThumbnailWidth(configured, large);
        Assert.Equal(ReaderDisplayPreferences.DecodeBounds(configured, large), ReaderDisplayPreferences.DecodeBoundsForWidth(width, large));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WiderPreviewsGrowTheDecodeBoundsWithThem(bool large)
    {
        ReaderDisplayPreferences.ThumbnailDecodeBounds ordinary = ReaderDisplayPreferences.DecodeBoundsForWidth(240, large);
        ReaderDisplayPreferences.ThumbnailDecodeBounds wide = ReaderDisplayPreferences.DecodeBoundsForWidth(1500, large);
        Assert.True(wide.MaximumWidth >= 1500);
        Assert.True(wide.MaximumHeight > ordinary.MaximumHeight);
        Assert.True(ReaderDisplayPreferences.DecodeBoundsForWidth(0, large).MaximumWidth > 0);
    }

    [Fact]
    public void TextlessWidthIsTheOrdinaryWidthWhileLargerImagesAreOff()
    {
        ReaderMediaPreferences off = ReaderMediaPreferences.Default with { TextlessLarge = false, TextlessWidth = 900 };
        Assert.Equal(ReaderDisplayPreferences.EffectiveThumbnailWidth(240, false), ReaderDisplayPreferences.EffectiveTextlessWidth(240, off, false));
        Assert.Equal(ReaderDisplayPreferences.EffectiveThumbnailWidth(240, true), ReaderDisplayPreferences.EffectiveTextlessWidth(240, off, true));
    }

    [Theory]
    [InlineData(360, false, 360)]
    [InlineData(360, true, 540)]            // scaled like the thumbnail in the large reader
    [InlineData(120, false, 240)]           // never narrower than the ordinary width
    [InlineData(960, false, 960)]
    public void TextlessWidthIsLargerButNeverSmallerThanTheOrdinaryOne(int textless, bool large, int expected)
    {
        ReaderMediaPreferences media = ReaderMediaPreferences.Default with { TextlessWidth = textless };
        Assert.Equal(expected, ReaderDisplayPreferences.EffectiveTextlessWidth(240, media, large));
    }

    [Fact]
    public void FocusedScaleIsOneHundredWhileTheOptionIsOffAndClampedWhenOn()
    {
        ReaderMediaPreferences media = ReaderMediaPreferences.Default with { FocusedScalePercent = 300 };
        Assert.Equal(100, ReaderDisplayPreferences.FocusedScalePercent(media));
        Assert.Equal(300, ReaderDisplayPreferences.FocusedScalePercent(media with { FocusedEnlarge = true }));
        Assert.Equal(400, ReaderDisplayPreferences.FocusedScalePercent(media with { FocusedEnlarge = true, FocusedScalePercent = 9000 }));
        Assert.Equal(100, ReaderDisplayPreferences.FocusedScalePercent(media with { FocusedEnlarge = true, FocusedScalePercent = 10 }));
    }

    [Theory]
    [InlineData(false, 200, 1.0)]
    [InlineData(true, 100, 1.0)]
    [InlineData(true, 50, 1.0)]
    [InlineData(true, 200, 2.0)]
    [InlineData(true, 250, 2.5)]
    public void OnlyTheFocusedCardIsEnlarged(bool focused, int percent, double expected) =>
        Assert.Equal(expected, ReaderPostLayout.FocusFactor(focused, percent));

    [Fact]
    public void OnlyTextlessChangesRebuildThePostsImages()
    {
        ReaderMediaPreferences baseline = ReaderMediaPreferences.Default;
        Assert.True(baseline.SamePostImages(baseline with { FocusedEnlarge = true, FocusedScalePercent = 400, HoverEnlarge = true, HoverSize = 900, HoverDelayMs = 200 }));
        Assert.False(baseline.SamePostImages(baseline with { TextlessLarge = false }));
        Assert.False(baseline.SamePostImages(baseline with { TextlessWidth = 500 }));
        ReaderMediaPreferences off = baseline with { TextlessLarge = false };
        Assert.True(off.SamePostImages(off with { TextlessWidth = 800 }));
    }

    [Fact]
    public void ARaisedHeightCapLetsAWiderPreviewKeepItsProportion()
    {
        Avalonia.Size capped = MediaSlotGeometry.Fit(0.5, 700, 700, 340);
        Avalonia.Size raised = MediaSlotGeometry.Fit(0.5, 700, 700, 900);
        Assert.True(raised.Height > capped.Height);
        Assert.True(raised.Width >= capped.Width);
    }
}
