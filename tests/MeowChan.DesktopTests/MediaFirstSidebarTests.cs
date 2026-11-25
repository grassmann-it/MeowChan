using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaFirstSidebarTests
{
    private static readonly int[] CaptureWidths = [280, 320, 480, 831];
    private static readonly string[] CaptureLanguages = ["en", "de", "ru"];
    private static readonly bool[] FactsStates = [true, false];
    private static readonly string[] CompactFactKeys = ["media.fact.location", "media.fact.author", "media.fact.posted",
        "media.fact.file", "archive.size", "media.fact.dimensions"];
    private static readonly string[] ContrastBackdrops = ["#FFFFFF", "#000000"];
    private static readonly string[] GearEntries = ["autoHide", "facts"];

    [Theory]
    [InlineData(SavedMediaKind.Image, "sample.jpg")]
    [InlineData(SavedMediaKind.Video, "sample.webm")]
    public void HeaderFavoriteClicksTheDisplayedOccurrenceOfAGroupedTile(SavedMediaKind kind, string name) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException());
        MediaLibraryEntry entry = new(BooruMediaIdentity.Thread("42"), "Thread", 1, DateTimeOffset.UnixEpoch, null,
            "42", 1, null, 0, null, null, "asset", new string('a', 64), 100, "media", "original", name, kind,
            MediaLibraryPresence.Present, false, false);
        var row = new MediaLibraryTileRow(new(entry, [entry, entry with { PostId = "43" }]), 0,
            _ => new(MediaLibraryFlag.No, true, null, true, [], [], null));
        var writes = new List<(string, MediaLibraryEntry)>();
        page.ExecuteMediaMenuActionAsync = (command, target, current) =>
        {
            Assert.True(current());
            writes.Add((command, target));
            return Task.CompletedTask;
        };
        ReaderMediaSidebar panel = new() { DecisionKeyContext = "media" };
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(MediaPage).GetMethod("SetPanelRow", flags)!.Invoke(page, [row]);
        typeof(MediaPage).GetMethod("DescribePanel", flags)!.Invoke(page, [panel, row]);
        Window window = new() { Width = 831, Height = 700, Content = panel };
        try
        {
            window.Show();
            Settle(window);
            Assert.True(panel.FavoriteButton.IsEffectivelyEnabled);
            Click(window, Center(panel.FavoriteButton, window));
            Assert.Equal(("favorites.toggle", entry), Assert.Single(writes));
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    [Fact]
    public void MacDetailsAndImportKeepTheirOwnGestures()
    {
        var bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);
        KeyEventArgs KeyI(KeyModifiers modifiers) => new() { Key = Key.I, KeyModifiers = modifiers };
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Shift), "media.sidebar.info", "reader"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Shift), "media.sidebar.info", "media"));
        // Kürzelschema §Reservierte Tasten: sidebar facts and the separate gallery information line.
        Assert.True(EffectiveShortcutBindings.ContextsOverlap("media.sidebar.info", "gallery.layout.info"));
        // Tom 25.11 19:55: “Shift+I = Fakten, Shift+A = Autohide; Galerie-Infozeile auf Shift+Z.”
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Z, KeyModifiers = KeyModifiers.Shift }, "gallery.layout.info", "gallery"));
        Assert.False(bindings.Matches(KeyI(KeyModifiers.Shift), "gallery.layout.info", "gallery"));
        foreach (string context in new[] { "reader", "media" })
            Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Shift }, "media.sidebar.autohide", context));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Meta), "media.details", "media"));
        Assert.True(bindings.Matches(KeyI(KeyModifiers.Meta | KeyModifiers.Shift), "import.open", "shell"));
        Assert.True(CommandRouter.IsNativeInput(new TextBox(), Key.I));
        foreach (string context in new[] { "reader", "media" })
        {
            Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift },
                "media.tags.edit", context));
            Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F }, "favorites.toggle", context));
            for (int value = 0; value <= 5; value++)
                Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.D0 + value, KeyModifiers = KeyModifiers.Shift },
                    value == 0 ? "media.rating.clear" : "media.rating.set-" + value, context));
        }
    }

    [Fact]
    public void GalleryInfoOverridesMustBeDistinctAndTheNewDefaultYieldsToAnOlderReaderBinding()
    {
        ShortcutGesture info = new("I", ["Shift"]);
        var explicitInfo = new ShortcutBindingsSection().Set("media.sidebar.info", [info]).Set("gallery.layout.info", [info]);
        // Both commands now execute in the gallery and have separate meanings (19:55, Shift+I / Shift+Z).
        Assert.Contains(EffectiveShortcutBindings.Build(explicitInfo).Issues, issue => issue.BlocksSave);
        var galleryOverride = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("gallery.layout.info", [info]));
        KeyEventArgs galleryKey = new() { Key = Key.I, KeyModifiers = KeyModifiers.Shift };
        Assert.True(galleryOverride.Matches(galleryKey, "gallery.layout.info", "gallery"));
        Assert.False(galleryOverride.Matches(galleryKey, "media.sidebar.info", "gallery"));
        var readerOverride = new ShortcutBindingsSection().Set("reader.sidebar.toggle", [info]);
        var bindings = EffectiveShortcutBindings.Build(readerOverride);
        KeyEventArgs shiftI = new() { Key = Key.I, KeyModifiers = KeyModifiers.Shift };
        Assert.True(bindings.Matches(shiftI, "reader.sidebar.toggle", "reader"));
        Assert.False(bindings.Matches(shiftI, "media.sidebar.info", "reader"));
        Assert.True(bindings.Matches(shiftI, "media.sidebar.info", "media"));
    }

    [Fact]
    public void DefaultModeAndLimitsNormalizeAtTheProfileBoundary()
    {
        Config config = new();
        Assert.False(config.MediaSidebarAutoHide);
        Assert.True(config.MediaSidebarFacts);
        Assert.Equal(("Auto", 104, 40), (config.MediaSidebarInfo, config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
        config.MediaSidebarInfo = "unknown";
        config.MediaSidebarInfoMinimumFree = -1;
        config.MediaSidebarInfoMaximumShare = 120;
        Assert.Equal(("Auto", 64, 60), (config.MediaSidebarInfo, config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
        config.MediaSidebarInfoMinimumFree = 900;
        config.MediaSidebarInfoMaximumShare = 0;
        Assert.Equal((480, 20), (config.MediaSidebarInfoMinimumFree, config.MediaSidebarInfoMaximumShare));
    }

    [Fact]
    public void OverlayIdleRestartsAfterInteractionAndUsesTheLeaveDelay()
    {
        // W112-72 §5: "Nach einem Haltegrund beginnt die volle Leerlaufzeit neu."
        static long At(int ms) => System.Diagnostics.Stopwatch.Frequency * ms / 1000;
        foreach (int delay in new[] { 500, 2000, 30000 })
        {
            SidebarOverlayIdle idle = new(0);
            idle.Observe(At(delay - 1), delay, false, false, false, out bool hide);
            Assert.False(hide);
            idle.Observe(At(delay), delay, false, false, false, out hide);
            Assert.True(hide);
            foreach (var (bar, press, popup) in new[] { (true, false, false), (false, true, false), (false, false, true) })
            {
                var held = idle.Observe(At(delay + 100), delay, bar, press, popup, out hide);
                Assert.False(hide);
                var released = held.Observe(At(delay + 200), delay, false, false, false, out hide);
                Assert.False(hide);
                released.Observe(At(2 * delay + 199), delay, false, false, false, out hide);
                Assert.False(hide);
                released.Observe(At(2 * delay + 200), delay, false, false, false, out hide);
                Assert.True(hide);
            }
            var leaving = new SidebarOverlayIdle(0, Leaving: true);
            leaving.Observe(At(399), delay, false, false, false, out hide);
            Assert.False(hide);
            leaving.Observe(At(400), delay, false, false, false, out hide);
            Assert.True(hide);
        }
    }

    [Fact]
    public void HidingAndRevealingBothBarsPreservesImageBoundsAndCloseTarget() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using RenderTargetBitmap picture = new(new PixelSize(600, 900));
        ReaderMediaSidebar panel = new();
        // Tom 25.11. 19:55: “Das Medium wird nie verdeckt”; “ⓘ / Shift+I schaltet nur die Fakten”.
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = true });
        panel.Describe("No. 7", "portrait.png", "", true);
        panel.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity);
        Window window = new() { Width = 320, Height = 800, Content = panel };
        try
        {
            window.Show();
            Settle(window);
            Rect InPanel(Control c) => new(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size);
            Rect image = InPanel(panel.Picture), close = InPanel(panel.Close);
            Assert.Equal(InPanel(panel.Frame).Center.Y, image.Center.Y, 0.5);
            long time = System.Diagnostics.Stopwatch.GetTimestamp() + 3 * System.Diagnostics.Stopwatch.Frequency;
            panel.ObserveIdle(time);
            panel.ObserveIdle(time + System.Diagnostics.Stopwatch.Frequency);
            Settle(window);
            Assert.False(panel.HeaderRow.IsEffectivelyVisible);
            Assert.False(panel.Details.IsEffectivelyVisible);
            Assert.Equal(image, InPanel(panel.Picture));
            Assert.True(panel.Close.IsEffectivelyVisible);
            Assert.Equal(close, InPanel(panel.Close));
            window.MouseMove(new Point(120, 400));
            Settle(window);
            Assert.True(panel.HeaderRow.IsEffectivelyVisible);
            Assert.True(panel.Details.IsEffectivelyVisible);
            Assert.Equal(image, InPanel(panel.Picture));
            Assert.Equal(close, InPanel(panel.Close));
            window.FocusManager!.Focus(null);
            panel.ObserveIdle(time + 4 * System.Diagnostics.Stopwatch.Frequency);
            panel.ObserveIdle(time + 5 * System.Diagnostics.Stopwatch.Frequency);
            Assert.False(panel.Details.IsEffectivelyVisible);
            Assert.True(panel.Frame.Focus(NavigationMethod.Tab));
            Assert.True(panel.Details.IsEffectivelyVisible);
            Click(window, Center(panel.InfoToggle, window));
            Assert.False(panel.InfoToggle.IsChecked);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedRatingStaysVisibleOnTheFrame(bool automatic) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using RenderTargetBitmap picture = new(new PixelSize(600, 600));
        ReaderMediaSidebar panel = new();
        panel.Describe("No. 7", "same.png", "", true, mediumKey: "asset");
        panel.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity, "asset", picture.PixelSize);
        panel.SetTags(["sky"], null);
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = automatic });
        const string failure = "Rating could not be saved.";
        int writes = 0;
        panel.ConfigureRating(() => true, () => 3, _ => { writes++; return Task.FromException(new IOException(failure)); });
        // W112-72 §§1,3: “Die Leisten nehmen im Overlay nie Layoutplatz”; the write error stays below the rating.
        Window window = new() { Width = 320, Height = automatic ? 480 : 410, Content = panel };
        try
        {
            window.Show();
            if (!automatic) panel.ToggleDetails();
            Settle(window);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            double imageHeight = panel.Picture.Bounds.Height;
            if (!automatic) Assert.Equal(panel.Card.Bounds.Height - 2, panel.Frame.Bounds.Height, 0.5);
            panel.DecisionRow.StarButtons[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal(1, writes);
            Assert.True(panel.Details.IsEffectivelyVisible);
            var alert = Assert.Single(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == failure);
            Rect bounds = new(alert.TranslatePoint(default, panel)!.Value, alert.Bounds.Size);
            Assert.True(bounds.Height > 0 && new Rect(panel.Bounds.Size).Contains(bounds));
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(alert));
            if (automatic) Assert.Equal(imageHeight, panel.Picture.Bounds.Height);
            else Assert.True(panel.Picture.Bounds.Height <= imageHeight && panel.Picture.Bounds.Height > 0);
            Assert.Equal(panel.Card.Bounds.Height - 2, panel.Frame.Bounds.Height, 0.5);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ReorderingTheShownMediumKeepsItsSourceAndDockedActions() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using RenderTargetBitmap picture = new(new PixelSize(600, 600));
        ReaderMediaSidebar panel = new();
        void Describe(int position, string key) => panel.Describe("No. 7", "same.png", "", true, new(position, 9, false), mediumKey: key);
        Describe(1, "asset-a");
        panel.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity, "asset-a", picture.PixelSize);
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Auto" });
        Window window = new() { Width = 320, Height = 484, Content = panel };
        try
        {
            window.Show();
            Settle(window);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            // Tom 25.11. 19:55: “Das Medium wird nie verdeckt”; “ⓘ / Shift+I schaltet nur die Fakten”.
            window.PlatformImpl!.Resize(new Size(320, 468), WindowResizeReason.User);
            Settle(window);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            Describe(6, "asset-a");
            Settle(window);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            Assert.Same(picture, panel.Shown);
            Describe(7, "asset-b");
            Settle(window);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingADecisionAfterTheTargetChangesDoesNotWriteEitherMedium(bool favorite) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ReaderMediaSidebar panel = new();
        int?[] ratings = [2, 4];
        bool[] favorites = [false, true];
        void Select(int index)
        {
            panel.Describe("No. 7", "same.png", "", true, mediumKey: index.ToString());
            panel.ConfigureRating(() => true, () => ratings[index], value => { ratings[index] = value; return Task.CompletedTask; });
            panel.ConfigureFavorite(() => true, () => favorites[index], () => { favorites[index] = !favorites[index]; return Task.CompletedTask; });
        }
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Always" });
        Select(0);
        Window window = new() { Width = 320, Height = 820, Content = panel };
        try
        {
            window.Show();
            Settle(window);
            Button target = favorite ? panel.FavoriteButton : panel.DecisionRow.StarButtons[2];
            Point point = Center(target, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Assert.True(target.IsPressed);
            Select(1);
            Settle(window);
            window.MouseUp(point, MouseButton.Left);
            Settle(window);
            Assert.False(target.IsPressed);
            Assert.Equal(new int?[] { 2, 4 }, ratings);
            Assert.Equal((false, true), (favorites[0], favorites[1]));

            Click(window, Center(target, window));
            Assert.Equal(favorite ? new int?[] { 2, 4 } : [2, 3], ratings);
            Assert.Equal((false, !favorite), (favorites[0], favorites[1]));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void At240DipTheOverlayControlsAndTheFreeVideoPosterHaveTheirOwnHitTargets() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using RenderTargetBitmap poster = new(new PixelSize(400, 300));
        const string posterKey = "same-video-poster";
        int tagOpens = 0, playRequests = 0;
        int? rating = 2;
        ReaderMediaSidebar panel = new()
        {
            BorderThickness = new Thickness(1, 0),
            OpenTagsAsync = (_, _) => { tagOpens++; return Task.CompletedTask; }
        };
        panel.Describe("No. 7", "same.mp4", "", true, mediumKey: posterKey);
        panel.ConfigureRating(() => true, () => rating, value => { rating = value; return Task.CompletedTask; });
        panel.SetTags(["cat", "dog"], null);
        panel.ShowPicture(poster, _ => { }, true, double.PositiveInfinity, double.PositiveInfinity, mediumKey: posterKey);
        panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Always", MediaSidebarAutoHide = true });
        // Both hosts add the idle activator after creating the sidebar.
        ReaderInlineVideoSlot slot = new(panel.FrameArea, activatorWhenIdle: true);
        panel.VideoRow.Children.Add(slot.Below);
        ReaderInlineVideoController video = new();
        BooruComponentDispatcher.Wait(video.ConfigureAsync(ReaderInlineVideoOptions.Default with { Enabled = true }));
        slot.Offer(video, "video", _ => { playRequests++; return Task.FromResult<GalleryEntry?>(null); }, "same.mp4", posterKey: posterKey);
        Window window = new() { Width = 240, Height = 800, Content = panel };
        try
        {
            window.Show();
            window.Activate();
            Settle(window);
            Point freePoster = panel.Picture.TranslatePoint(new Point(40, 35), window)!.Value;
            window.MouseMove(freePoster);
            Settle(window);
            var overlay = SidePanelDesignTests.ById<Border>(panel, "ReaderMediaSidebarOverlay");
            Assert.True(overlay.IsEffectivelyVisible);
            Assert.True(slot.Activator.IsEffectivelyVisible);
            var ratingRow = SidePanelDesignTests.ById<GalleryDecisionRow>(panel, "ReaderMediaSidebarDecision");
            var actions = overlay.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible && AutomationProperties.GetAutomationId(button) is "ReaderMediaSidebarEditTags" or "ReaderMediaSidebarTagsToggle").ToArray();
            Assert.Equal(2, actions.Length);
            foreach (Button button in ratingRow.StarButtons.Cast<Button>().Concat(actions))
            {
                Point topLeft = button.TranslatePoint(default, overlay)!.Value;
                Assert.InRange(topLeft.X, 0, overlay.Bounds.Width - button.Bounds.Width + 0.01);
                Assert.InRange(button.Bounds.Width, 24, 48);
            }
            for (int index = 0; index < ratingRow.StarButtons.Count; index++)
            {
                Click(window, Center(ratingRow.StarButtons[index], window));
                Assert.Equal(index + 1, rating);
            }
            // Fable §1: “kein ⋯, Stern-Klick löscht”.
            Assert.False(ratingRow.ClearButton.IsEffectivelyVisible);
            Click(window, Center(ratingRow.StarButtons[4], window));
            Assert.Null(rating);
            Click(window, Center(actions.Single(b => AutomationProperties.GetAutomationId(b) == "ReaderMediaSidebarEditTags"), window));
            Assert.Equal(1, tagOpens);
            Click(window, Center(actions.Single(b => AutomationProperties.GetAutomationId(b) == "ReaderMediaSidebarTagsToggle"), window));
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            Assert.Equal(0, playRequests);
            Click(window, freePoster);
            Assert.Equal(1, playRequests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GearOffersExactlyTheTwoSharedPreferencesAndReflectsShortcutChanges() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Config saved = new();
        ReaderMediaSidebar sidebar = new() { SaveSidebarSetting = update => update(saved) };
        sidebar.Describe("No. 42", "fixture.jpg", "", true);
        Window window = new() { Width = 320, Height = 640, Content = sidebar };
        try
        {
            window.Show();
            Settle(window);
            ToggleButton gear = SidePanelDesignTests.ById<ToggleButton>(sidebar, "ReaderMediaSidebarSettings");
            // M7: the gear contains only auto-hide and facts in every host.
            (MenuFlyout Menu, MenuItem[] Items) Open()
            {
                Click(window, Center(gear, window));
                // Fable §12 requires a second pointer click on the open gear to dismiss its menu.
                MenuFlyout menu = Assert.IsType<MenuFlyout>(gear.Flyout);
                Assert.True(menu.IsOpen);
                Assert.Single(window.OpenedPopups, popup => popup.PlacementTarget == gear);
                MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
                Assert.Equal(GearEntries, items.Select(item => Assert.IsType<string>(item.Tag)));
                return (menu, items);
            }
            Assert.Equal(Texts.T("media.sidebar.settings"), ToolTip.GetTip(gear));
            var toggled = Open();
            Assert.True(gear.IsChecked);
            Click(window, Center(gear, window));
            Assert.False(toggled.Menu.IsOpen);
            Assert.False(gear.IsChecked);
            var first = Open();
            Assert.False(first.Items[0].IsChecked);
            Assert.True(first.Items[1].IsChecked);
            first.Items[1].Command!.Execute(null);
            first.Menu.Hide();
            Assert.False(saved.MediaSidebarFacts);
            Assert.True(sidebar.DecisionRow.IsEffectivelyVisible);
            Assert.True(sidebar.RouteInfoKey(new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Shift, Source = sidebar.Frame }));
            Assert.True(saved.MediaSidebarAutoHide);
            Settle(window);
            var second = Open();
            Assert.True(second.Items[0].IsChecked);
            Assert.False(second.Items[1].IsChecked);
            second.Menu.Hide();
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HoverRevealsAllTagsAndTheChevronKeepsThemOpenAfterLeaving() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ReaderMediaSidebar sidebar = new();
        sidebar.Describe("No. 42", "fixture.webm", "640 × 360", true, savedBytes: 4096);
        sidebar.SetSourceFacts("Board", "Reviewer", "25.11.", 4096, 8192, null);
        sidebar.SetTags(Enumerable.Range(1, 20).Select(i => $"tag {i}").ToArray(), null);
        Config saved = new();
        sidebar.SaveSidebarSetting = update => update(saved);
        Window window = new() { Width = 320, Height = 800, Content = sidebar };
        try
        {
            window.Show();
            Settle(window);
            var more = SidePanelDesignTests.ById<ScrollViewer>(sidebar, "ReaderMediaSidebarExpanded");
            var chevron = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarExpand");
            Point leave = sidebar.Frame.TranslatePoint(new Point(8, 8), window)!.Value;
            window.MouseMove(leave);
            Settle(window);
            Assert.False(more.IsEffectivelyVisible);
            double compact = sidebar.Details.Bounds.Height;
            Rect card = sidebar.Card.Bounds;
            window.MouseMove(Center(chevron, window));
            BooruComponentDispatcher.WaitUntil(() => more.IsEffectivelyVisible, "hover expands the info bar");
            Settle(window);
            // Fable §7 and Tom 21:45: hover overlays the medium; only the pin reserves space.
            Assert.Equal(compact, sidebar.Details.Bounds.Height);
            Assert.Equal(card, sidebar.Card.Bounds);
            Assert.Contains(window.OpenedPopups, popup => popup.Child?.IsVisualAncestorOf(more) == true);
            Assert.Contains(more.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "tag 20");
            Assert.Contains(more.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Texts.T("media.fact.source"));
            window.MouseMove(leave);
            BooruComponentDispatcher.WaitUntil(() => !more.IsEffectivelyVisible, "leaving releases the hover expansion");
            Click(window, Center(chevron, window));
            window.MouseMove(leave);
            Assert.True(saved.MediaSidebarPinned);
            Assert.True(sidebar.Details.Bounds.Height > compact);
            Assert.True(sidebar.Card.Bounds.Height < card.Height);
            var dwell = System.Diagnostics.Stopwatch.StartNew();
            BooruComponentDispatcher.WaitUntil(() => dwell.ElapsedMilliseconds >= 500,
                "the transient hover interval elapses after pinning");
            Assert.True(more.IsEffectivelyVisible);
            Click(window, Center(chevron, window));
            window.MouseMove(leave);
            BooruComponentDispatcher.WaitUntil(() => !more.IsEffectivelyVisible, "click releases the pinned expansion");
            Assert.False(saved.MediaSidebarPinned);
            Assert.True(sidebar.DecisionRow.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExpansionOnlyAddsFactsMissingFromTheCurrentLayout() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ReaderMediaSidebar sidebar = new();
        sidebar.Describe("No. 42", "scene.png", "640 × 360", true, savedBytes: 4096);
        sidebar.SetSourceFacts("Board", "Author", "25.11.", 4096, 4096, null);
        Window window = new() { Width = 831, Height = 800, Content = sidebar };
        try
        {
            window.Show();
            Settle(window);
            var toggle = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarExpand");
            var rows = SidePanelDesignTests.ById<ScrollViewer>(sidebar, "ReaderMediaSidebarExpanded");
            Assert.False(toggle.IsEffectivelyVisible);
            Assert.False(rows.IsEffectivelyVisible);
            Click(window, Center(sidebar.InfoToggle, window));
            Assert.True(toggle.IsEffectivelyVisible);
            Click(window, Center(toggle, window));
            string[] Labels() => rows.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("workspace-meta") && t.IsEffectivelyVisible)
                .Select(t => t.Text ?? "").ToArray();
            foreach (string key in CompactFactKeys)
                Assert.Single(Labels(), label => label == Texts.T(key));
            Assert.DoesNotContain(Texts.T("media.fact.saved"), Labels());
            Assert.DoesNotContain(Texts.T("media.fact.source"), Labels());
            sidebar.SetTags(["sky"], null);
            Click(window, Center(sidebar.InfoToggle, window));
            Assert.True(rows.IsEffectivelyVisible);
            Assert.DoesNotContain(Texts.T("media.fact.file"), Labels());
            sidebar.SetSourceFacts("Board", "Author", "25.11.", 4096, 8192, null);
            Settle(window);
            Assert.Single(Labels(), label => label == Texts.T("media.fact.saved"));
            Assert.Single(Labels(), label => label == Texts.T("media.fact.source"));
            BooruReviewTestPlatform.Resize(window, 320);
            Assert.Single(Labels(), label => label == Texts.T("media.fact.file"));
            sidebar.SetSourceFacts("Board", "Author", "25.11.", 4096, 4096, null);
            sidebar.SetTags([], null);
            BooruReviewTestPlatform.Resize(window, 831);
            Assert.False(rows.IsEffectivelyVisible);
            Assert.False(toggle.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Theory]
    [InlineData(320, false)]
    [InlineData(320, true)]
    [InlineData(480, false)]
    [InlineData(480, true)]
    public void DockingReservesSpaceAndOverlayLeavesTheImageStable(int width, bool portrait) => BooruComponentDispatcher.Run(() =>
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
        panel.Describe("No. 22490087", "evening.jpg", $"{pixels.Width}×{pixels.Height}", true, new(2, 64, false), 2300000, "image");
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
            Control details = panel.Details;
            var overlay = SidePanelDesignTests.ById<Border>(panel, "ReaderMediaSidebarOverlay");
            Rect In(Control c) => new(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size);
            void Capture(string layout)
            {
                if (Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_PANEL_DESIGN") is not { Length: > 0 } dir) return;
                using RenderTargetBitmap result = new(new PixelSize((int)panel.Bounds.Width, 900));
                result.Render(panel);
                result.Save(Path.Combine(dir, $"112-mf-reader-{orientation}-{layout}.png"), PngBitmapEncoderOptions.Default);
            }
            if (width == CaptureWidths[1] && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_PANEL_DESIGN")))
            {
                foreach (string language in CaptureLanguages)
                {
                    Texts.SetLanguage(language);
                    foreach (int captureWidth in CaptureWidths)
                    {
                        window.PlatformImpl!.Resize(new Size(captureWidth, 900), WindowResizeReason.User);
                        foreach (bool shown in FactsStates)
                        {
                            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarFacts = shown });
                            Settle();
                            Capture($"{language}-{captureWidth}-facts-{(shown ? "on" : "off")}");
                        }
                    }
                }
                Texts.SetLanguage("en");
                window.PlatformImpl!.Resize(new Size(width, 900), WindowResizeReason.User);
            }
            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Off" });
            Settle();
            // Tom 25.11. 19:55: “Das Medium wird nie verdeckt”; “ⓘ / Shift+I schaltet nur die Fakten”.
            SidePanelDesignTests.AssertCompactHeader(panel);
            Assert.False(panel.PositionPill.IsVisible);
            Assert.Equal("2 / 64", SidePanelDesignTests.ById<TextBlock>(panel, "ReaderMediaSidebarPosition").Text);
            Rect full = In(panel.Card), image = In(panel.Picture);
            Assert.Equal(In(panel.Frame).Center.Y, image.Center.Y, 0.5);
            Assert.True(details.IsVisible);
            Assert.True(overlay.IsVisible);
            var compactFacts = SidePanelDesignTests.ById<MediaSidebarFacts>(panel, "ReaderMediaSidebarFacts");
            Assert.False(compactFacts.IsEffectivelyVisible);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            Assert.False(panel.InfoToggle.IsChecked);
            Click(window, Center(panel.InfoToggle, window));
            Assert.True(compactFacts.IsEffectivelyVisible);
            Assert.True(panel.InfoToggle.IsChecked);
            Click(window, Center(panel.InfoToggle, window));
            Assert.False(compactFacts.IsEffectivelyVisible);
            Assert.True(panel.DecisionRow.IsEffectivelyVisible);
            BooruComponentDispatcher.Wait(panel.EditTagsAsync());
            Assert.Same(SidePanelDesignTests.ById<Button>(panel, "ReaderMediaSidebarEditTags"), tagAnchor);
            Assert.True(panel.DecisionRow.RequestRating(4));
            Assert.Contains(MediaRatingText.Value(4), panel.LiveText);
            Assert.Equal(4, panel.DecisionRow.View.Rating);
            // The rating badge can stand in for the controls when auto-hide conceals both bars.
            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = true, MediaSidebarInfo = "Off" });
            window.MouseMove(Center(panel.Frame, window));
            Settle();
            long idle = System.Diagnostics.Stopwatch.GetTimestamp() + 60 * System.Diagnostics.Stopwatch.Frequency;
            panel.ObserveIdle(idle);
            panel.ObserveIdle(idle + System.Diagnostics.Stopwatch.Frequency);
            Assert.False(panel.HeaderRow.IsEffectivelyVisible);
            Assert.Contains(panel.Badges.Plan.Badges, mark => mark.Badge.Id == MediaBadgeId.Rating && mark.Badge.Label == "4");
            Assert.True(panel.DecisionRow.RequestRating(null));
            Assert.Null(panel.DecisionRow.View.Rating);
            Assert.DoesNotContain(panel.Badges.Plan.Badges, mark => mark.Badge.Id == MediaBadgeId.Rating);
            panel.DecisionRow.RequestRating(3);

            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = true, MediaSidebarInfo = "Off" });
            Settle();
            full = In(panel.Card);
            image = In(panel.Picture);
            panel.ConfigureInfo(ReaderMediaPreferences.Default with { MediaSidebarAutoHide = true });
            window.MouseMove(new Point(width / 2, 150));
            Settle();
            Assert.True(overlay.IsVisible);
            Assert.Equal(full, In(panel.Card));
            Assert.Equal(image, In(panel.Picture));
            Assert.Equal(In(panel.Frame).Bottom, In(overlay).Bottom, 0.5);
            Assert.True(overlay.Bounds.Height >= 100);
            Assert.Equal(In(panel.Frame).Width, overlay.Bounds.Width, 0.5);
            Color background = Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background).Color;
            string ColorOf(string token)
            {
                Color c = Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Child!.FindResource(ThemeVariant.Dark, token)).Color;
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            foreach (string backdrop in ContrastBackdrops)
            {
                string surface = ContrastRules.CompositeOver($"#{background.A:X2}{background.R:X2}{background.G:X2}{background.B:X2}", backdrop);
                Assert.True(ContrastRules.Contrast("#FFFFFF", surface) >= 7);
                Assert.True(ContrastRules.Contrast(ColorOf("Workspace.Favorite"), surface) >= 3);
                Assert.True(ContrastRules.Contrast(ColorOf("Workspace.Accent"), surface) >= 3);
            }
            Assert.False(panel.FavoriteButton.Focus(NavigationMethod.Tab));
            Assert.False(panel.FavoriteButton.IsTabStop);
            var ratingBar = SidePanelDesignTests.ById<GalleryDecisionRow>(panel, "ReaderMediaSidebarDecision");
            var facts = SidePanelDesignTests.ById<MediaSidebarFacts>(panel, "ReaderMediaSidebarFacts");
            Control frame = SidePanelDesignTests.ById<Control>(panel, "ReaderMediaSidebarFrame");
            Button edit = SidePanelDesignTests.ById<Button>(overlay, "ReaderMediaSidebarEditTags");
            Button star = SidePanelDesignTests.ById<Button>(ratingBar, "ReaderMediaSidebarDecisionRating4");
            Control[] actions = [SidePanelDesignTests.ById<Button>(panel, "ReaderMediaSidebarInfoToggle"),
                SidePanelDesignTests.ById<Button>(panel, "ReaderMediaSidebarOpen"),
                SidePanelDesignTests.ById<Button>(panel, "ReaderMediaSidebarSettings"),
                SidePanelDesignTests.ById<Button>(panel, "ReaderMediaSidebarClose"),
                SidePanelDesignTests.ById<Button>(overlay, "ReaderMediaSidebarTagsToggle"), edit, star];
            Assert.True(frame.Focus(NavigationMethod.Tab));
            foreach (Control action in actions)
            {
                Assert.False(action.Focus(NavigationMethod.Tab));
                Assert.False(action.IsTabStop);
                Assert.Same(frame, window.FocusManager!.GetFocusedElement());
            }
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Settle();
            Assert.DoesNotContain(window.FocusManager!.GetFocusedElement(), actions);
            Assert.True(frame.Focus(NavigationMethod.Tab));
            tagAnchor = null;
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle();
            Assert.Null(tagAnchor);
            Assert.Equal(3, rating);
            Click(window, Center(star, window));
            Assert.Equal(4, rating);
            Assert.Same(frame, window.FocusManager!.GetFocusedElement());
            Click(window, Center(edit, window));
            Assert.Same(edit, tagAnchor);
            Assert.True(details.IsVisible);
            Assert.Same(panel.Frame, window.FocusManager!.GetFocusedElement());
            Assert.False(panel.DecisionRow.FocusRating());
            window.KeyDown += RouteInfo;
            try
            {
                window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Shift);
                window.KeyReleaseQwerty(PhysicalKey.I, RawInputModifiers.Shift);
                Settle();
                Assert.False(facts.IsEffectivelyVisible);
                Assert.True(ratingBar.IsEffectivelyVisible);
                Assert.Same(panel.Frame, window.FocusManager!.GetFocusedElement());
            }
            finally { window.KeyDown -= RouteInfo; }
            void RouteInfo(object? sender, KeyEventArgs e) => panel.RouteInfoKey(e);

            panel.ConfigureInfo(ReaderMediaPreferences.Default);
            Settle();
            Assert.True(details.IsVisible);
            Assert.True(In(panel.Frame).Contains(In(panel.Picture)));
            Assert.Equal(In(panel.Card).Bottom + 8, In(details).Top, 0.5);
            double withFacts = details.Bounds.Height;
            Assert.True(panel.InfoToggle.IsChecked);
            Assert.True(facts.IsEffectivelyVisible);
            Assert.InRange(details.Bounds.Height, 1, 360);
            Assert.True(panel.Card.Bounds.Height >= 900 * 0.55);
            Assert.Equal(In(panel.Frame).Center.Y, In(panel.Picture).Center.Y, 0.5);
            Assert.False(In(overlay).Intersects(In(panel.Frame)));

            Click(window, Center(panel.InfoToggle, window));
            panel.Describe("No. 42", "next.jpg", "", true);
            Settle();
            Assert.False(facts.IsEffectivelyVisible);
            Assert.True(details.Bounds.Height < withFacts);
            var infoKey = new KeyEventArgs { Key = Key.I, KeyModifiers = KeyModifiers.Shift, Source = panel.Frame };
            Assert.True(panel.RouteInfoKey(infoKey));
            Assert.True(infoKey.Handled);
            Assert.True(facts.IsEffectivelyVisible);
            window.PlatformImpl!.Resize(new Size(width, 380), WindowResizeReason.User);
            Settle();
            Assert.True(details.IsVisible);
            Assert.True(panel.InfoToggle.IsChecked);
            Assert.Contains(CommandCatalog.Label("media.sidebar.info"), ToolTip.GetTip(panel.InfoToggle)?.ToString());
            window.PlatformImpl.Resize(new Size(width, 392), WindowResizeReason.User);
            Settle();
            Assert.True(details.IsVisible);
            Assert.Equal(panel.Card.Bounds.Height - 2, panel.Frame.Bounds.Height, 0.5);
        }
        finally { window.Close(); Texts.SetLanguage(previous); }
    });

    private static string SamplePicture([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!,
        "../../samples/MeowChan.ImageViewerComparison/fixtures/nasa-PIA18033.jpg");
}
