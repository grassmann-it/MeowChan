using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// P5 part A: reader values that used to be fixed constants are clamped to Default/Min/Max, and spoilers follow their mode.
public sealed class ReaderDisplayLimitsTests
{
    [Fact]
    public void DefaultsAreTodaysFixedValues()
    {
        Assert.Equal(24, ReaderDisplayLimits.FoldLinesDefault);
        Assert.Equal(18, ReaderDisplayLimits.FoldVisibleLines(ReaderDisplayLimits.FoldLinesDefault));
        Assert.Equal(8, ReaderDisplayLimits.PreviewLinesDefault);
        Assert.Equal(420, ReaderDisplayLimits.PreviewCharacters(ReaderDisplayLimits.PreviewLinesDefault));
        Assert.Equal(300, ReaderDisplayLimits.PreviewDelayDefault);
        Assert.Equal(5, ReaderDisplayLimits.ReplyStepDefault);
        Assert.Equal(4, ReaderDisplayLimits.ReplyLinesDefault);
        Assert.Equal(260, ReaderDisplayLimits.ReplyCharacters(ReaderDisplayLimits.ReplyLinesDefault));
    }

    public static TheoryData<string, Func<int, int>, int, int, int> Ranges => new()
    {
        { "fold", ReaderDisplayLimits.ClampFoldLines, 8, 24, 400 },
        { "preview lines", ReaderDisplayLimits.ClampPreviewLines, 3, 8, 40 },
        { "preview delay", ReaderDisplayLimits.ClampPreviewDelay, 0, 300, 2000 },
        { "reply step", ReaderDisplayLimits.ClampReplyStep, 1, 5, 50 },
        { "reply lines", ReaderDisplayLimits.ClampReplyLines, 1, 4, 20 },
        { "gallery upscale percent", GalleryFitPreferences.ClampPercent, 100, 200, 1600 },
    };

    [Theory]
    [MemberData(nameof(Ranges))]
    public void EveryValueClampsInclusivelyToItsRange(string name, Func<int, int> clamp, int minimum, int standard, int maximum)
    {
        Assert.Equal(minimum, clamp(minimum));
        Assert.Equal(maximum, clamp(maximum));
        Assert.Equal(standard, clamp(standard));
        Assert.Equal(minimum, clamp(minimum - 1));
        Assert.Equal(maximum, clamp(maximum + 1));
        Assert.Equal(minimum, clamp(int.MinValue));
        Assert.Equal(maximum, clamp(int.MaxValue));
        Assert.True(minimum <= standard && standard <= maximum, name);
    }

    [Fact]
    public void ConstantsMatchTheClampedRanges()
    {
        Assert.Equal((8, 400), (ReaderDisplayLimits.FoldLinesMinimum, ReaderDisplayLimits.FoldLinesMaximum));
        Assert.Equal((3, 40), (ReaderDisplayLimits.PreviewLinesMinimum, ReaderDisplayLimits.PreviewLinesMaximum));
        Assert.Equal((0, 2000), (ReaderDisplayLimits.PreviewDelayMinimum, ReaderDisplayLimits.PreviewDelayMaximum));
        Assert.Equal((1, 50), (ReaderDisplayLimits.ReplyStepMinimum, ReaderDisplayLimits.ReplyStepMaximum));
        Assert.Equal((1, 20), (ReaderDisplayLimits.ReplyLinesMinimum, ReaderDisplayLimits.ReplyLinesMaximum));
        Assert.Equal((100, 1600), (GalleryFitPreferences.MaxUpscalePercentMinimum, GalleryFitPreferences.MaxUpscalePercentMaximum));
        Assert.Equal(200, GalleryFitPreferences.MaxUpscalePercentDefault);
    }

    [Theory]
    [InlineData(8, 6)]
    [InlineData(9, 7)]   // 6.75
    [InlineData(10, 8)]  // 7.5 rounds away from zero
    [InlineData(24, 18)]
    [InlineData(25, 19)] // 18.75
    [InlineData(400, 300)]
    [InlineData(1, 6)]   // clamped to 8 first
    [InlineData(5000, 300)]
    public void FoldShowsThreeQuartersOfTheThresholdRounded(int threshold, int visible)
    {
        Assert.Equal(visible, ReaderDisplayLimits.FoldVisibleLines(threshold));
        int clamped = ReaderDisplayLimits.ClampFoldLines(threshold);
        Assert.True(ReaderDisplayLimits.FoldVisibleLines(threshold) < clamped);
    }

    [Theory]
    [InlineData(3, 158)]  // 157.5
    [InlineData(8, 420)]
    [InlineData(40, 2100)]
    [InlineData(0, 158)]
    [InlineData(99, 2100)]
    public void PreviewCharactersGrowWithTheLines(int lines, int characters) =>
        Assert.Equal(characters, ReaderDisplayLimits.PreviewCharacters(lines));

    [Theory]
    [InlineData(1, 65)]
    [InlineData(4, 260)]
    [InlineData(20, 1300)]
    [InlineData(0, 65)]
    [InlineData(50, 1300)]
    public void ReplyCharactersGrowWithTheLines(int lines, int characters) =>
        Assert.Equal(characters, ReaderDisplayLimits.ReplyCharacters(lines));

    [Fact]
    public void QuotePreviewShortenerFollowsAGivenLineLimitWithinTheRange()
    {
        string text = string.Join("\n", Enumerable.Range(1, 60).Select(n => "line " + n));
        Assert.Equal(3, ArchivePage.ShortenQuotePreview(text, 1).Split('\n').Length);   // clamped up to the minimum
        Assert.Equal(12, ArchivePage.ShortenQuotePreview(text, 12).Split('\n').Length);
        Assert.Equal(40, ArchivePage.ShortenQuotePreview(text, 500).Split('\n').Length); // clamped down to the maximum
        string longLine = new('x', 3000);
        Assert.Equal(ReaderDisplayLimits.PreviewCharacters(12) + 1, ArchivePage.ShortenQuotePreview(longLine, 12).Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(-1, 0)]
    public void AnUndefinedSpoilerModeFallsBackToHide(int mode, int expected) =>
        Assert.Equal((ReaderSpoilerMode)expected, ReaderDisplayLimits.ClampSpoilerMode((ReaderSpoilerMode)mode));

    [Theory]
    // mode, revealed for good, pointer over the body -> painted readable
    [InlineData(0, false, false, false)]
    [InlineData(0, false, true, false)]   // hovering does nothing in hide mode
    [InlineData(0, true, false, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, false, true, true)]
    [InlineData(1, true, false, true)]   // a click keeps it shown after the pointer left
    [InlineData(2, false, false, true)]
    [InlineData(2, false, true, true)]
    public void SpoilersAreShownByRevealModeAndHover(int mode, bool revealed, bool hovered, bool shown) =>
        Assert.Equal(shown, ReaderSpoilerPolicy.Shown((ReaderSpoilerMode)mode, revealed, hovered));

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]   // the click makes the hover reveal permanent
    [InlineData(2, false, false)] // nothing to reveal
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    public void ClickingASpoilerRevealsItForGoodOnlyWhenThereIsSomethingToReveal(int mode, bool revealed, bool reveals) =>
        Assert.Equal(reveals, ReaderSpoilerPolicy.ClickReveals((ReaderSpoilerMode)mode, revealed));

    [Fact]
    public void AccessibleTextHidesTheSpoilerWordsExactlyWhileTheyAreNotShown()
    {
        ReaderBodyDocument document = ReaderBodyFormatting.Format("a secret plan b", [], [new(ArchiveTextMarkKind.Spoiler, 2, 11)]);
        Assert.True(document.HasSpoilers);
        string hidden = ReaderSpoilerPolicy.AccessibleText(document, shown: false, "Spoiler, hidden");
        Assert.Equal("a Spoiler, hidden b", hidden); // one placeholder for the whole run
        Assert.DoesNotContain("secret", hidden);
        Assert.Equal("a secret plan b", ReaderSpoilerPolicy.AccessibleText(document, shown: true, "Spoiler, hidden"));
        ReaderBodyDocument plain = ReaderBodyFormatting.Format("no spoiler here", [], []);
        Assert.Equal("no spoiler here", ReaderSpoilerPolicy.AccessibleText(plain, shown: false, "Spoiler, hidden"));

        // The mode decides: hover/always modes expose the words while the spoilers are shown, hide mode never before a reveal.
        string ForState(ReaderSpoilerMode mode, bool revealed, bool hovered) =>
            ReaderSpoilerPolicy.AccessibleText(document, ReaderSpoilerPolicy.Shown(mode, revealed, hovered), "Spoiler, hidden");
        Assert.DoesNotContain("secret", ForState(ReaderSpoilerMode.Hide, false, true));
        Assert.DoesNotContain("secret", ForState(ReaderSpoilerMode.Hover, false, false));
        Assert.Contains("secret", ForState(ReaderSpoilerMode.Hover, false, true));
        Assert.Contains("secret", ForState(ReaderSpoilerMode.Always, false, false));
        Assert.Contains("secret", ForState(ReaderSpoilerMode.Hide, true, false));
    }
}
