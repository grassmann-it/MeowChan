using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SidePanelDesignTests
{
    private static readonly bool[] Segments = [false, true];
    private static readonly string[] BuiltInLanguages = ["en", "de"];
    private static readonly int[] Widths = [240, 280, 320, 360, 480];

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string language = Texts.Language;
        try { Texts.SetLanguage("en"); test(); }
        finally { Texts.SetLanguage(language); }
    });

    private static Rect In(Control child, Control parent) =>
        new(child.TranslatePoint(default, parent)!.Value, child.Bounds.Size);
    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 0.5);
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Capture(Control frame, string variant, int width)
    {
        string? capture = Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_PANEL_DESIGN");
        if (string.IsNullOrEmpty(capture)) return;
        frame.UpdateLayout();
        using RenderTargetBitmap bitmap = new(new PixelSize((int)frame.Bounds.Width, (int)frame.Bounds.Height));
        bitmap.Render(frame);
        string directory = capture == "1" ? Path.GetTempPath() : capture;
        bitmap.Save(Path.Combine(directory, $"112-mf-{variant}-{width}.png"), PngBitmapEncoderOptions.Default);
    }

    private static void Rating(Window window, GalleryDecisionRow row, Control frame, double left, double right, bool popover)
    {
        int cell = popover ? (right - left < 336 ? 32 : 40) : 24;
        int glyph = popover ? 24 : 20;
        Rect Star(int index) => In(row.StarButtons[index], frame);
        Rect favorite = In(row.FavoriteButton, frame), clear = In(row.ClearButton, frame);
        Near(clear.Right + 8, favorite.Left);
        Near(left, In((Control)row.StarButtons[0].Content!, frame).Left);
        for (int i = 0; i < 5; ++i)
        {
            Near(glyph, ((Control)row.StarButtons[i].Content!).Bounds.Width);
            Near(cell, Star(i).Width);
            Near(Math.Max(32, cell), Star(i).Height);
            if (i > 0) Near(Star(i - 1).Right, Star(i).Left);
        }
        Near(32, clear.Width);
        Near(32, clear.Height);
        Near(left + 5 * cell + 8, clear.Left);
        Near(Star(0).Center.Y, clear.Center.Y);
        Near(32, favorite.Height);
        Near(Star(0).Center.Y, favorite.Center.Y);
        Near(Math.Max(32, cell), In(row.RatingGroup, frame).Height);
        // The narrowest popover has less than the specified 240 DIP minimum for its icon form.
        if (right - left >= (popover ? 240 : 200)) Assert.True(favorite.Right <= right + 0.5);
        if (!popover && right - left <= 246) Near(32, favorite.Width);
        if (!popover && right - left >= 286) Assert.True(favorite.Width > 32);
        Control[] controls = [..row.StarButtons, row.ClearButton, row.FavoriteButton];
        Rect[] before = controls.Select(control => In(control, frame)).ToArray();
        GalleryDecisionView original = row.View;
        foreach (GalleryDecisionCoverage coverage in Enum.GetValues<GalleryDecisionCoverage>())
        {
            row.Show(original with { Favorite = coverage });
            row.FocusRating();
            row.Preview(4);
            Settle(window);
            Assert.Equal(before, controls.Select(control => In(control, frame)));
        }
        row.Preview(null);
        row.Show(original);
        window.Focus();
        Settle(window);
    }

    private static void RatingUsesThePanelSurface(Control frame, GalleryDecisionRow row)
    {
        using RenderTargetBitmap rendered = new(new PixelSize((int)frame.Bounds.Width, (int)frame.Bounds.Height));
        rendered.Render(frame);
        using MemoryStream png = new();
        rendered.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        using SKBitmap pixels = SKBitmap.Decode(png);
        Rect caption = In(row.RatingBar, frame);
        int left = (int)caption.Left, top = (int)caption.Top;
        SKColor surface = pixels.GetPixel(left, top);
        bool hasInk = false;
        for (int y = top; y < caption.Bottom; y++)
        {
            for (int x = left; x < caption.Right; x++)
            {
                SKColor sample = pixels.GetPixel(x, y);
                Assert.True(sample.Red + 1 >= surface.Red && sample.Green + 1 >= surface.Green && sample.Blue + 1 >= surface.Blue,
                    $"Rating pixel ({x}, {y}) is {sample} over {surface}.");
                hasInk |= sample.Red > surface.Red + 20;
            }
        }
        Assert.True(hasInk);
    }

    private static void Sections(MediaPanelSections sections, Control frame, double left, double right, int width)
    {
        Control[] children = sections.Children.Where(c => c.IsVisible).ToArray();
        Assert.Equal(7, children.Length);
        for (int i = 1; i < children.Length; i += 2)
        {
            Rect line = In(Assert.IsType<Border>(children[i]), frame);
            Near(1, line.Height);
            Assert.Equal(frame.FindResource(frame.ActualThemeVariant, "Workspace.Border"), ((Border)children[i]).Background);
            Near(left, line.Left);
            Near(right, line.Right);
            Near(In(children[i - 1], frame).Bottom + 12, line.Top);
            Near(line.Bottom + 12, In(children[i + 1], frame).Top);
        }
        TextBlock heading = sections.Tags!.GetVisualDescendants().OfType<TextBlock>().First(block => block.IsEffectivelyVisible);
        Near(left, In(heading, frame).Left);
        Assert.Contains("workspace-meta", heading.Classes);
        Assert.Equal(FontWeight.SemiBold, heading.FontWeight);
        MediaFactGrid facts = Assert.Single(sections.Info!.GetVisualDescendants().OfType<MediaFactGrid>());
        Near(left, In(facts, frame).Left);
        for (int i = 0; i < facts.Children.Count; i += 2)
        {
            TextBlock label = (TextBlock)facts.Children[i], value = (TextBlock)facts.Children[i + 1];
            Near(12, label.FontSize);
            Near(12, value.FontSize);
            Assert.Equal(FontWeight.Normal, value.FontWeight);
            Assert.Equal(frame.FindResource(frame.ActualThemeVariant, "Workspace.Muted"), label.Foreground);
            Assert.Equal(frame.FindResource(frame.ActualThemeVariant, "Workspace.Text"), value.Foreground);
            if (right - left < WorkspaceMetrics.MediaFactStackWidth) Near(label.Bounds.Left, value.Bounds.Left);
            else Assert.True(value.Bounds.Left > label.Bounds.Right);
        }
        Button[] actions = sections.Actions!.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible).OrderBy(button => In(button, frame).Y).ToArray();
        Assert.NotEmpty(actions);
        for (int i = 0; i < actions.Length; ++i)
        {
            Rect button = In(actions[i], frame);
            Assert.True(button.Left >= left - 0.5 && button.Right <= right + 0.5);
            Near(actions[i].DesiredSize.Width, button.Width);
            Near(32, button.Height);
            if (i == 0) Near(left, button.Left);
            else
            {
                Rect previous = In(actions[i - 1], frame);
                if (button.Top == previous.Top) Near(previous.Right + 8, button.Left);
                else { Near(left, button.Left); Near(previous.Bottom + 8, button.Top); }
            }
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void GallerySegmentsKeepTheirHeaderAndRatingAxesIncludingQueueMode(bool queue) => Run(() =>
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        IGalleryTagPanelOwner owner = GalleryRatingKeyTests.LayoutOwner(new PrivateTagStore(temp.Path, lease));
        GalleryEntry entry = owner.Entries[0];
        GalleryViewer viewer = new();
        // Exercise the normal panel builders without starting a decoder for the fixture.
        typeof(GalleryViewer).GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, entry);
        viewer.SetInteractionAvailable(true);
        viewer.SetFavorite(false);
        viewer.SetRating(3, null);
        viewer.SetMediaTags([]);
        viewer.Configure(true, false, false, true, 0);
        viewer.SetPosition("1 / 3", false, true, new(1, 3, false, queue));
        using TaggingWalk walk = new(owner.Entries.Select(e => new TaggingWalkItem(e.EntryKey,
            new(e.Key.Thread, e.Key.Directory, e.Key.AssetId, e.Post?.PostId, null, null, null), e.Asset.OriginalFileName!)).ToArray(),
            0, TagQueueSnapshot.Empty, new TaggingDecisionLogTests.FakeWriter());
        if (queue) viewer.StartTaggingMode(walk, owner);

        GallerySidePanel panel = viewer.SidePanel;
        Border frame = (Border)panel.Parent!;
        GalleryControlTransfer.Cancel(frame); // The fixture supplies the presentation for this panel.
        frame.Margin = default;
        Window window = new() { Width = 520, Height = 800, Content = frame, Classes = { "motion-off" }, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            foreach (int width in Widths)
            {
                Rect[]? postGeometry = null;
                foreach (bool tags in Segments)
                {
                    if (tags) viewer.ShowTagPanel(owner);
                    else panel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    frame.Width = width;
                    frame.IsVisible = true;
                    Settle(window);
                    Assert.Equal(tags, viewer.IsTagPanelOpen);
                    Control leading = (Control)panel.PostButton.Parent!;
                    GalleryMeasuredGeometryTests.AssertSidebarHeader(panel, leading, panel.Position, panel.CloseButton);
                    double left = frame.Padding.Left + frame.BorderThickness.Left;
                    Near(16, frame.Padding.Top);
                    Near(frame.Padding.Top + frame.BorderThickness.Top, In(leading, frame).Top);
                    Near(32, panel.RowDefinitions[0].ActualHeight);
                    Near(24, panel.Position.Bounds.Height);
                    Near(panel.Position.Label.DesiredSize.Width + 18, panel.Position.Bounds.Width);
                    GalleryDecisionRow row = tags ? viewer.DecisionRow! : viewer.PostDecisionRow!;
                    Near(In(leading, frame).Bottom + 12, In(row.RatingGroup, frame).Top);
                    double right = In(panel.CloseButton, frame).Right;
                    Rating(window, row, frame, left, right, false);
                    if (!tags && !queue && width == 480) RatingUsesThePanelSurface(frame, row);
                    MediaPanelSections sections = panel.GetVisualDescendants().OfType<MediaPanelSections>().Single(s => s.IsEffectivelyVisible);
                    Sections(sections, frame, left, right, width);
                    if (tags)
                    {
                        QuickTagSurface editor = Assert.Single(panel.GetVisualDescendants().OfType<QuickTagSurface>());
                        Rect input = In(editor.Input, frame);
                        Near(left, input.Left);
                        Near(right, input.Right);
                        Near(32, input.Height);
                        Near(In(editor.Title, frame).Bottom + 4, input.Top);
                    }
                    Rect[] geometry = [In(leading, frame), In(panel.CloseButton, frame), In(panel.Position, frame), In(row.RatingGroup, frame)];
                    if (tags) Assert.Equal(postGeometry, geometry);
                    else postGeometry = geometry;
                    panel.Position.Show(new(1, 3, false, queue), marked: true);
                    Settle(window);
                    Assert.Contains("marked", panel.Position.Classes);
                    Assert.Equal(MediaPositionText.Long(new(1, 3, false, queue), true), ToolTip.GetTip(panel.Position));
                    Near(geometry[2].Center.X, In(panel.Position, frame).Center.X);
                    Assert.Equal(geometry[0], In(leading, frame));
                    Assert.Equal(geometry[1], In(panel.CloseButton, frame));
                    Assert.Equal(geometry[3], In(row.RatingGroup, frame));
                    panel.Position.Show(new(1, 3, false, queue));
                    Settle(window);
                    Capture(frame, (queue ? "tagging-gallery-" : "gallery-") + (tags ? "tags" : "post"), width);
                }
            }
            viewer.ShowTagPanel(owner);
            MediaTagSubject subject = owner.SubjectOf(entry, out _)!;
            Assert.Equal(GalleryMarkOutcome.Marked, viewer.Marks!.Mark(subject, entry));
            GalleryTagPanelPosition current = new(1, 3, false, queue);
            void VerifyPosition(bool marked)
            {
                Settle(window);
                Assert.Equal(marked, panel.Position.Classes.Contains("marked"));
                Assert.Equal(MediaPositionText.Long(current, marked), ToolTip.GetTip(panel.Position));
                Assert.Equal(ToolTip.GetTip(panel.Position), Avalonia.Automation.AutomationProperties.GetName(panel.Position));
                Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(panel.Position));
                Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Off, Avalonia.Automation.AutomationProperties.GetLiveSetting(panel.Position.Label));
            }

            viewer.SetPosition("1 / 3", false, true, current);
            VerifyPosition(marked: true);
            panel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            VerifyPosition(marked: true);
            Texts.SetLanguage("de");
            viewer.SetPosition("1 / 3", false, true, current);
            VerifyPosition(marked: true);
            viewer.ShowTagPanel(owner);
            VerifyPosition(marked: true);
            Assert.Equal(GalleryMarkOutcome.Unmarked, viewer.Marks.Unmark(subject));
            viewer.SetPosition("1 / 3", false, true, current);
            VerifyPosition(marked: false);
        }
        finally { window.Close(); BooruComponentDispatcher.Wait(viewer.StopAsync()); }
    });

    [Theory, InlineData(false), InlineData(true)]
    public void ReaderAndMediaShareTheTitlePreviewAndSections(bool media) => Run(() =>
    {
        ReaderMediaSidebar panel = new() { DecisionKeyContext = media ? "media" : "reader", OpenTagsAsync = (_, _) => Task.CompletedTask };
        panel.Describe(media ? "No. 200" : "No. 1051", "evening.jpg", "640×480", true, new(1, 55, false), 2048);
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Always" });
        panel.SetSourceFacts("Archive /g/ · 42", null, null, 2048, media ? null : 2048, null);
        panel.SetTags(["landscape"], null);
        panel.ConfigureRating(() => true, () => 3, _ => Task.CompletedTask);
        panel.ConfigureFavorite(() => true, () => false, () => Task.CompletedTask);
        using Bitmap picture = new(SamplePicture());
        panel.ShowPicture(picture, _ => { }, false, 1024, 768);
        Window window = new() { Width = 520, Height = 800, Content = panel, Classes = { "motion-off" }, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            foreach (int width in Widths)
            {
                panel.Width = width;
                Settle(window);
                AssertCompactHeader(panel);
                Assert.Contains("workspace-panel-title", panel.Title.Classes);
                Near(8, In(panel.HeaderRow, panel).Top);
                Near(In(panel.HeaderRow, panel).Bottom + 8, In(panel.Card, panel).Top);
                Near(In(panel.Card, panel).Bottom + 8, In(panel.DecisionRow, panel).Top);
                double left = panel.Padding.Left, right = width - panel.Padding.Right;
                Rating(window, panel.DecisionRow, panel, left, right, false);
                Sections(Assert.Single(panel.GetVisualDescendants().OfType<MediaPanelSections>()), panel, left, right, width);
                Capture(panel, media ? "media" : "reader", width);
            }
        }
        finally { window.Close(); }
    });

    internal static void AssertCompactHeader(ReaderMediaSidebar panel)
    {
        MediaCompactHeader header = Assert.IsType<MediaCompactHeader>(panel.HeaderRow);
        Near(48, header.Bounds.Height);
        Near(16, header.Meta.Bounds.Height);
        Rect? previous = null;
        foreach (Button button in new Button[] { panel.FavoriteButton, panel.InfoToggle, panel.Open, panel.Close })
        {
            Rect rect = In(button, header);
            Near(32, rect.Width); Near(32, rect.Height);
            if (previous.HasValue) Near(previous.Value.Right + 4, rect.Left);
            previous = rect;
        }
        Assert.True(panel.Close.IsVisible);
        Near(1, panel.Close.Opacity);
        Near(header.Bounds.Width, In(panel.Close, header).Right);
        if (panel.PositionPill.IsVisible)
        {
            Near(In(panel.Title, header).Right + 8, In(panel.PositionPill, header).Left);
            Near(panel.PositionPill.Label.DesiredSize.Width + 18, panel.PositionPill.Bounds.Width);
        }
        else if (panel.PositionPill.HasPosition)
            Assert.StartsWith(panel.PositionPill.Bare + " · ", header.Meta.Text);
    }

    private static string SamplePicture([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!,
        "../../samples/MeowChan.ImageViewerComparison/fixtures/nasa-PIA18033.jpg");

    [Theory, InlineData(240), InlineData(280), InlineData(360), InlineData(440), InlineData(480)]
    public void RateAndTagPopoverUsesItsResponsiveStarTargets(int width) => Run(() =>
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var owner = GalleryRatingKeyTests.LayoutOwner(new PrivateTagStore(temp.Path, lease));
        using QuickTagSession session = new(owner.OpenTarget(owner.Entries[0], out _)!);
        QuickTagPopover popover = QuickTagPopover.Create(new Border(), session, "evening.jpg", new()
        {
            Decision = new() { View = () => new(1, GalleryDecisionCoverage.None, 3, false, true),
                SetRating = _ => Task.CompletedTask, SetFavorite = _ => Task.CompletedTask }
        });
        Border frame = popover.Frame;
        // In the application the FlyoutPresenter supplies the overlay background behind this frame.
        frame[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Overlay");
        popover.Flyout.Content = null;
        Window window = new() { Width = 520, Height = 600, Content = frame, Classes = { "motion-off" }, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            frame.Width = width;
            Settle(window);
            Capture(frame, "popover", width);
            QuickTagSurface surface = popover.Surface!;
            Near(32, surface.Header.Bounds.Height);
            Assert.DoesNotContain(surface.Header.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip-position"));
            GalleryDecisionRow row = popover.DecisionRow!;
            Near(In(surface.Header, frame).Bottom + 12, In(row, frame).Top);
            Rating(window, row, frame, 12, width - 12, true);
            Border line = frame.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("quick-tag-divider"));
            Near(In(row, frame).Bottom + 12, In(line, frame).Top);
            Near(In(line, frame).Bottom + 12, In(surface.Input, frame).Top);
            if (width >= QuickTagPopover.MinimumWidth)
            {
                TextBlock label = ((Grid)surface.AdvancedButton.Content!).Children.OfType<TextBlock>().Single();
                Assert.False(Assert.Single(label.TextLayout.TextLines).HasCollapsed);
            }
        }
        finally { window.Close(); popover.Close(); }
    });

    [Theory, InlineData(false), InlineData(true)]
    public void EveryRatingIsAccessibleWithoutVisibleCaptionInAllTwelveLanguages(bool popover) => Run(() =>
    {
        GalleryDecisionRow row = new("LocalizedDecision", compact: !popover);
        row[!Panel.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel");
        Window window = new() { Width = 520, Height = 180, Content = row, Classes = { "motion-off" }, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            List<string> failures = [];
            foreach (string language in BuiltInLanguages.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                foreach (int width in Widths)
                {
                    row.Width = width - (popover ? 24 : 34);
                    for (int rating = 0; rating <= 6; rating++)
                    {
                        GalleryDecisionView view = new(1, GalleryDecisionCoverage.None, rating is > 0 and < 6 ? rating : null, rating == 6, true);
                        row.Show(view);
                        Settle(window);
                        string caption = GalleryDecisionText.Caption(view);
                        if (!Equals(ToolTip.GetTip(row.RatingBar), caption) ||
                            Avalonia.Automation.AutomationProperties.GetName(row.RatingBar) != caption ||
                            row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == caption))
                            failures.Add($"{language}, {width}, {caption}");
                    }
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { window.Close(); }
    });
}
