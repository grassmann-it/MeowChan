using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryVideoPlayerMenuTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    public void CatalogIconsAndLocalizedLabelsFitThePlayerMenuAcrossBundledLanguages(double scale) => WithPlayer((player, anchor) =>
    {
        TopLevel.GetTopLevel(anchor)!.SetRenderScaling(scale);
        var viewer = new GalleryViewer();
        try
        {
            var overflowRepeat = Assert.IsType<GalleryViewer.OverflowItem>(
                Assert.Single(viewer.OverflowItems, item => item.Name == "GalleryVideoRepeatToggle").Tag);
            player.ObserveAudioTracks([new(1, "Original"), new(2, "")], 1);

            foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                Assert.Equal(language, Texts.Language);
                MenuItem[] rows = Open(player, anchor);
                TopLevel popup = TopLevel.GetTopLevel(rows[0])!;
                popup.SetRenderScaling(scale);
                Settle(rows[0]);
                Assert.Equal(scale, popup.RenderScaling);
                string[] commands = ["video.speed-choose", "video.audio-track", "video.snapshot", "gallery.rotate", "video.loop"];
                Assert.Equal(commands.Select(id => CommandCatalog.Get(id).Icon), rows.Select(MenuIcons.KeyOf));
                Assert.Equal(overflowRepeat.IconKey, MenuIcons.KeyOf(rows[4]));
                Assert.Equal(MenuItemToggleType.CheckBox, rows[4].ToggleType);

                AssertLayout(rows[0], Texts.T("viewer.video.speedMenu"));
                AssertLayout(rows[1], Texts.T("viewer.video.audioTrack"));
                var menu = Assert.IsAssignableFrom<MenuBase>(rows[0].FindAncestorOfType<MenuBase>());
                Assert.InRange(menu.Bounds.Width, MenuLayout.DefaultMinimumWidth - 1, MenuLayout.DefaultMaximumWidth + 1);
                Assert.Null(rows[0].InputGesture);
                Assert.Null(rows[1].InputGesture);
                Assert.True(rows[0].Focus(NavigationMethod.Directional));
                SendKey(rows[0], Key.Right);
                Assert.True(rows[0].IsSubMenuOpen);
                AssertChoices(rows[0], VideoRates.Choices.Select(GalleryVideoPlayer.RateLabel), "1×");
                SendKey(Assert.IsType<MenuItem>(rows[0].Items[0]), Key.Left);
                Assert.False(rows[0].IsSubMenuOpen);
                Assert.True(rows[0].IsFocused);
                if (language is "de" or "ja") CaptureMenu(menu, language, scale);
                SendKey(rows[0], Key.Escape);
                Assert.False(player.PlayerMenu.IsOpen);
                Assert.True(anchor.IsFocused);
            }
        }
        finally { BooruComponentDispatcher.Wait(viewer.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OpenMenuRelocalizesWithoutReplacingFocusedAudioChoice() => WithPlayer((player, anchor) =>
    {
        Texts.SetLanguage("en");
        player.ObserveAudioTracks([new(4, "Original"), new(9, "")], 9);
        MenuItem[] rows = Open(player, anchor);
        Assert.True(rows[1].Focus(NavigationMethod.Directional));
        SendKey(rows[1], Key.Right);
        MenuItem selected = Assert.IsType<MenuItem>(rows[1].Items[1]);
        Assert.True(selected.Focus(NavigationMethod.Directional));

        Texts.SetLanguage("de");
        Settle(selected);
        Assert.True(player.PlayerMenu.IsOpen);
        Assert.True(rows[1].IsSubMenuOpen);
        Assert.True(selected.IsFocused);
        Assert.Same(selected, rows[1].Items[1]);
        Assert.Equal(Texts.T("viewer.video.speedMenu"), rows[0].Header);
        Assert.Equal(Texts.T("viewer.video.audioTrack"), rows[1].Header);
        Assert.Equal(Texts.T("viewer.video.audioTrack"), ControlAutomationPeer.CreatePeerForElement(rows[1])?.GetName());
        Assert.Equal(CommandCatalog.Label("video.snapshot"), AutomationProperties.GetName(rows[2]));
        Assert.StartsWith(CommandCatalog.Label("video.snapshot"), Assert.IsType<string>(ToolTip.GetTip(rows[2])));
        AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], Texts.T("viewer.video.audioTrack.item", 2));
    });

    [Fact]
    public void RadioChoicesReflectRateAndTrackObservationsEachTimeTheMenuOpens()
    {
        List<int> requests = [];
        WithPlayer((player, anchor) =>
        {
            Texts.SetLanguage("de");
            BooruComponentDispatcher.Wait(player.ShowAsync(UnavailableVideo("first.webm"), CancellationToken.None));
            Settle(anchor);
            EnableFakeTransport(player);
            player.ObserveAudioTracks([new(4, "Original"), new(9, "")], 4);
            MenuItem[] rows = Open(player, anchor);
            MenuItem[] rates = AssertChoices(rows[0], VideoRates.Choices.Select(GalleryVideoPlayer.RateLabel), "1×");
            AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], "Original");
            Assert.True(rows[0].Focus(NavigationMethod.Directional));
            SendKey(rows[0], Key.Right);
            MenuItem faster = Assert.Single(rates, choice => Equals(choice.Header, "1,5×"));
            Assert.True(faster.Focus(NavigationMethod.Directional));
            SendKey(faster, Key.Enter);
            Assert.False(player.PlayerMenu.IsOpen);
            Assert.Equal(1.5f, player.Rate);

            rows = Open(player, anchor);
            AssertChoices(rows[0], VideoRates.Choices.Select(GalleryVideoPlayer.RateLabel), "1,5×");
            MenuItem[] tracks = AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], "Original");
            Assert.True(rows[1].Focus(NavigationMethod.Directional));
            SendKey(rows[1], Key.Right);
            Assert.True(tracks[1].Focus(NavigationMethod.Directional));
            SendKey(tracks[1], Key.Enter);
            Assert.False(player.PlayerMenu.IsOpen);
            Assert.Equal(9, Assert.Single(requests));

            rows = Open(player, anchor);
            AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], Texts.T("viewer.video.audioTrack.item", 2));
            player.PlayerMenu.Hide();

            // Show runs the real medium transition; missing verified bytes keep it off the native backend.
            BooruComponentDispatcher.Wait(player.ShowAsync(UnavailableVideo("next.webm"), CancellationToken.None));
            Settle(anchor);
            Assert.False(anchor.IsEnabled);
            Assert.Equal(1f, player.Rate);
            EnableFakeTransport(player);
            rows = Open(player, anchor);
            Assert.False(rows[1].IsVisible);
            Assert.Empty(rows[1].Items);
            player.PlayerMenu.Hide();

            player.ObserveAudioTracks([new(6, "Single track")], 6);
            rows = Open(player, anchor);
            Assert.False(rows[1].IsVisible);
            player.PlayerMenu.Hide();

            player.ObserveAudioTracks([new(2, "Dub"), new(3, "Director")], 3);
            rows = Open(player, anchor);
            Assert.True(rows[1].IsEffectivelyVisible);
            AssertChoices(rows[1], ["Dub", "Director"], "Director");
        }, id =>
        {
            requests.Add(id);
            return id;
        });
    }

    [Theory]
    [InlineData("video.speed-choose", "Primary+Shift+9")]
    [InlineData("video.audio-track", "Primary+Shift+0")]
    public void SubmenuCommandsRemainBindableOnMacWithoutDefaultShortcuts(string command, string binding)
    {
        Assert.Empty(EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).For(command));
        ShortcutGesture gesture = ShortcutGesture.Parse(binding);
        var profile = new ShortcutBindingsSection().Set(command, [gesture]);
        var mac = EffectiveShortcutBindings.Build(profile, ShortcutPlatform.MacOS);
        ShortcutGesture effective = Assert.Single(mac.For(command));
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, effective.Resolve(mac.Platform).KeyModifiers);
        Assert.Equal("⇧⌘" + binding[^1], effective.Display(mac.Platform));
        Assert.DoesNotContain(mac.Issues, issue => issue.BlocksSave);
    }

    private static MenuItem[] Open(GalleryVideoPlayer player, Button anchor)
    {
        Assert.True(anchor.Focus(NavigationMethod.Tab));
        Press(anchor, Key.Enter);
        Assert.True(player.PlayerMenu.IsOpen);
        return player.PlayerMenu.Items.Cast<MenuItem>().ToArray();
    }

    private static MenuItem[] AssertChoices(MenuItem parent, IEnumerable<string> labels, string selected)
    {
        MenuItem[] choices = parent.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(labels, choices.Select(item => Assert.IsType<string>(item.Header)));
        Assert.All(choices, item =>
        {
            Assert.Equal(MenuItemToggleType.Radio, item.ToggleType);
            Assert.Null(item.Icon);
        });
        Assert.Equal(selected, Assert.Single(choices, item => item.IsChecked).Header);
        return choices;
    }

    private static void AssertLayout(MenuItem row, string expectedLabel)
    {
        var icon = Assert.IsType<Border>(row.Icon);
        Assert.NotNull(Assert.IsType<Avalonia.Media.ImageBrush>(icon.OpacityMask).Source);
        var header = Assert.Single(row.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_HeaderPresenter");
        var label = Assert.IsAssignableFrom<TextBlock>(header.Child);
        Assert.Equal(expectedLabel, label.Text);
        Assert.InRange(icon.Bounds.Width, 15, 17);
        Assert.InRange(icon.Bounds.Height, 15, 17);
        Assert.True(icon.TranslatePoint(new Point(icon.Bounds.Width, 0), row)!.Value.X <= label.TranslatePoint(default, row)!.Value.X + 1);
        Assert.True(MenuLayout.TextWidth(label, expectedLabel) <= label.Bounds.Width + 1);
        Control chevron = Assert.Single(row.GetVisualDescendants().OfType<Control>(), part => part.Name == "PART_ExpandIcon");
        Assert.True(chevron.IsEffectivelyVisible);
        Assert.True(label.TranslatePoint(new Point(label.Bounds.Width, 0), row)!.Value.X <= chevron.TranslatePoint(default, row)!.Value.X + 1);
        Assert.InRange(row.Bounds.Width, icon.Bounds.Width + label.Bounds.Width, MenuLayout.DefaultMaximumWidth);
    }

    private static void CaptureMenu(MenuBase menu, string language, double scale)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_VIDEO_MENU") is not { Length: > 0 } directory) return;
        PixelSize pixels = PixelSize.FromSize(menu.Bounds.Size, scale);
        using RenderTargetBitmap bitmap = new(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(menu);
        bitmap.Save(Path.Combine(directory, $"video-menu-{language}-{scale * 100:0}.png"), PngBitmapEncoderOptions.Default);
    }

    private static void WithPlayer(Action<GalleryVideoPlayer, Button> test, Func<int, int?>? selectAudioTrack = null) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        var player = selectAudioTrack is null ? new GalleryVideoPlayer() : new GalleryVideoPlayer(selectAudioTrack);
        var window = new Window { Width = 640, Height = 480, Content = player, Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            EnableFakeTransport(player);
            Settle(window);
            test(player, Assert.IsType<Button>(player.PlayerMenuControl));
        }
        finally
        {
            player.PlayerMenu.Hide();
            window.Close();
            BooruComponentDispatcher.Wait(player.DisposeAsync());
        }
    });

    private static void EnableFakeTransport(GalleryVideoPlayer player) =>
        typeof(GalleryVideoPlayer).GetMethod("EnableTransport", PrivateInstance)!.Invoke(player, [true]);

    private static GalleryEntry UnavailableVideo(string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), file);
        string digest = new('a', 64);
        var asset = new MediaAssetReference(digest, file, "original", file, Path.Combine(directory, file), 3,
            digest, "https://offline.invalid/" + file, file, true);
        var manifest = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", []);
        var key = new GalleryAssetKey(new ResourceId("test", "gallery", "1"), directory, asset.AssetId);
        return new GalleryEntry(key, manifest, asset, null, null, false, false);
    }

    private static void Press(Control control, Key key)
    {
        var top = TopLevel.GetTopLevel(control)!;
        top.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        top.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(control);
    }

    private static void SendKey(Control control, Key key)
    {
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Settle(control);
    }

    private static void Settle(Control control)
    {
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
