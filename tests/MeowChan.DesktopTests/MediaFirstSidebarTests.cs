using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaFirstSidebarTests
{
    [Fact]
    public void MacDetailsAndImportKeepTheirOwnGestures()
    {
        var bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);
        KeyEventArgs KeyI(KeyModifiers modifiers) => new() { Key = Key.I, KeyModifiers = modifiers };
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Shift), "media.sidebar.info", "reader"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Shift), "media.sidebar.info", "media"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("media.sidebar.info", "gallery.layout.info"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Shift), "gallery.layout.info", "gallery"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Meta), "media.details", "media"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Meta | KeyModifiers.Shift), "import.open", "shell"));
        Assert.True(CommandRouter.IsNativeInput(new TextBox(), Key.I));
    }

    [Fact]
    public void DefaultModeAndLimitsNormalizeAtTheProfileBoundary()
    {
        Config config = new();
        Assert.Equal(("Off", 160, 40), (config.MediaSidebarInfo, config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
        config.MediaSidebarInfo = "unknown";
        config.MediaSidebarInfoMinimumFree = -1;
        config.MediaSidebarInfoMaximumShare = 120;
        Assert.Equal(("Off", 96, 60), (config.MediaSidebarInfo, config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
        config.MediaSidebarInfoMinimumFree = 900;
        config.MediaSidebarInfoMaximumShare = 0;
        Assert.Equal((480, 20), (config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
    }

    [Fact]
    public void AutoUsesSeparateEnterAndExitThresholds()
    {
        var preferences = ReaderMediaPreferences.Default with { MediaSidebarInfo = "Auto" };
        bool docked = false;
        foreach (var (free, expected) in new[] { (170, true), (150, true), (140, true), (130, false), (150, false), (165, true) })
        {
            Size inner = new(302, 300 + 58 + free);
            var result = ReaderMediaSidebarRules.InfoLayout(preferences, null, inner, new(1080, 1080), 0, docked,
                double.PositiveInfinity, inner.Height + 18);
            Assert.Equal(expected, result.Docked);
            docked = result.Docked;
            Assert.Equal(result, ReaderMediaSidebarRules.InfoLayout(preferences, null, inner, new(1080, 1080), 0, docked,
                double.PositiveInfinity, inner.Height + 18));
        }
    }

    [Theory]
    [InlineData(320, false)]
    [InlineData(320, true)]
    [InlineData(480, false)]
    [InlineData(480, true)]
    public void MediaKeepsItsTopAndAutoUsesOnlySpareHeight(int width, bool portrait) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string previous = Texts.Language;
        Texts.SetLanguage("en");
        PixelSize pixels = portrait ? new(1080, 1920) : new(1920, 1080);
        string orientation = portrait ? "portrait" : "landscape";
        using Bitmap photo = new(SamplePicture());
        using RenderTargetBitmap picture = new(pixels);
        Image crop = new() { Source = photo, Stretch = Stretch.UniformToFill, ClipToBounds = true };
        Rect bounds = new(0, 0, pixels.Width, pixels.Height);
        crop.Measure(bounds.Size);
        crop.Arrange(bounds);
        picture.Render(crop);
        Control? tagAnchor = null;
        ReaderMediaSidebar panel = new() { BorderThickness = new Thickness(1, 0), DecisionKeyContext = portrait ? "reader" : "media",
            OpenTagsAsync = (anchor, _) => { tagAnchor = anchor; return Task.CompletedTask; } };
        int? rating = 3;
        panel.Describe("No. 22490087", "evening.jpg", $"{pixels.Width}×{pixels.Height}", true, new(2, 64, false), 2300000);
        panel.ConfigureRating(() => true, () => rating, value => { rating = value; return Task.CompletedTask; });
        panel.ConfigureFavorite(() => true, () => false, () => Task.CompletedTask);
        panel.SetTags(["space", "landscape"], null);
        panel.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity, "image", pixels);
        Window window = new() { Width = width, Height = 900, Content = panel,
            Classes = { MotionPolicy.OffClass }, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            window.Activate();
            void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Settle();
            var details = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "ReaderMediaSidebarDetails");
            var overlay = panel.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "ReaderMediaSidebarOverlay");
            Rect In(Control c) => new(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size);
            void Capture(string layout)
            {
                if (Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_PANEL_DESIGN") is not { Length: > 0 } dir) return;
                using RenderTargetBitmap result = new(new PixelSize(width, 900));
                result.Render(panel);
                result.Save(Path.Combine(dir, $"112-mf-reader-{orientation}-{layout}-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_PANEL_DESIGN")))
            {
                foreach (string mode in new[] { "Off", "Overlay", "Auto", "Always" })
                {
                    panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = mode });
                    window.MouseMove(new Point(width / 2, 150));
                    Settle();
                    Capture(mode.ToLowerInvariant());
                }
                panel.ConfigureInfo(ReaderMediaPreferences.Default);
                Settle();
            }
            SidePanelDesignTests.AssertCompactHeader(panel);
            Rect full = In(panel.Card), image = In(panel.Picture);
            Assert.True(full.Height >= 0.92 * panel.Bounds.Height);
            Assert.False(details.IsVisible);
            Assert.False(overlay.IsVisible);
            Assert.DoesNotContain(panel.GetVisualDescendants(), c => c is GalleryDecisionRow or MediaFactGrid && c.IsEffectivelyVisible);
            BooruComponentDispatcher.Wait(panel.EditTagsAsync());
            Assert.Same(panel.InfoToggle, tagAnchor);
            Assert.True(panel.DecisionRow.RequestRating(4));
            Assert.Contains(MediaRatingText.Value(4), panel.LiveText);
            Assert.Contains(panel.Badges.Plan.Badges, mark => mark.Badge.Id == MediaBadgeId.Rating && mark.Badge.Label == "4");
            Assert.True(panel.DecisionRow.RequestRating(null));
            Assert.DoesNotContain(panel.Badges.Plan.Badges, mark => mark.Badge.Id == MediaBadgeId.Rating);
            panel.DecisionRow.RequestRating(3);

            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Overlay" });
            window.MouseMove(new Point(width / 2, 150));
            Settle();
            Assert.True(overlay.IsVisible);
            Assert.Equal(full, In(panel.Card));
            Assert.Equal(image, In(panel.Picture));
            Assert.Equal(image.Bottom, In(overlay).Bottom, 0.5);
            Assert.Equal(40, overlay.Bounds.Height);
            Assert.Equal(full.Width - 2, overlay.Bounds.Width, 0.5);
            Color background = Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background).Color;
            string ColorOf(string token)
            {
                Color c = Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Child!.FindResource(ThemeVariant.Dark, token)).Color;
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            foreach (string backdrop in new[] { "#FFFFFF", "#000000" })
            {
                string surface = ContrastRules.CompositeOver($"#{background.A:X2}{background.R:X2}{background.G:X2}{background.B:X2}", backdrop);
                Assert.True(ContrastRules.Contrast("#FFFFFF", surface) >= 7);
                Assert.True(ContrastRules.Contrast(ColorOf("Workspace.Favorite"), surface) >= 3);
                Assert.True(ContrastRules.Contrast(ColorOf("Workspace.Accent"), surface) >= 3);
            }
            Assert.True(panel.FavoriteButton.Focus(NavigationMethod.Tab));
            var ratingBar = overlay.GetVisualDescendants().OfType<GalleryDecisionRow>().Single();
            var overlayActions = overlay.GetVisualDescendants().OfType<Button>()
                .Where(button => !button.GetVisualAncestors().Contains(ratingBar)).ToArray();
            Assert.Equal(2, overlayActions.Length);
            foreach (Control next in new Control[] { panel.InfoToggle, panel.Open, panel.Frame, ratingBar.FocusStar,
                overlayActions[0], overlayActions[1], panel.Close })
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                Settle();
                Assert.Same(next, window.FocusManager!.GetFocusedElement());
            }

            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Auto" });
            Settle();
            Assert.Equal(!portrait || width == 320, details.IsVisible);
            Assert.Equal(image.Size, In(panel.Picture).Size);
            if (details.IsVisible) Assert.Equal(In(panel.Card).Bottom + 8, In(details).Top, 0.5);

            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Always" });
            Settle();
            Assert.True(details.IsVisible);
            Assert.Equal(true, panel.InfoToggle.IsChecked);
            Assert.InRange(details.Bounds.Height, 1, 360);
            Assert.True(panel.Card.Bounds.Height >= 900 * 0.55);
            Assert.Equal(image.Top, In(panel.Picture).Top, 0.5);
            Assert.False(overlay.IsVisible);

            panel.InfoToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.Describe("No. 42", "next.jpg", "", true);
            Assert.False(details.IsVisible);
            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Off" });
            var infoKey = new KeyEventArgs { Key = Key.I, KeyModifiers = KeyModifiers.Shift, Source = panel.InfoToggle };
            Assert.True(panel.RouteInfoKey(infoKey));
            Assert.True(infoKey.Handled);
            Assert.True(details.IsVisible);
            window.PlatformImpl!.Resize(new Size(width, 380), WindowResizeReason.User);
            Settle();
            Assert.False(details.IsVisible);
            Assert.Equal(false, panel.InfoToggle.IsChecked);
            Assert.Contains(Texts.T("settings.reader.mediaSidebarInfo.shortHeight"), ToolTip.GetTip(panel.InfoToggle)?.ToString());
        }
        finally { window.Close(); Texts.SetLanguage(previous); }
    });

    private static string SamplePicture([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!,
        "../../samples/MeowChan.ImageViewerComparison/fixtures/nasa-PIA18033.jpg");
}
