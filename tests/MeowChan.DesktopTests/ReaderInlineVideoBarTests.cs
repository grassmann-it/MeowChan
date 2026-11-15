using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The minimal video control of the reader (a post's video and the media sidebar share it): what it holds, how it lays out, that it is never wider
/// than the picture's frame, and what the sidebar's header and counter say. The panels are measured with fixed-size stand-ins (the real buttons and
/// the slider need the theme); the real control set is checked in the reader-inline-video smoke.
/// </summary>
[Collection("Localization")]
public sealed class ReaderInlineVideoBarTests : IDisposable
{
    public ReaderInlineVideoBarTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private const double TimeWidth = 40;

    private static (InlineVideoBarPanel Bar, Border[] Parts) Bar(bool open = true)
    {
        Border play = new() { Width = 32, Height = 32 }, seek = new() { Height = 16 }, time = new() { Child = new Border { Width = TimeWidth }, Height = 16 },
               mute = new() { Width = 32, Height = 32 }, openButton = new() { Width = 32, Height = 32 };
        InlineVideoBarPanel bar = new(play, seek, time, mute, open ? openButton : null);
        return (bar, new[] { play, seek, time, mute, openButton });
    }

    private static void Layout(Control control, double width, double height = 1000)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, control.DesiredSize.Width, control.DesiredSize.Height));
    }

    // ---- the control set -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBarHoldsPlaySeekTimeMuteAndOnlyWhereOfferedOpenInGallery()
    {
        Assert.Equal(new[] { "GalleryVideoPlayPause", "GalleryVideoSeek", "GalleryVideoTime", "GalleryVideoMute", "ReaderInlineVideoOpen" }, InlineVideoBarLayout.ControlNames);
        var (bar, parts) = Bar(open: true);
        Assert.Equal(5, bar.Children.Count);
        Assert.Equal(new Control[] { bar.Play, bar.Seek, bar.Time, bar.Mute }, new Control[] { parts[0], parts[1], parts[2], parts[3] });
        Assert.Same(parts[4], bar.Open);
        var (plain, _) = Bar(open: false);
        Assert.Equal(4, plain.Children.Count);
        Assert.Null(plain.Open);
        // Nothing of the gallery's transport (speed, repeat, A-B loop, next frame, volume slider) is among the names.
        foreach (string gallery in new[] { "GalleryVideoSpeed", "GalleryVideoLoop", "GalleryVideoVolume", "GalleryVideoFrameStep", "GalleryVideoAbLoop" })
            Assert.DoesNotContain(gallery, InlineVideoBarLayout.ControlNames);
        Assert.Equal(InlineVideoBarLayout.RowHeight, bar.Height);
    }

    // ---- what leaves first when it gets narrow ------------------------------------------------------------------------------------

    [Fact]
    public void ThePartsLeaveInTheOrderTimeOpenSeekMuteAndPlayStays()
    {
        double Hidden(Func<InlineVideoBarLayout.Plan, bool> part)
        {
            for (double width = 600; width >= 0; width--)
                if (!part(InlineVideoBarLayout.Decide(width, TimeWidth, hasOpen: true))) return width;
            return -1;
        }
        double time = Hidden(plan => plan.Time), open = Hidden(plan => plan.Open), seek = Hidden(plan => plan.Seek), mute = Hidden(plan => plan.Mute);
        Assert.True(time > open && open > seek && seek > mute && mute > 0, $"{time} {open} {seek} {mute}");
        // Whatever is shown implies what it needs: the time needs the timeline, "open" and the timeline need mute.
        for (double width = 0; width <= 600; width++)
        {
            var plan = InlineVideoBarLayout.Decide(width, TimeWidth, hasOpen: true);
            if (plan.Time) Assert.True(plan.Seek, width.ToString());
            if (plan.Open) Assert.True(plan.Seek, width.ToString());
            if (plan.Seek) Assert.True(plan.Mute, width.ToString());
        }
        Assert.False(InlineVideoBarLayout.Decide(400, TimeWidth, hasOpen: false).Open);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(159, true)]
    [InlineData(160, false)]
    public void BelowOneHundredSixtyDipTheButtonsAre24WithFourDipGaps(double width, bool compact)
    {
        var plan = InlineVideoBarLayout.Decide(width, TimeWidth, hasOpen: true);
        Assert.Equal(compact, plan.Compact);
        Assert.Equal(compact ? 24 : 32, plan.Button);
        Assert.Equal(compact ? 4 : 8, plan.Gap);
    }

    [Fact]
    public void ASmallPortraitVideoKeepsPlaySeekAndMuteAndATinyOneKeepsPlay()
    {
        var portrait = InlineVideoBarLayout.Decide(100, TimeWidth, hasOpen: true);
        Assert.True(portrait.Seek && portrait.Mute);
        Assert.False(portrait.Time);
        var tiny = InlineVideoBarLayout.Decide(24, TimeWidth, hasOpen: true);
        Assert.False(tiny.Mute || tiny.Seek || tiny.Time || tiny.Open);
        Assert.Equal(24, tiny.Button);
    }

    // ---- as wide as the picture, never wider -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 400)]  // portrait
    [InlineData(320, 400)]  // landscape
    [InlineData(24, 400)]   // tiny
    [InlineData(500, 400)]  // a frame wider than the post gives: the row is not wider than the owner
    [InlineData(240, 240)]
    public void TheRowIsExactlyAsWideAsTheFrame(double frame, double available)
    {
        var (bar, _) = Bar();
        ReaderInlineVideoRow row = new(bar) { FrameWidth = frame };
        Layout(row, available);
        double expected = Math.Min(frame, available);
        Assert.Equal(expected, bar.Bounds.Width, 3);
        Assert.Equal(0, bar.Bounds.X, 3);
        Assert.True(bar.Bounds.Width <= frame + 0.001);
        Assert.Equal(InlineVideoBarLayout.RowHeight, bar.Bounds.Height, 3);
        Assert.Equal(ReaderInlineVideoRow.TransportWidth(frame, available), bar.Bounds.Width, 3);
    }

    [Fact]
    public void WithoutAFrameWidthTheRowTakesItsOwnWidthAsTheSidebarDoes()
    {
        var (bar, _) = Bar();
        ReaderInlineVideoRow row = new(bar);
        Layout(row, 360);
        Assert.Equal(360, bar.Bounds.Width, 3);
        Assert.Equal(360, ReaderInlineVideoRow.TransportWidth(double.NaN, 360));
        Assert.Equal(200, ReaderInlineVideoRow.TransportWidth(200, double.PositiveInfinity));
    }

    [Fact]
    public void TheTimeHidesBeforeAnythingElseWhenTheFrameNarrows()
    {
        var (bar, parts) = Bar();
        ReaderInlineVideoRow row = new(bar) { FrameWidth = 320 };
        Layout(row, 400);
        Assert.True(bar.Current.Time);
        Assert.True(parts[2].Bounds.Width > 0);
        row.FrameWidth = 190;
        Layout(row, 400);
        Assert.False(bar.Current.Time);
        Assert.Equal(0, parts[2].Bounds.Width);
        Assert.True(bar.Current.Seek && bar.Current.Mute && bar.Current.Open);
        Assert.True(parts[1].IsVisible && parts[3].IsVisible && parts[4].IsVisible);
        row.FrameWidth = 100;
        Layout(row, 400);
        Assert.False(bar.Current.Open);
        Assert.False(parts[4].IsVisible);
        Assert.True(bar.Current.Seek && bar.Current.Mute);
        // The play button stays at the left edge and the mute button at the right edge of the bar.
        Assert.Equal(0, parts[0].Bounds.X, 3);
        Assert.Equal(100, parts[3].Bounds.Right, 3);
    }

    [Fact]
    public void ThePartsNeverLeaveTheBarOrOverlapAtAnyWidth()
    {
        for (double width = 24; width <= 480; width += 8) // a frame is never narrower than 24 dip (MediaSlotGeometry.MinimumExtent)
        {
            var (bar, parts) = Bar();
            Layout(bar, width);
            Assert.Equal(width, bar.Bounds.Width, 3);
            Rect previous = default;
            foreach (Border part in new[] { parts[0], parts[1], parts[2], parts[4], parts[3] }.OrderBy(part => part.Bounds.X))
            {
                if (!part.IsVisible || part.Bounds.Width <= 0) continue;
                Assert.True(part.Bounds.X >= -0.001 && part.Bounds.Right <= width + 0.001, $"{width}: {part.Bounds}");
                if (previous.Width > 0) Assert.True(part.Bounds.X >= previous.Right - 0.001, $"{width}: overlap {previous} {part.Bounds}");
                previous = part.Bounds;
            }
        }
    }

    // ---- the sidebar's header and counter -----------------------------------------------------------------------------------------

    [Fact]
    public void TheHeaderIsOneLineOfPostFileAndSizeAndSkipsWhatIsMissing()
    {
        Assert.Equal("No. 75 · cat.jpg · 880 KiB", ReaderMediaSidebarRules.HeaderText("No. 75", "cat.jpg", "880 KiB"));
        Assert.Equal("No. 75 · cat.jpg", ReaderMediaSidebarRules.HeaderText("No. 75", "cat.jpg", ""));
        Assert.Equal("cat.jpg · 880 KiB", ReaderMediaSidebarRules.HeaderText("", "cat.jpg", "880 KiB"));
        Assert.Equal("No. 75", ReaderMediaSidebarRules.HeaderText("No. 75", " ", ""));
        Assert.Equal(string.Empty, ReaderMediaSidebarRules.HeaderText("", "", ""));
    }

    // ---- the hover bug, and the sources that pin the look -------------------------------------------------------------------------------

    private static string Source(string relative, [CallerFilePath] string self = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../..", relative)));

    [Fact]
    public void HoveringAnUnstartedVideoNeverPaintsOverItsPoster()
    {
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        string[] states = { "Button.video-activator /template/", "Button.video-activator:pointerover /template/", "Button.video-activator:pressed /template/", "Button.video-activator:disabled /template/" };
        foreach (string state in states)
        {
            int at = styles.IndexOf(state, StringComparison.Ordinal);
            Assert.True(at >= 0, state);
            string rule = styles[at..styles.IndexOf("</Style>", at, StringComparison.Ordinal)];
            // Transparent in every state: the presenter of Semi's button fills the whole frame flat while hovered otherwise.
            Assert.Contains("Property=\"Background\" Value=\"Transparent\"", rule);
            Assert.DoesNotContain("Workspace.Hover", rule);
            Assert.DoesNotContain("Workspace.Card", rule);
        }
        string slot = Source("Desktop/ReaderInlineVideoSlot.cs");
        Assert.Contains("video-activator", slot);
        // The hover look is a thin ring, not a fill.
        Assert.Contains("Button.video-activator:pointerover /template/ ContentPresenter#PART_ContentPresenter\"><Setter Property=\"Background\" Value=\"Transparent\" /><Setter Property=\"BorderBrush\" Value=\"{DynamicResource Workspace.Accent}\"", styles);
    }

    [Fact]
    public void TheMediaSidebarHasNoOverlayArrowsAndNoCounterOverTheMedium()
    {
        // Tom (15.11.2026): the arrows and the "n / total" counter over the medium are gone for good, in the reader's sidebar and in the Media page's panel.
        string styles = Source("Desktop/WorkspaceStyles.axaml"), sidebar = Source("Desktop/Views/Archive/ReaderMediaSidebar.cs");
        Assert.DoesNotContain("overlay-nav", styles);
        Assert.DoesNotContain("overlay-counter", styles);
        foreach (string gone in new[] { "overlay-nav", "overlay-counter", "ReaderMediaSidebarPrevious", "ReaderMediaSidebarNext", "ReaderMediaSidebarCounter", "ShowsNavigation" })
            Assert.DoesNotContain(gone, sidebar);
        Assert.DoesNotContain("Previous", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
        Assert.DoesNotContain("StepPanel", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
        // The keys stay: Alt+Shift+Left/Right steps through the media of a post in the reader, the strip stays.
        Assert.Contains("ReaderMediaSidebarStrip", sidebar);
        Assert.Contains("reader.sidebar.previous-media", Source("Desktop/Commands/CommandCatalog.cs"));
        Assert.Contains("reader.sidebar.next-media", Source("Desktop/Commands/CommandCatalog.cs"));
    }

    [Fact]
    public void ThePlayerOfAPostHasNoGhostFileNameAndOnlyTheMinimalBar()
    {
        string slot = Source("Desktop/ReaderInlineVideoSlot.cs"), inline = Source("Desktop/GalleryVideoPlayer.Inline.cs");
        Assert.Contains("UseMinimalTransport(open)", slot);
        Assert.Contains("ReaderInlineVideoBar", inline);
        Assert.Contains("--no-video-title-show", Source("Desktop/GalleryVideoPlayer.cs"));
        // The bar replaces the gallery's transport: the old status line (and its filename) is not part of it.
        Assert.DoesNotContain("_status", inline.Replace("// _status", string.Empty));
    }
}
