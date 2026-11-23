using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// V2 (PLAN-MEDIEN-VIEWER-UND-SEITE §1.1): the gallery side panel host. Segment switching, width clamping and the focus order of the
// control itself (controls without an application), and the source contracts of its hooks in the viewer and the shell.
[Collection("Localization")]
public sealed class GallerySidePanelTests : IDisposable
{
    public GallerySidePanelTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static GallerySidePanel Panel(out Button close)
    {
        close = new Button { Name = "GalleryReturnImage" };
        return new GallerySidePanel(close) { PostContent = new StackPanel { Children = { new Button { Name = "PostAction" } } },
            TagContent = new StackPanel { Children = { new TextBox { Name = "TagField" } } } };
    }

    [Theory]
    [InlineData(140)]
    [InlineData(148)]
    public void NarrowHeaderLeavesTheTagContentBelowTheTabs(int width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.SetLanguage("fr");
        GallerySidePanel panel = Panel(out Button close);
        panel.Width = width;
        close.Width = close.Height = 32;
        panel.Segment = GallerySidePanelSegment.Tags;
        panel.Position.Show(new(1, 2, false));
        Window window = new() { Width = 360, Height = 240, Content = panel };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Control segment = (Control)panel.PostButton.Parent!;
            Assert.Equal(panel.PostButton.Bounds.Top, panel.TagsButton.Bounds.Top);
            Assert.True(segment.Bounds.Top + panel.TagsButton.Bounds.Bottom <= panel.TagSlot.Bounds.Top);
            Assert.Equal(0, segment.Bounds.Left, 0.5);
            Assert.Equal(segment.Bounds.Right + 8, panel.Position.Bounds.Left, 0.5);
            Assert.Equal(close.Bounds.Left - 8, panel.Position.Bounds.Right, 0.5);
            Assert.Equal(close.Bounds.Center.Y, panel.Position.Bounds.Center.Y, 0.5);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- segment ----

    [Fact]
    public void ExactlyOneContentIsVisibleAndTheSegmentShowsWhichOne()
    {
        GallerySidePanel panel = Panel(out _);
        Assert.Equal(GallerySidePanelSegment.Post, panel.Segment);
        Assert.True(panel.PostSlot.IsVisible);
        Assert.False(panel.TagSlot.IsVisible);
        Assert.True(panel.PostButton.IsChecked);
        Assert.False(panel.TagsButton.IsChecked);

        Control post = panel.PostContent!, tags = panel.TagContent!;
        panel.Segment = GallerySidePanelSegment.Tags;
        Assert.False(panel.PostSlot.IsVisible);
        Assert.True(panel.TagSlot.IsVisible);
        Assert.False(panel.PostButton.IsChecked);
        Assert.True(panel.TagsButton.IsChecked);
        // Switching only changes visibility: both contents stay instantiated (no rebuild on Post ↔ Tags).
        Assert.Same(post, panel.PostContent);
        Assert.Same(tags, panel.TagContent);
    }

    [Fact]
    public void AClickOnTheOtherSegmentAsksTheViewerAndTheShownSegmentStaysUntilItAnswers()
    {
        GallerySidePanel panel = Panel(out _);
        List<(GallerySidePanelSegment Segment, bool Focus)> requests = new();
        panel.SegmentRequested += (segment, focus) => requests.Add((segment, focus));

        panel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // the shown one: nothing to ask
        Assert.Empty(requests);
        Assert.True(panel.PostButton.IsChecked);

        panel.TagsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var request = Assert.Single(requests);
        Assert.Equal(GallerySidePanelSegment.Tags, request.Segment);
        Assert.True(request.Focus); // a click moves the focus into the content
        Assert.Equal(GallerySidePanelSegment.Post, panel.Segment);
        Assert.True(panel.PostButton.IsChecked);
        Assert.False(panel.TagsButton.IsChecked);
    }

    [Fact]
    public void TheArrowKeysPickTheSegmentsAndOnlyFromTheSegment()
    {
        Assert.Equal(GallerySidePanelSegment.Post, GallerySidePanel.ArrowTarget(Key.Left, KeyModifiers.None));
        Assert.Equal(GallerySidePanelSegment.Tags, GallerySidePanel.ArrowTarget(Key.Right, KeyModifiers.None));
        Assert.Null(GallerySidePanel.ArrowTarget(Key.Right, KeyModifiers.Control)); // Ctrl+→ stays the video's seek
        Assert.Null(GallerySidePanel.ArrowTarget(Key.Up, KeyModifiers.None));
        // Without the focus on a segment button an arrow is the gallery's (previous/next medium).
        GallerySidePanel panel = Panel(out _);
        KeyEventArgs right = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right };
        Assert.False(panel.RouteSegmentKey(right));
        Assert.False(right.Handled);
    }

    [Fact]
    public void TagsIsOfferedOnlyForATaggableMediumAndSaysWhyNot()
    {
        GallerySidePanel panel = Panel(out _);
        panel.SetTagsAvailable(false, "This file cannot be tagged.");
        Assert.False(panel.TagsButton.IsEnabled);
        Assert.Equal("This file cannot be tagged.", ToolTip.GetTip(panel.TagsButton));
        // The shown segment stays usable even when the next medium is not taggable (the panel shows its reason).
        panel.Segment = GallerySidePanelSegment.Tags;
        Assert.True(panel.TagsButton.IsEnabled);
        panel.Segment = GallerySidePanelSegment.Post;
        panel.SetTagsAvailable(true, null);
        Assert.True(panel.TagsButton.IsEnabled);
        Assert.StartsWith("Tags", ToolTip.GetTip(panel.TagsButton)?.ToString());
    }

    [Fact]
    public void TheSegmentAndItsButtonsAreNamedForScreenReaders()
    {
        GallerySidePanel panel = Panel(out _);
        Assert.Equal("Post", AutomationProperties.GetName(panel.PostButton));
        Assert.Equal("Tags", AutomationProperties.GetName(panel.TagsButton));
        Control segment = (Control)panel.PostButton.Parent!;
        Assert.Equal("Panel content", AutomationProperties.GetName(segment));
        Assert.Contains("arrow", AutomationProperties.GetHelpText(segment));
        Assert.Contains("280", AutomationProperties.GetName(panel.Grip));
        Assert.Contains("480", AutomationProperties.GetName(panel.Grip));
        Texts.SetLanguage("de");
        panel.Localize();
        Assert.Equal("Panelinhalt", AutomationProperties.GetName(segment));
    }

    // ---- focus order ----

    [Fact]
    public void TheFocusOrderIsSegmentThenContentThenClose()
    {
        GallerySidePanel panel = Panel(out Button close);
        Control segment = (Control)panel.PostButton.Parent!;
        panel.Position.Show(new(1, 18, false));
        Control counterHost = panel.Position;
        Assert.Same(panel, counterHost.Parent);
        int Index(Control control) => panel.Children.IndexOf(control);
        Assert.True(Index(segment) < Index(counterHost));
        Assert.True(Index(counterHost) < Index(panel.PostSlot));
        Assert.True(Index(counterHost) < Index(panel.TagSlot));
        Assert.True(Index(panel.PostSlot) < Index(close));
        Assert.True(Index(panel.TagSlot) < Index(close));
        // The close button is the last focusable child; the grip and the slots themselves take no focus.
        Assert.Equal(close, panel.Children.Where(child => child.Focusable).Last());
        Assert.False(panel.Grip.Focusable);
        Assert.False(counterHost.Focusable);
        Assert.False(panel.PostSlot.Focusable);
        Assert.False(panel.TagSlot.Focusable);
        // Within the segment: Post before Tags.
        Assert.True(((Panel)segment).Children.IndexOf(panel.PostButton) < ((Panel)segment).Children.IndexOf(panel.TagsButton));
        // The position fills the space between the segment and the close button.
        Assert.True(Grid.GetColumn(close) > Grid.GetColumn(segment));
        Assert.Equal(Grid.GetRow(segment), Grid.GetRow(close));
        Assert.InRange(Grid.GetColumn(counterHost), Grid.GetColumn(segment) + 1, Grid.GetColumn(close) - 1);
    }

    // ---- width ----

    [Fact]
    public void TheWidthLimitsAreSettingsWithDefaultMinimumAndMaximum()
    {
        Assert.Equal((320, 280, 480), (Config.GalleryPanelWidthDefault, Config.GalleryPanelWidthMinimum, Config.GalleryPanelWidthMaximum));
        Assert.Equal(280, GallerySidePanel.ClampWidth(100));
        Assert.Equal(480, GallerySidePanel.ClampWidth(1000));
        Assert.Equal(333, GallerySidePanel.ClampWidth(333.4));
        Assert.Equal(320, GallerySidePanel.ClampWidth(double.NaN));
        Assert.Equal(320, GallerySidePanel.ClampWidth(double.PositiveInfinity));
        Assert.Equal("gallery.panelWidth", GallerySidePanel.WidthPreferenceKey);
    }

    [Fact]
    public void DraggingTheLeftEdgeToTheLeftWidensThePanelWithinItsLimits()
    {
        Assert.Equal(420, GallerySidePanel.DragWidth(320, startX: 500, currentX: 400));
        Assert.Equal(300, GallerySidePanel.DragWidth(320, startX: 500, currentX: 520));
        Assert.Equal(480, GallerySidePanel.DragWidth(320, startX: 500, currentX: 0));
        Assert.Equal(280, GallerySidePanel.DragWidth(320, startX: 500, currentX: 900));
    }

    [Theory]
    // docked from 900 dip: the wish, while the picture keeps 300 dip
    [InlineData(480, 1440, false, false, 480)]
    [InlineData(480, 900, false, false, 480)]
    [InlineData(320, 1440, false, false, 320)]
    // below 900 dip over the picture: the existing rule keeps a strip of the picture free, the minimum width wins in the end
    [InlineData(320, 640, false, true, 316)]
    [InlineData(480, 760, false, true, 436)]
    [InlineData(320, 760, false, true, 320)]
    [InlineData(320, 500, false, true, 280)]
    [InlineData(320, 290, false, true, 266)]
    // the overlay option only has to fit the viewer
    [InlineData(480, 1440, true, false, 480)]
    [InlineData(480, 400, true, false, 376)]
    // before the first layout the wish stands
    [InlineData(400, 0, false, true, 400)]
    public void TheEffectiveWidthFollowsTheDockingRule(int preferred, int viewer, bool overlayOption, bool narrow, int expected) =>
        Assert.Equal(expected, GallerySidePanel.EffectiveWidth(preferred, viewer, overlayOption, narrow, minimumPicture: 300));

    // ---- work-view rows and texts ----

    [Fact]
    public void TheSegmentHasItsOwnWorkViewRowsAndTheHostKeepsThePostPanelRow()
    {
        foreach (string id in new[] { "gallery.panel.post", "gallery.panel.tags", "gallery.post-panel" })
            foreach (WorkView view in WorkViewSetting.All)
                Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, view)), id);
        // The segment rows are elements: the commands keep their bar rows, which decide where a dialog returns the focus.
        Assert.Equal(WorkViewKind.Element, WorkViewPolicy.Get("gallery.panel.post").Kind);
        Assert.Equal(WorkViewKind.Element, WorkViewPolicy.Get("gallery.panel.tags").Kind);
        Assert.True(WorkViewPolicy.GalleryCommandReduced("media.tags.edit", WorkView.Barebones));
    }

    [Fact]
    public void EveryNewTextExistsInTheTwelveLanguages()
    {
        string[] keys = ["gallery.panel.post", "gallery.panel.tags", "gallery.panel.segments", "gallery.panel.segmentHelp", "gallery.panel.close", "gallery.panel.resize"];
        foreach (string key in keys)
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
        string[] files = Directory.GetFiles(Root("Desktop/Localization/Packs"), "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            System.Text.Json.JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in keys)
            {
                Assert.True(strings.TryGetProperty(key, out System.Text.Json.JsonElement value), Path.GetFileName(file) + ": " + key);
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), Path.GetFileName(file) + ": " + key);
            }
            Assert.Contains("{0}", strings.GetProperty("gallery.panel.resize").GetString());
            Assert.Contains("{1}", strings.GetProperty("gallery.panel.resize").GetString());
        }
    }

    // ---- source contracts of the hooks ----

    [Fact]
    public void TheHostSitsInThePostPanelSlotAndKeepsTheCloseButtonsName()
    {
        string side = ReadSource("Desktop/GalleryViewer.SidePanel.cs");
        Assert.Contains("CloseLayerButton(\"GalleryReturnImage\"", side);
        Assert.Contains("_contextPanel.Child = _sidePanel;", side);
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        // The card has no header of its own any more: no title and no second "Back to image" button in the post content.
        Assert.DoesNotContain("CloseLayerButton(\"GalleryReturnImage\"", viewer);
        Assert.DoesNotContain("Texts.T(\"gallery.viewer7\")", viewer);
        Assert.Contains("_sidePanel.RouteSegmentKey(e)", viewer);
        // The tag panel puts its surface into the Tags slot and hides its own ×: one close button in the header.
        string tags = ReadSource("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("_sidePanel.TagContent = surface;", tags);
        Assert.Contains("surface.UseSidebarHeader();", tags);
        // Placement keeps the existing rule; only the widths come from the host.
        string overlay = ReadSource("Desktop/GalleryViewer.Overlay.cs");
        Assert.Contains("_contextPanel.Width = DockedContextWidth;", overlay);
        Assert.DoesNotContain("_contextPanel.Width = 320;", overlay);
        Assert.Contains("GallerySidePanel.EffectiveWidth(_panelWidth", ReadSource("Desktop/GalleryViewer.Overflow.cs"));
        // Not touched by this package.
        foreach (string untouched in new[] { "Desktop/GalleryVideoPlayer.cs", "Desktop/GalleryImageCache.cs", "Desktop/GalleryDecodeQueue.cs", "Desktop/GalleryFrameFile.cs" })
            Assert.DoesNotContain("SidePanel", ReadSource(untouched));
    }

    [Fact]
    public void TheWidthIsAViewPreferenceOfTheLayoutProfile()
    {
        string prefs = ReadSource("Desktop/Shell/MainWindow.GalleryPreferences.cs");
        Assert.Contains("_config.GetLayoutProfile(mode).ColumnWidths[GallerySidePanel.WidthPreferenceKey] = width;", prefs);
        Assert.Contains("_pendingColumnWidths", prefs);
        Assert.Contains("_gallery.PanelWidth = GalleryPanelWidthPreference();", prefs);
        Assert.Contains("_gallery.PanelWidthChanged += width =>", ReadSource("Desktop/Shell/MainWindow.Gallery.cs"));
        // A width outside the limits that reached the profile file is clamped when read.
        Assert.Contains("GallerySidePanel.ClampWidth(width)", prefs);
    }

    private static string ReadSource(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
