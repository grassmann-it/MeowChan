using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class GalleryAppearanceTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"GalleryBackground\":null}")]
    [InlineData("{\"GalleryBackground\":\"Unknown\"}")]
    [InlineData("{\"GalleryBackground\":1}")]
    public void OldAndInvalidSettingsUseBlack(string json)
    {
        Assert.Equal("Black", JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!.GalleryBackground);
        Assert.Equal("Black", new Config().GalleryBackground);
    }

    [Fact]
    public void TheBackgroundSurvivesProfileCopiesAndSettingsTransfer()
    {
        using var directory = new TempDirectory();
        var original = new Config { DownloadPath = directory.Path, GalleryBackground = " checkerboard " };
        var edited = new Config();
        edited.CopySettingsFieldsFrom(original.Clone());
        var saved = new Config();
        saved.CopyFrom(edited);
        saved.SaveConfig(directory.Path);

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal("Checkerboard", loaded.GalleryBackground);
        Config imported = JsonSerializer.Deserialize(SettingsTransfer.Export(loaded).ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Equal("Checkerboard", imported.GalleryBackground);
    }

    [Fact]
    public void TheSettingsChoiceIsSearchableAndResettable() => Run(() =>
    {
        using var editor = new SettingsEditor(new Config { GalleryBackground = "Theme" });
        // ProbeSearchJump runs synchronously; keep its search from scheduling a timer beyond this test.
        ById<SearchBox>(editor, "SettingsSearchBox").Debounce = TimeSpan.Zero;
        var picker = ById<ComboBox>(editor, "GalleryBackgroundInput");
        Assert.Equal("Background", AutomationProperties.GetName(picker));
        Assert.NotNull(editor.ProbeSearchJump("checkerboard"));
        Assert.Equal("Theme", Assert.IsType<SettingsChoice>(picker.SelectedItem).Value);

        picker.SelectedIndex = 2;
        Assert.Equal("White", editor.SelectedGalleryBackground?.Value);
        Texts.SetLanguage("de");
        Assert.Equal("Hintergrund", AutomationProperties.GetName(picker));
        Assert.Equal("Weiß", Assert.IsType<SettingsChoice>(picker.SelectedItem).Label);

        editor.ResetDraft(SettingsEditor.SettingsResetScope.ReadingMedia);
        Assert.Equal("Schwarz", Assert.IsType<SettingsChoice>(picker.SelectedItem).Label);
    });

    [Fact]
    public void TheMenuAndKeyboardShareTheImageBackgroundWithoutChangingVideo() => Run(() =>
    {
        using var directory = new TempDirectory();
        var viewer = new GalleryViewer();
        try
        {
            GalleryEntry picture = ImageEntry(directory.Path, "picture");
            BooruComponentDispatcher.Wait(viewer.ShowAsync(picture, CancellationToken.None));
            var viewport = ById<Grid>(viewer, "GalleryViewport");
            var picker = ById<ComboBox>(viewer.OverflowMenuContent, "gallery.background");
            Assert.Equal(new[] { "Black", "Theme", "White", "Checkerboard" }, picker.Items.Cast<string>());
            Assert.Equal("Background", AutomationProperties.GetName(picker));
            Assert.Contains("B", Assert.IsType<string>(ToolTip.GetTip(picker)));
            Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(viewport.Background).Color);

            List<string> changes = [];
            viewer.BackgroundChanged += changes.Add;
            Assert.False(Press(viewer, new TextBox(), Key.B));
            Assert.False(Press(viewer, viewer, Key.B, KeyModifiers.Meta));
            Assert.True(Press(viewer, viewer, Key.B));
            Assert.True(Press(viewer, viewer, Key.B));
            Assert.Equal(2, picker.SelectedIndex);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(viewport.Background).Color);

            picker.SelectedIndex = 3;
            Assert.Equal(TileMode.Tile, Assert.IsType<DrawingBrush>(viewport.Background).TileMode);
            Assert.True(Press(viewer, viewer, Key.B));
            Assert.Equal(0, picker.SelectedIndex);
            Assert.Equal(new[] { "Theme", "White", "Checkerboard", "Black" }, changes);
            viewer.ConfigureBackground("White");
            Assert.Equal(4, changes.Count);

            GalleryEntry video = Entry(directory.Path, "video.webm", []);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(video, CancellationToken.None));
            Assert.False(picker.IsEnabled);
            Assert.False(Press(viewer, viewer, Key.B));
            Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(viewport.Background).Color);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(picture, CancellationToken.None));
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(viewport.Background).Color);
            Assert.Equal(4, changes.Count);
        }
        finally { BooruComponentDispatcher.Wait(viewer.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void FlippingMirrorsTheRotatedPictureAndFollowsRotationAcrossMedia() => Run(() =>
    {
        using var directory = new TempDirectory();
        var viewer = new GalleryViewer();
        try
        {
            GalleryEntry first = ImageEntry(directory.Path, "first");
            GalleryEntry next = ImageEntry(directory.Path, "next");
            byte[] original = File.ReadAllBytes(first.Asset.Path);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(first, CancellationToken.None));
            var image = ById<Image>(viewer, "GalleryImage");
            Assert.NotNull(image.Source);
            var rotate = ById<Button>(viewer.OverflowMenuContent, "gallery.rotate");
            var flip = ById<ToggleButton>(viewer.OverflowMenuContent, "gallery.flip-horizontal");
            Assert.Equal("Flip horizontally", AutomationProperties.GetName(flip));
            Assert.NotNull(ToolTip.GetTip(flip));
            Assert.Same(rotate.Parent, flip.Parent);
            Assert.True(flip.IsEnabled);

            Click(rotate);
            Point sample = new(13, 7);
            Point rotated = image.RenderTransform!.Value.Transform(sample);
            Click(flip);
            Point flipped = image.RenderTransform!.Value.Transform(sample);
            Assert.Equal(-rotated.X, flipped.X, 9);
            Assert.Equal(rotated.Y, flipped.Y, 9);
            Assert.True(flip.IsChecked);
            Click(flip);
            Assert.Equal(rotated, image.RenderTransform!.Value.Transform(sample));
            Assert.False(flip.IsChecked);

            Click(flip);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(first, CancellationToken.None));
            Assert.True(flip.IsChecked);
            Assert.Equal(90, viewer.Rotation);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(next, CancellationToken.None));
            Assert.False(flip.IsChecked);
            Assert.Equal(0, viewer.Rotation);
            BooruComponentDispatcher.Wait(viewer.ShowAsync(first, CancellationToken.None));
            Assert.False(flip.IsChecked);
            Assert.Equal(original, File.ReadAllBytes(first.Asset.Path));
        }
        finally { BooruComponentDispatcher.Wait(viewer.DisposeAsync().AsTask()); }
    });

    [Theory]
    [InlineData(0, "hflip")]
    [InlineData(90, "transpose")]
    [InlineData(180, "vflip")]
    [InlineData(270, "antitranspose")]
    public void VideoSurfacesMirrorHorizontallyAfterRotation(int rotation, string nativeFilter)
    {
        var unflipped = new VideoSurfaceTransform(2, rotation, 3, 5);
        var flipped = unflipped with { Flipped = true };
        var point = new Point(10, 3);
        Point plain = unflipped.ToMatrix().Transform(point);
        Point mirrored = flipped.ToMatrix().Transform(point);
        Assert.Equal(2 * unflipped.TranslateX - plain.X, mirrored.X, 9);
        Assert.Equal(plain.Y, mirrored.Y, 9);

        string? transform = GalleryVideoPlayer.NativeTransformType(rotation, flipped: true);
        Assert.Equal(new[] { ":video-filter=transform", ":transform-type=" + nativeFilter }, VideoNativeRotation.OptionsToAdd(null, transform));
        Assert.Equal(new[] { ":video-filter=" }, VideoNativeRotation.OptionsToAdd(transform, null));
    }

    [Fact]
    public void MacUsesBareBAndBothCommandsCanBeRebound()
    {
        var defaults = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);
        Assert.Empty(defaults.Issues);
        Assert.Equal("B", defaults.Display("gallery.background"));
        Assert.Empty(defaults.For("gallery.flip-horizontal"));
        Assert.Equal("H", defaults.Display("post.hide"));
        Assert.Equal("⇧H", defaults.Display("gallery.tools-toggle"));
        Assert.False(defaults.Matches(new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Meta }, "gallery.background"));

        ShortcutBindingsSection profile = new ShortcutBindingsSection()
            .Set("gallery.background", [ShortcutGesture.Parse("Shift+B")])
            .Set("gallery.flip-horizontal", [ShortcutGesture.Parse("Alt+Shift+B")]);
        var custom = EffectiveShortcutBindings.Build(profile, ShortcutPlatform.MacOS);
        Assert.Empty(custom.Issues);
        Assert.True(custom.Matches(new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Shift }, "gallery.background"));
        Assert.True(custom.Matches(new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift }, "gallery.flip-horizontal"));
    }

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        Texts.SetLanguage("en");
        try { test(); }
        finally { Texts.SetLanguage("en"); }
    });

    private static T ById<T>(Control root, string id) where T : Control =>
        Assert.Single(root.GetLogicalDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static bool Press(GalleryViewer viewer, Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, Source = source };
        viewer.RouteKeyForTests(e);
        return e.Handled;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static GalleryEntry ImageEntry(string directory, string name)
    {
        using var picture = new SKBitmap(32, 24);
        picture.Erase(SKColors.Transparent);
        picture.SetPixel(8, 12, SKColors.Red);
        using SKData png = picture.Encode(SKEncodedImageFormat.Png, 100);
        return Entry(directory, name + ".png", png.ToArray());
    }

    private static GalleryEntry Entry(string directory, string file, byte[] bytes)
    {
        string path = Path.Combine(directory, file);
        File.WriteAllBytes(path, bytes);
        var asset = new MediaAssetReference(file, file, "original", file, path, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "", file, true);
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "gallery", "1"), directory, asset.AssetId), snapshot, asset, null, null, false, false);
    }
}
