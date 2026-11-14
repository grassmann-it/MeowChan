using System.Text.Json;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The media sidebar of the large reader (R8): settings, when it is shown, which medium it presents, its pacing and its texts.</summary>
[Collection("Localization")]
public sealed class ReaderMediaSidebarTests : IDisposable
{
    public ReaderMediaSidebarTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ReaderMediaPreferences On(int minimum = 1280, int width = 480, bool right = false) =>
        ReaderMediaPreferences.Default with { Sidebar = true, SidebarMinimumReaderWidth = minimum, SidebarWidth = width, SidebarRight = right };

    [Fact]
    public void TheSidebarIsOffByDefaultOnTheLeftWithItsDefaultWidths()
    {
        Config config = new();
        Assert.False(config.ReaderMediaSidebar);
        Assert.Equal("Left", config.ReaderMediaSidebarSide);
        Assert.Equal(480, config.ReaderMediaSidebarWidth);
        Assert.Equal(1280, config.ReaderMediaSidebarMinimumWidth);
        ReaderMediaPreferences media = ReaderMediaPreferences.From(config);
        Assert.False(media.Sidebar);
        Assert.False(media.SidebarRight);
        Assert.Equal(ReaderMediaPreferences.Default, media);
        Assert.False(ReaderMediaSidebarRules.IsShown(media, largeReader: true, 1920));
    }

    [Fact]
    public void CloneAndBothCopiesKeepEverySidebarValue()
    {
        Config source = new() { ReaderMediaSidebar = true, ReaderMediaSidebarSide = "Right", ReaderMediaSidebarWidth = 700, ReaderMediaSidebarMinimumWidth = 1600 };
        Config clone = source.Clone(), copy = new(), settings = new();
        copy.CopyFrom(source);
        settings.CopySettingsFieldsFrom(source);
        foreach (Config config in new[] { clone, copy, settings })
        {
            ReaderMediaPreferences media = ReaderMediaPreferences.From(config);
            Assert.True(media.Sidebar);
            Assert.True(media.SidebarRight);
            Assert.Equal(700, media.SidebarWidth);
            Assert.Equal(1600, media.SidebarMinimumReaderWidth);
        }
    }

    [Theory]
    [InlineData("Right", "Right")]
    [InlineData(" right ", "Right")]
    [InlineData("Left", "Left")]
    [InlineData("middle", "Left")]
    [InlineData("", "Left")]
    public void TheSideIsLeftUnlessItIsRight(string stored, string expected)
    {
        Assert.Equal(expected, new Config { ReaderMediaSidebarSide = stored }.ReaderMediaSidebarSide);
        Config loaded = JsonSerializer.Deserialize<Config>("{\"ReaderMediaSidebarSide\":" + JsonSerializer.Serialize(stored) + "}")!;
        Assert.Equal(expected, loaded.ReaderMediaSidebarSide);
    }

    [Fact]
    public void OutOfRangeWidthsAreClampedAndRejectedByValidation()
    {
        ReaderMediaPreferences clamped = (ReaderMediaPreferences.Default with { SidebarWidth = 1, SidebarMinimumReaderWidth = 99999 }).Clamped();
        Assert.Equal(ReaderDisplayPreferences.MediaSidebarWidthMinimum, clamped.SidebarWidth);
        Assert.Equal(ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum, clamped.SidebarMinimumReaderWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaSidebarWidth = 239 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaSidebarWidth = 1201 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaSidebarMinimumWidth = 799 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { ReaderMediaSidebarMinimumWidth = 3841 }.Validate());
    }

    [Fact]
    public void TheSidebarIsTheFocusedMediaSurfaceWhileItIsShown()
    {
        ReaderMediaPreferences focus = ReaderMediaPreferences.Default with { FocusedEnlarge = true };
        Assert.Equal(ReaderFocusedMedia.Presentation.Inline, ReaderFocusedMedia.Choose(focus, presentedElsewhere: false));
        Assert.Equal(ReaderFocusedMedia.Presentation.Sidebar, ReaderFocusedMedia.Choose(focus, presentedElsewhere: true));
        Assert.Equal(ReaderFocusedMedia.Presentation.Sidebar, ReaderFocusedMedia.Choose(ReaderMediaPreferences.Default, presentedElsewhere: true));
    }

    [Theory]
    [InlineData(false, true, 1920, false)]   // not chosen
    [InlineData(true, false, 1920, false)]   // the embedded reader has no sidebar
    [InlineData(true, true, 1279, false)]    // narrower than the setting: gives way to the posts
    [InlineData(true, true, 1280, true)]
    [InlineData(true, true, 0, true)]        // not laid out yet: no second layout pass on opening
    public void ItIsShownOnlyWhenChosenInTheLargeReaderAndWideEnough(bool chosen, bool large, double width, bool shown)
    {
        ReaderMediaPreferences media = chosen ? On() : ReaderMediaPreferences.Default;
        Assert.Equal(shown, ReaderMediaSidebarRules.IsShown(media, large, width));
        Assert.Equal(chosen && width is > 0 and < 1280, ReaderMediaSidebarRules.IsCollapsedByWidth(media, width));
    }

    [Fact]
    public void TheWidthLeavesThePostsTheirMinimumAndDraggedWidthsAreClamped()
    {
        Assert.Equal(480, ReaderMediaSidebarRules.EffectiveWidth(480, 1920));
        Assert.Equal(480, ReaderMediaSidebarRules.EffectiveWidth(480, 0));
        Assert.Equal(1300 - ReaderMediaSidebarRules.SplitterWidth - ReaderMediaSidebarRules.PostsMinimumWidth, ReaderMediaSidebarRules.EffectiveWidth(1200, 1300));
        Assert.Equal(ReaderDisplayPreferences.MediaSidebarWidthMinimum, ReaderMediaSidebarRules.EffectiveWidth(1200, 500));
        Assert.Equal(240, ReaderMediaSidebarRules.ClampWidth(12));
        Assert.Equal(1200, ReaderMediaSidebarRules.ClampWidth(5000));
        Assert.Equal(613, ReaderMediaSidebarRules.ClampWidth(612.6));
        Assert.Equal(480, ReaderMediaSidebarRules.ClampWidth(double.NaN));
    }

    [Theory]
    [InlineData(0, 3, 1, 1)]
    [InlineData(2, 3, 1, 2)]     // no wrap-around at the end
    [InlineData(0, 3, -1, 0)]
    [InlineData(5, 3, -1, 1)]    // a stale index is first brought back into the post
    [InlineData(0, 0, 1, 0)]
    public void StepsStayWithinTheFocusedPostsMedia(int index, int count, int delta, int expected) =>
        Assert.Equal(expected, ReaderMediaSidebarRules.Step(index, count, delta));

    [Fact]
    public void OnlyAPostWithSeveralMediaCanStepAndOnlyAwayFromItsEnds()
    {
        Assert.False(ReaderMediaSidebarRules.CanStep(0, 1, 1));
        Assert.False(ReaderMediaSidebarRules.CanStep(0, 3, -1));
        Assert.True(ReaderMediaSidebarRules.CanStep(0, 3, 1));
        Assert.True(ReaderMediaSidebarRules.CanStep(2, 3, -1));
        Assert.False(ReaderMediaSidebarRules.CanStep(2, 3, 1));
    }

    [Fact]
    public void HeldArrowKeysAreFollowedAtOnceAndThenAtMostOncePerInterval()
    {
        ReaderMediaFollowThrottle throttle = new(120);
        Assert.True(throttle.Request(1000));          // the first change shows at once
        Assert.False(throttle.Request(1030));         // key repeat: waits
        Assert.False(throttle.Request(1060));
        Assert.True(throttle.Pending);
        Assert.Equal(60, throttle.Remaining(1060));
        Assert.True(throttle.Tick(1120));             // only the latest waiting change is shown
        Assert.False(throttle.Pending);
        Assert.False(throttle.Tick(1240));            // nothing waited
        Assert.True(throttle.Request(1400));          // after a quiet moment: at once again
        throttle.Reset();
        Assert.True(throttle.Request(1401));
    }

    [Fact]
    public void ADecodeIsRepeatedOnlyForAClearlyLargerFrame()
    {
        Assert.Equal((960d, 1440d), ReaderMediaSidebarRules.PhysicalTarget(new Size(480, 720), 2));
        Assert.Equal((1d, 1d), ReaderMediaSidebarRules.PhysicalTarget(new Size(0, 0), double.NaN));
        PixelSize shown = new(900, 600);
        Assert.False(ReaderMediaSidebarRules.NeedsLargerDecode(shown, (950, 640), null));
        Assert.True(ReaderMediaSidebarRules.NeedsLargerDecode(shown, (1400, 1000), null));
        Assert.False(ReaderMediaSidebarRules.NeedsLargerDecode(shown, (1400, 1000), new PixelSize(900, 600))); // already the original size
        Assert.True(ReaderMediaSidebarRules.NeedsLargerDecode(new PixelSize(0, 0), (10, 10), null));
    }

    [Fact]
    public void ThePictureFollowsTheGalleryFitLimit()
    {
        Assert.Equal(double.PositiveInfinity, ReaderMediaSidebarRules.MaximumDisplay(300, new GalleryFitPreferences()));
        Assert.Equal(600, ReaderMediaSidebarRules.MaximumDisplay(300, new GalleryFitPreferences(true, 200)));
        Assert.Equal(300, ReaderMediaSidebarRules.MaximumDisplay(300, new GalleryFitPreferences(true, 10)));
    }

    [Fact]
    public void TheRegionNamesThePostTheMediumAndItsFile()
    {
        CapturedAttachmentText file = new("cat.jpg", 1258291);
        Assert.Equal("cat.jpg, " + SizeText.Format(1258291), ReaderMediaSidebarRules.FileText(file));
        Assert.Equal("Media of post No. 124, 2 of 3, cat.jpg, " + SizeText.Format(1258291), ReaderMediaSidebarRules.RegionText("124", 1, 3, file));
        Assert.Equal("cat.jpg", ReaderMediaSidebarRules.FileText(new CapturedAttachmentText("cat.jpg", null)));
        Assert.Equal(SizeText.Format(2048), ReaderMediaSidebarRules.FileText(new CapturedAttachmentText(string.Empty, 2048)));
        Assert.Contains("MiB", SizeText.Format(1258291));
    }

    [Fact]
    public void TheThreeCommandsAreReaderCommandsWithYieldingDefaults()
    {
        foreach ((string id, string gesture) in new[] { ("reader.sidebar.toggle", "Primary+Shift+M"),
            ("reader.sidebar.previous-media", "Alt+Shift+Left"), ("reader.sidebar.next-media", "Alt+Shift+Right") })
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.Equal(gesture, command.Shortcut);
            Assert.True(command.Scope.HasFlag(CommandScope.Reader));
            Assert.True(command.IsImplemented);
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
                Assert.NotEmpty(EffectiveShortcutBindings.Build(null, platform).For(id));
        }
        Assert.Contains("reader-view-menu", CommandCatalog.Get("reader.sidebar.toggle").Contexts);
    }
}
