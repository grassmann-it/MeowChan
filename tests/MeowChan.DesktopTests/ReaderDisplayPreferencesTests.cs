using System;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderDisplayPreferencesTests
{
    [Fact]
    public void DefaultsPreserveTheExistingReaderGeometryAndSystemDate()
    {
        ReaderDisplayPreferences preferences = new();

        Assert.Equal(240, preferences.ReaderThumbnailWidth);
        Assert.Equal(ReaderDateStyle.System, preferences.ReaderDateStyle);
        Assert.Equal(268, ReaderDisplayPreferences.EffectiveThumbnailWidth(preferences.ReaderThumbnailWidth, false) + 28);
        Assert.Equal(new(240, 180), ReaderDisplayPreferences.DecodeBounds(preferences.ReaderThumbnailWidth, false));
        Assert.Equal(new(1040, 720), ReaderDisplayPreferences.DecodeBounds(preferences.ReaderThumbnailWidth, true));
    }

    [Theory]
    [InlineData(int.MinValue, 120)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    [InlineData(480, 480)]
    [InlineData(int.MaxValue, 480)]
    public void ThumbnailWidthIsClampedAtItsPreferenceBoundary(int input, int expected)
    {
        ReaderDisplayPreferences preferences = new() { ReaderThumbnailWidth = input };

        Assert.Equal(expected, preferences.ReaderThumbnailWidth);
        Assert.Equal(expected, ReaderDisplayPreferences.EffectiveThumbnailWidth(preferences.ReaderThumbnailWidth, false));
        Assert.Equal(expected * 3 / 2, ReaderDisplayPreferences.EffectiveThumbnailWidth(preferences.ReaderThumbnailWidth, true));
    }

    [Fact]
    public void NormalDecodeBoundsGrowWithConfiguredWidthButKeepThreeQuarterHeight()
    {
        Assert.Equal(new(480, 360), ReaderDisplayPreferences.DecodeBounds(480, false));
        // A smaller preference does not undercut the existing decoder floor.
        Assert.Equal(new(240, 180), ReaderDisplayPreferences.DecodeBounds(120, false));
    }

    [Theory]
    [InlineData(120, false, 480)]
    [InlineData(240, false, 600)]
    [InlineData(480, false, 840)]
    [InlineData(120, true, 580)]
    [InlineData(240, true, 760)]
    [InlineData(480, true, 1120)]
    public void ConfiguredPreviewWidthsGrowTheSlotAndKeepTheLegacyBesideThreshold(int configured, bool large, int threshold)
    {
        int width = ReaderDisplayPreferences.EffectiveThumbnailWidth(configured, large);
        foreach (bool beside in new[] { false, true })
        {
            ReaderPostLayout layout = new();
            layout.SetValue(ReaderPostLayout.ThumbnailWidthProperty, configured);
            layout.SetValue(ReaderPostLayout.LargeReaderProperty, large);
            Control media = new MeasuredControl(width, 100);
            Control body = new MeasuredControl(150, 50);
            layout.Children.Add(media);
            layout.Children.Add(body);
            Size available = new(threshold - (beside ? 0 : 1), 1000);
            layout.Measure(available);
            layout.Arrange(new Rect(available));
            Assert.Equal(width, media.Bounds.Width);
            Assert.Equal(beside ? 0d : 108d, body.Bounds.Y);
            Assert.Equal(beside ? width + 12d : 0d, body.Bounds.X);
        }
    }

    // No fonts, native host or fixed Width/Height alignment: only the real panel's measure/arrange math.
    private sealed class MeasuredControl(double width, double height) : Control
    {
        protected override Size MeasureOverride(Size availableSize) =>
            new(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    [Fact]
    public void LoadedPortraitGeometryIsNotReducedToThePlaceholderRatio()
    {
        Assert.Equal(135, ArchiveAttachmentView.PreviewHeight(240, 180, null));
        Assert.Equal(180, ArchiveAttachmentView.PreviewHeight(240, 180, new PixelSize(2, 3)));
        Assert.Equal(135, ArchiveAttachmentView.PreviewHeight(240, 180, new PixelSize(16, 9)));
        Assert.Equal(280, ArchiveAttachmentView.PreviewHeight(360, 280, new PixelSize(2, 3)));
    }

    [Fact]
    public void DateCatalogKeepsEveryRequiredPlaceholder()
    {
        Assert.Equal(new[]
        {
            "reader.date.justNow", "reader.date.ago", "reader.date.sinceOp", "reader.date.unit.d",
            "reader.date.unit.h", "reader.date.unit.min", "reader.date.unit.s"
        }, ReaderDateCatalog.Entries.Select(entry => entry.Key));
        Assert.All(ReaderDateCatalog.Entries.Where(entry => entry.Key != "reader.date.justNow"), entry =>
            Assert.Contains("{0}", entry.English, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 0, "reader.date.unit.s")]
    [InlineData(59, 59, "reader.date.unit.s")]
    [InlineData(60, 1, "reader.date.unit.min")]
    [InlineData(3600, 1, "reader.date.unit.h")]
    [InlineData(86400, 1, "reader.date.unit.d")]
    public void RelativeDateUsesTheLargestWholeUnit(int seconds, long value, string unit)
    {
        Assert.Equal((value, unit), ReaderDateText.DurationParts(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void MissingDatesStayEmptyAndSystemStyleRemainsTheOldFormat()
    {
        Assert.Equal(string.Empty, ReaderDateText.Posted(null, ReaderDateStyle.RelativeToNow));
        DateTimeOffset posted = new(2026, 11, 13, 12, 34, 56, TimeSpan.Zero);
        Assert.Equal(posted.ToLocalTime().ToString("g", Texts.Culture), ReaderDateText.Posted(posted, ReaderDateStyle.System));
        Assert.Equal(posted.ToLocalTime().ToString("F", Texts.Culture), ReaderDateText.PostedFull(posted));
    }
}
