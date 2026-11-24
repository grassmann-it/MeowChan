using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
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

    [Fact]
    public void CatalogIconsAndLocalizedLabelsFitThePlayerMenuAcrossBundledLanguages() => WithPlayer((player, anchor) =>
    {
        var viewer = new GalleryViewer();
        try
        {
            var overflowRepeat = Assert.IsType<GalleryViewer.OverflowItem>(
                Assert.Single(viewer.OverflowItems, item => item.Name == "GalleryVideoRepeatToggle").Tag);
            ObserveTracks(player, 1, new(1, "Original"), new(2, "Commentary"));

            foreach (string language in new[] { "en", "de", "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans" })
            {
                Texts.SetLanguage(language);
                Assert.Equal(language, Texts.Language);
                MenuItem[] rows = Open(player, anchor);
                string[] commands = ["video.speed-choose", "video.audio-track", "video.snapshot", "gallery.rotate", "video.loop"];
                Assert.Equal(commands.Select(id => CommandCatalog.Get(id).Icon), rows.Select(MenuIcons.KeyOf));
                Assert.Equal(overflowRepeat.IconKey, MenuIcons.KeyOf(rows[4]));
                Assert.Equal(MenuItemToggleType.CheckBox, rows[4].ToggleType);

                AssertLayout(rows[0], Texts.T("viewer.video.speedMenu"));
                AssertLayout(rows[1], Texts.T("viewer.video.audioTrack"));
                Assert.Null(rows[0].InputGesture);
                Assert.Null(rows[1].InputGesture);
                Assert.True(rows[0].Focus(NavigationMethod.Directional));
                SendKey(rows[0], Key.Right);
                Assert.True(rows[0].IsSubMenuOpen);
                SendKey(Assert.IsType<MenuItem>(rows[0].Items[0]), Key.Left);
                Assert.False(rows[0].IsSubMenuOpen);
                Press(anchor, Key.Escape);
                Assert.False(player.PlayerMenu.IsOpen);
                Assert.True(anchor.IsFocused);
            }
        }
        finally { BooruComponentDispatcher.Wait(viewer.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void RadioChoicesReflectRateAndTrackObservationsEachTimeTheMenuOpens() => WithPlayer((player, anchor) =>
    {
        Texts.SetLanguage("de");
        ObserveTracks(player, 4, new(4, "Original"), new(9, ""));
        MenuItem[] rows = Open(player, anchor);
        MenuItem[] rates = AssertChoices(rows[0], VideoRates.Choices.Select(GalleryVideoPlayer.RateLabel), "1×");
        AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], "Original");
        SendKey(rows[0], Key.Right);
        MenuItem faster = Assert.Single(rates, choice => Equals(choice.Header, "1,5×"));
        Assert.True(faster.Focus(NavigationMethod.Directional));
        SendKey(faster, Key.Enter);
        Assert.False(player.PlayerMenu.IsOpen);
        Assert.Equal(1.5f, player.Rate);

        rows = Open(player, anchor);
        AssertChoices(rows[0], VideoRates.Choices.Select(GalleryVideoPlayer.RateLabel), "1,5×");
        MenuItem[] tracks = AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], "Original");
        SendKey(rows[1], Key.Right);
        Assert.True(tracks[1].Focus(NavigationMethod.Directional));
        SendKey(tracks[1], Key.Enter);
        Assert.False(player.PlayerMenu.IsOpen);

        // Feed the resulting active track as it would be reported by the native transport.
        ObserveTracks(player, 9, new(4, "Original"), new(9, ""));
        rows = Open(player, anchor);
        AssertChoices(rows[1], ["Original", Texts.T("viewer.video.audioTrack.item", 2)], Texts.T("viewer.video.audioTrack.item", 2));
        player.PlayerMenu.Hide();

        // Show runs the real medium transition; missing verified bytes keep it off the native backend.
        BooruComponentDispatcher.Wait(player.ShowAsync(UnavailableVideo(), CancellationToken.None));
        Settle(anchor);
        Assert.False(anchor.IsEnabled);
        Assert.Equal(1f, player.Rate);
        EnableFakeTransport(player);
        rows = Open(player, anchor);
        Assert.False(rows[1].IsVisible);
        Assert.Empty(rows[1].Items);
        player.PlayerMenu.Hide();

        ObserveTracks(player, 6, new VideoAudioTrackChoice(6, "Single track"));
        rows = Open(player, anchor);
        Assert.False(rows[1].IsVisible);
        player.PlayerMenu.Hide();

        ObserveTracks(player, 3, new(2, "Dub"), new(3, "Director"));
        rows = Open(player, anchor);
        Assert.True(rows[1].IsEffectivelyVisible);
        AssertChoices(rows[1], ["Dub", "Director"], "Director");
    });

    [Theory]
    [InlineData("video.speed-choose", "Primary+Shift+9")]
    [InlineData("video.audio-track", "Primary+Shift+0")]
    public void SubmenuCommandsRemainBindableOnMacWithoutDefaultShortcuts(string command, string binding)
    {
        Assert.Empty(EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).For(command));
        ShortcutGesture gesture = ShortcutGesture.Parse(binding);
        var profile = new ShortcutBindingsSection().Set(command, [gesture]);
        var mac = EffectiveShortcutBindings.Build(profile, ShortcutPlatform.MacOS);
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, Assert.Single(mac.For(command)).Resolve(mac.Platform).KeyModifiers);
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
        Assert.True(icon.Bounds.Width > 0);
        Assert.True(icon.TranslatePoint(new Point(icon.Bounds.Width, 0), row)!.Value.X <= label.TranslatePoint(default, row)!.Value.X);
        Assert.True(MenuLayout.TextWidth(label, expectedLabel) <= label.Bounds.Width + 1);
        Assert.True(label.TranslatePoint(new Point(label.Bounds.Width, 0), row)!.Value.X <= row.Bounds.Width + 1);
        Assert.InRange(row.Bounds.Width, icon.Bounds.Width + label.Bounds.Width, MenuLayout.DefaultMaximumWidth);
    }

    private static void WithPlayer(Action<GalleryVideoPlayer, Button> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        var player = new GalleryVideoPlayer();
        var window = new Window { Width = 640, Height = 480, Content = player.TransportControl, Classes = { MotionPolicy.OffClass } };
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

    // Substitute the observations normally returned by the native VLC worker.
    private static void ObserveTracks(GalleryVideoPlayer player, int current, params VideoAudioTrackChoice[] tracks)
    {
        typeof(GalleryVideoPlayer).GetField("_audioTrackChoices", PrivateInstance)!.SetValue(player, tracks);
        typeof(GalleryVideoPlayer).GetField("_audioTrackCurrent", PrivateInstance)!.SetValue(player, current);
    }

    private static void EnableFakeTransport(GalleryVideoPlayer player) =>
        typeof(GalleryVideoPlayer).GetMethod("EnableTransport", PrivateInstance)!.Invoke(player, [true]);

    private static GalleryEntry UnavailableVideo()
    {
        const string file = "missing.webm";
        string directory = Path.GetTempPath();
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
