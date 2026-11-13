using System;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Avalonia;

namespace MeowChan.DesktopTests;

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
