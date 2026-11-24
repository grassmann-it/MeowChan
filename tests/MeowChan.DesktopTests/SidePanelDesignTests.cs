using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input.Raw;
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
    private static readonly int[] InfoBarWidths = [240, 280, 320, 480, 700, 831];
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
        Matrix? StarTransform() => ((Control)row.StarButtons[0].Content!).RenderTransform?.Value;
        Matrix? initialTransform = StarTransform();
        GalleryDecisionView original = row.View;
        foreach (GalleryDecisionCoverage coverage in Enum.GetValues<GalleryDecisionCoverage>())
        {
            row.Show(original with { Favorite = coverage });
            Assert.Equal(initialTransform, StarTransform());
            row.FocusRating();
            row.Preview(4);
            Assert.Equal(initialTransform, StarTransform());
            Settle(window);
            Assert.Equal(before, controls.Select(control => In(control, frame)));
            Near(left, In((Control)row.StarButtons[0].Content!, frame).Left);
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
        Rect bar = In(row.RatingBar, frame);
        SKColor surface = pixels.GetPixel((int)bar.Left - 2, (int)bar.Top);
        static bool Between(byte sample, byte background, byte foreground) =>
            sample >= Math.Min(background, foreground) - 1 && sample <= Math.Max(background, foreground) + 1;

        for (int i = 0; i < row.StarButtons.Count; i++)
        {
            Rect cell = In(row.StarButtons[i], frame);
            string token = i < row.View.Rating.GetValueOrDefault() ? "Workspace.Favorite" : "Workspace.Muted";
            Color color = Assert.IsAssignableFrom<ISolidColorBrush>(frame.FindResource(frame.ActualThemeVariant, token)).Color;
            SKColor ink = new(color.R, color.G, color.B, color.A);
            bool hasSurface = false, hasInk = false;
            for (int y = (int)cell.Top; y < cell.Bottom; y++)
            {
                for (int x = (int)cell.Left; x < cell.Right; x++)
                {
                    SKColor pixel = pixels.GetPixel(x, y);
                    // Antialiasing blends each channel toward the glyph; gold lowers the blue channel.
                    Assert.True(Between(pixel.Red, surface.Red, ink.Red) && Between(pixel.Green, surface.Green, ink.Green)
                        && Between(pixel.Blue, surface.Blue, ink.Blue) && Between(pixel.Alpha, surface.Alpha, ink.Alpha),
                        $"Star {i + 1} pixel ({x}, {y}) is {pixel} over {surface} with ink {ink}.");
                    hasSurface |= pixel == surface;
                    hasInk |= pixel == ink;
                }
            }
            Assert.True(hasSurface && hasInk, $"Star {i + 1} must show both the panel and its {token} glyph.");
        }
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
            if (actions[i].GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("panel-action-label")) is { } label)
            {
                if (!label.IsEffectivelyVisible)
                {
                    Near(32, button.Width);
                    var icon = Assert.Single(actions[i].GetVisualDescendants().OfType<Border>(), b => b.OpacityMask is ImageBrush);
                    Assert.True(icon.IsEffectivelyVisible);
                    Near(16, icon.Bounds.Width);
                    Assert.Equal(label.Text, AutomationProperties.GetName(actions[i]));
                }
                else if (width >= 280)
                    Assert.DoesNotContain(label.TextLayout.TextLines, line => line.HasCollapsed);
                else Assert.StartsWith(label.Text!, ToolTip.GetTip(actions[i])?.ToString());
            }
            if (i == 0) Near(left, button.Left);
            else
            {
                Rect previous = In(actions[i - 1], frame);
                if (i == 1 && actions[i].Parent == actions[i - 1].Parent
                    && actions[i].Parent is PanelActionStack { KeepPairOnOneLine: true })
                    Near(previous.Top, button.Top);
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
                    Capture(frame, (queue ? "tagging-gallery-" : "gallery-") + (tags ? "tags" : "post"), width);
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
    public void DockedReaderAndMediaBarsFitEachLanguageAndProtectThePicture(bool media) => Run(() =>
    {
        string host = media ? "media" : "reader";
        Control? editorAnchor = null;
        ReaderMediaSidebar panel = new() { DecisionKeyContext = host, BorderThickness = new Thickness(1, 0),
            OpenTagsAsync = (anchor, _) => { editorAnchor = anchor; return Task.CompletedTask; } };
        using Bitmap picture = new(SamplePicture());
        panel.Describe("No. 22490087", "evening.jpg", "1920×1080", true, new(2, 64, false), 2300000, "sample");
        panel.ConfigureRating(() => true, () => 3, _ => Task.CompletedTask);
        panel.ConfigureFavorite(() => true, () => false, () => Task.CompletedTask);
        panel.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity, "sample", picture.PixelSize);
        panel.SetSourceFacts("Thread · Evening sky", "Alex", "25.11.2026", 2300000, null, null);
        Window window = new() { Width = InfoBarWidths[0], Height = 900, Content = panel, Classes = { "motion-off" } };
        try
        {
            window.Show();
            var bar = ById<MediaInfoBar>(panel, "ReaderMediaSidebarInfoBar");
            T Find<T>(string name) where T : Control => ById<T>(panel, name);
            // Fable §2.2, Variante A: “Fakten fest darunter, 3 Spalten × 2 Zeilen”; “kein Flyout”.
            var facts = ById<MediaSidebarFacts>(panel, "ReaderMediaSidebarFacts");
            Button edit = Find<Button>("ReaderMediaSidebarEditTags");
            foreach (string language in BuiltInLanguages.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                foreach (int width in InfoBarWidths)
                {
                    BooruReviewTestPlatform.Resize(window, width);
                    foreach (bool tagged in Segments)
                    {
                        panel.SetTags(tagged ? ["sky", "autumn", "clouds", "evening", "stargazing", "blue hour", "landscape"] : [], null);
                        Settle(window);
                        AssertCompactHeader(panel);
                        Assert.True(bar.IsEffectivelyVisible);
                        Assert.False(panel.DecisionRow.FavoriteButton.IsEffectivelyVisible);
                        Near(In(panel.Card, panel).Bottom + 8, In(bar, panel).Top);
                        double height = In(bar, panel).Bottom - In(panel.Card, panel).Bottom;
                        Near(8 + 40 + (tagged && width < 700 ? 28 : 0), height);
                        Assert.InRange(height, 0, 337 * 0.34);
                        // Fable §8–10: only offer expansion for tags or facts missing from the compact grid.
                        Button chevron = Find<Button>("ReaderMediaSidebarExpand");
                        bool expandable = tagged || facts.Bounds.Width < 384;
                        Assert.Equal(expandable, chevron.IsEffectivelyVisible);
                        if (expandable) Near(In(edit, bar).Right + 4, In(chevron, bar).Left);
                        Button last = expandable ? chevron : edit;
                        var tag = Find<Button>(tagged ? "ReaderMediaSidebarTagsToggle" : "ReaderMediaSidebarTagsEmpty");
                        Assert.False(panel.DecisionRow.ClearButton.IsEffectivelyVisible);
                        double inset = bar.Bounds.Width < 260 ? 4 : 8;
                        Near(inset, In(panel.DecisionRow.StarButtons[0], bar).Left);
                        Near(bar.Bounds.Width - inset, In(last, bar).Right);
                        Assert.Equal(!tagged || width < 700, tag.IsEffectivelyVisible);
                        if (tag.IsEffectivelyVisible) Near(In(last, bar).Center.Y, In(tag, bar).Center.Y);
                        else Near(In(last, bar).Center.Y, In(Find<Panel>("ReaderMediaSidebarTags"), bar).Center.Y);
                        Near(In(last, bar).Center.Y, In(panel.DecisionRow.StarButtons[0], bar).Center.Y);
                        Assert.Equal(width >= 480, edit.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible));
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tag)));
                        Assert.Empty(bar.GetVisualDescendants().OfType<MediaFactGrid>());
                        Assert.True(facts.IsEffectivelyVisible);
                        Assert.True(In(facts, panel).Top >= In(bar, panel).Bottom);
                        var values = facts.GetVisualDescendants().OfType<SelectableTextBlock>().Where(c => c.IsEffectivelyVisible).ToArray();
                        Assert.Equal(width < 420 ? 4 : 6, values.Length);
                        Assert.Equal(width < 420 || width >= 600 ? 2 : 3, values.Select(c => In(c, panel).Top).Distinct().Count());
                        Assert.All(values, value => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(value))));
                        if (language is "en" or "de" or "ru" && width is 320 or 480)
                        {
                            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                            {
                                window.RequestedThemeVariant = theme;
                                Settle(window);
                                Capture(panel, $"{host}-{language}-{theme.Key}-{(tagged ? "tags" : "empty")}", width);
                            }
                        }
                        if (!tagged)
                        {
                            tag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Same(tag, editorAnchor);
                        }
                    }
                }
            }
            BooruReviewTestPlatform.Resize(window, 320);
            panel.SetTags(Enumerable.Range(1, 40).Select(i => "tag " + i).ToArray(), null);
            Settle(window);
            var chips = Find<Panel>("ReaderMediaSidebarTags");
            Button extra = Find<Button>("ReaderMediaSidebarMoreTags");
            int displayed = chips.Children.Count(c => c.IsEffectivelyVisible && c != extra);
            Assert.InRange(displayed, 1, 39);
            Assert.Equal("+" + (40 - displayed).ToString(Texts.Culture), extra.Content);
            extra.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(extra, editorAnchor);
            double expandedHeight = bar.Bounds.Height;
            ToggleButton toggle = Find<ToggleButton>("ReaderMediaSidebarTagsToggle");
            // Tom, 17:55: “Buttons ... sind nicht fokussierbar”. Use the pointer as in the application.
            Assert.False(toggle.Focus(NavigationMethod.Tab));
            Point togglePoint = In(toggle, window).Center;
            window.MouseMove(togglePoint);
            window.MouseDown(togglePoint, MouseButton.Left);
            window.MouseUp(togglePoint, MouseButton.Left);
            Settle(window);
            Assert.False(chips.IsEffectivelyVisible);
            Near(expandedHeight - 28, bar.Bounds.Height);

            Rect card = In(panel.Card, panel), image = In(panel.Picture, panel);
            // 19:55: “Kein Chevron im Kopf”; a pointer click on the plain filename leaves the facts below the image.
            TextBlock filename = ((MediaCompactHeader)panel.HeaderRow).Meta;
            Assert.False(filename.Focus(NavigationMethod.Tab));
            Point point = In(filename, window).Center;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle(window);
            Assert.Empty(window.OpenedPopups.Where(popup => popup.Child is not ToolTip));
            Assert.True(facts.IsEffectivelyVisible);
            Assert.Equal(card, In(panel.Card, panel));
            Assert.Equal(image, In(panel.Picture, panel));
            BooruReviewTestPlatform.Resize(window, 831);
            Settle(window);
            Assert.Contains(facts.GetVisualDescendants().OfType<SelectableTextBlock>(),
                value => value.IsEffectivelyVisible && value.Text == "evening.jpg");
            Point info = In(panel.InfoToggle, window).Center;
            window.MouseMove(info);
            window.MouseDown(info, MouseButton.Left);
            window.MouseUp(info, MouseButton.Left);
            Settle(window);
            Assert.False(facts.IsEffectivelyVisible);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            Assert.False(filename.IsFocused);
            panel.SetTags([], "read only");
            var empty = Find<Button>("ReaderMediaSidebarTagsEmpty");
            Assert.False(empty.IsEnabled);
            Assert.Equal("read only", ToolTip.GetTip(empty));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NarrowHeaderKeepsPositionAndFileFactsOnOneLineInEveryLanguage() => Run(() =>
    {
        const string filename = "a-long-filename-that-needs-to-be-shortened-in-the-middle.jpg";
        ReaderMediaSidebar panel = new();
        panel.Describe("No. 22490087", filename, "1080×1920", true, new(2, 64, false), 2 * 1024 * 1024);
        Window window = new() { Width = 240, Height = 640, Content = panel };
        try
        {
            window.Show();
            MediaCompactHeader header = Assert.IsType<MediaCompactHeader>(panel.HeaderRow);
            foreach (string language in BuiltInLanguages.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                Settle(window);
                AssertCompactHeader(panel);
                Assert.False(panel.PositionPill.IsVisible);
                Assert.Equal("2 / 64", header.Position.Text);
                Assert.Contains(".jpg", header.Meta.Text);
                // §2.1: filename and size in the header; §2.2: dimensions in the facts.
                Assert.EndsWith(SizeText.Format(2 * 1024 * 1024), header.Meta.Text);
                Assert.Contains(panel.GetVisualDescendants().OfType<SelectableTextBlock>(), v => v.Text == "1080×1920" && v.IsEffectivelyVisible);
                Assert.Contains(filename, ToolTip.GetTip(header.Meta)?.ToString());
                Assert.Single(header.Meta.TextLayout.TextLines);
                Near(In(panel.Card, panel).Bottom + 8, In(panel.Details, panel).Top);
                Assert.True(panel.Frame.Bounds.Height > panel.Details.Bounds.Height);
            }
        }
        finally { window.Close(); }
    });

    internal static T ById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    internal static void AssertCompactHeader(ReaderMediaSidebar panel)
    {
        MediaCompactHeader header = Assert.IsType<MediaCompactHeader>(panel.HeaderRow);
        // Fable §2.1: 40 DIP (4 + 32 + 4) for one row, 56 for two; actions gain whole labels only.
        bool single = header.UsesSingleRow;
        Near(single ? 32 : 48, header.Bounds.Height);
        if (panel.Bounds.Width <= 320) Assert.False(single);
        if (single) Assert.True(In(header.Meta, header).Left >= In(panel.Title, header).Right);
        if (panel.Bounds.Width >= 700) Assert.True(single);
        Near(16, header.Meta.Bounds.Height);
        Rect? previous = null;
        foreach (Button button in new Button[] { panel.FavoriteButton, panel.InfoToggle, panel.Open, panel.SettingsButton, panel.Close })
        {
            Rect rect = In(button, header);
            Near(32, rect.Height);
            if (!single || button == panel.Close || button == panel.SettingsButton) Near(32, rect.Width);
            else Assert.True(rect.Width >= 32);
            if (previous.HasValue) Near(previous.Value.Right + 4, rect.Left);
            previous = rect;
        }
        Assert.True(panel.Close.IsVisible);
        Near(1, panel.Close.Opacity);
        Near(header.Bounds.Width, In(panel.Close, header).Right);
        if (panel.PositionPill.IsVisible)
        {
            double center = (In(panel.Title, header).Right + In(panel.FavoriteButton, header).Left) / 2;
            Near(center, In(panel.PositionPill, header).Center.X);
            Near(panel.PositionPill.Label.DesiredSize.Width + 18, panel.PositionPill.Bounds.Width);
        }
        else if (panel.PositionPill.HasPosition)
            Assert.Equal(panel.PositionPill.Bare, header.Position.Text);
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
                        if (!popover && (width >= 360 || width >= 320 && language is "en" or "de") &&
                            row.FavoriteButton.Bounds.Width <= 32) failures.Add($"{language}, {width}, favorite label");
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
