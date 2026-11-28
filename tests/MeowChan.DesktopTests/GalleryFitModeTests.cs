using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class GalleryFitModeTests
{
    [Fact]
    public void WidthFitScrollsWithTheWheelButActualSizeUsesTheWheelForZoom() => WithTallImage((viewer, viewport) =>
    {
        double start = viewer.CaptureState().PanY;
        ScrollDown(viewport);
        Assert.True(viewer.CaptureState().PanY < start);
        Assert.False(viewer.IsViewManuallyZoomed);

        Assert.True(Press(viewer, Key.NumPad1));
        Assert.True(viewer.CaptureState().ActualZoom);
        Assert.Equal(1, viewer.ViewZoom);
        ScrollDown(viewport);
        Assert.InRange(viewer.ViewZoom, 0.01, 0.99);
    });

    [Fact]
    public void APageDownRotationBindingTakesPriorityOverWidthFitScrolling() => WithTallImage((viewer, _) =>
    {
        double start = viewer.CaptureState().PanY;
        Assert.True(Press(viewer, Key.PageDown));
        Assert.True(viewer.CaptureState().PanY < start);
        Assert.Equal(0, viewer.Rotation);

        ShortcutService.Publish(new ShortcutBindingsSection()
            .Set("gallery.rotate", [ShortcutGesture.Parse("PageDown")]));
        Assert.True(Press(viewer, Key.PageDown));
        Assert.Equal(90, viewer.Rotation);
        Assert.False(viewer.IsViewManuallyZoomed);
    });

    [Fact]
    public void APageDownNavigationBindingTakesPriorityOverWidthFitScrolling() => WithTallImage((viewer, _) =>
    {
        viewer.SetPosition("1 / 2", previous: false, next: true);
        List<int> moves = [];
        viewer.MoveRequested += moves.Add;
        ShortcutService.Publish(new ShortcutBindingsSection()
            .Set("gallery.next", [ShortcutGesture.Parse("PageDown")]));

        double start = viewer.CaptureState().PanY;
        Assert.True(Press(viewer, Key.PageDown));
        Assert.Equal(1, Assert.Single(moves));
        Assert.Equal(start, viewer.CaptureState().PanY);
    });

    [Fact]
    public void HorizontalFitArrowsBrowseWhenPanHasReachedAnEdge() => WithTallImage((viewer, _) =>
    {
        Window window = Assert.IsType<Window>(TopLevel.GetTopLevel(viewer));
        window.Height = 640;
        window.UpdateLayout();
        viewer.RotateView();
        viewer.ConfigureFit(new GalleryFitPreferences { Mode = GalleryFitMode.Height });
        viewer.SetPosition("2 / 3", previous: true, next: true);
        List<int> moves = [];
        viewer.MoveRequested += moves.Add;

        double start = viewer.CaptureState().PanX;
        Assert.True(start > 0);
        Assert.True(Press(viewer, Key.Left));
        Assert.Equal(-1, Assert.Single(moves));
        Assert.True(Press(viewer, Key.Right));
        Assert.True(viewer.CaptureState().PanX < start);
        Assert.Single(moves);
        for (int attempt = 0; attempt < 50 && moves.Count < 2; attempt++)
            Assert.True(Press(viewer, Key.Right));
        Assert.Equal(new[] { -1, 1 }, moves);
        Assert.Equal(-start, viewer.CaptureState().PanX, 8);
    });

    [Fact]
    public void VideoFitResetsTheImagePreferenceAndTheMenuKeepsOneRadioSelection() => WithTallImage((viewer, _, picture) =>
    {
        RadioButton fit = FitRow("gallery.fit");
        RadioButton width = FitRow("gallery.fit-width");
        RadioButton height = FitRow("gallery.fit-height");
        Assert.True(width.IsChecked);
        Assert.False(fit.IsChecked);
        Assert.False(height.IsChecked);
        Assert.Equal(MenuItemToggleType.Radio, Assert.IsType<GalleryViewer.OverflowItem>(fit.Tag).Icon.ToggleType);
        List<GalleryFitMode> changes = [];
        viewer.FitModeChanged += changes.Add;

        double widthZoom = viewer.ViewZoom;
        var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(ControlAutomationPeer.CreatePeerForElement(height));
        selection.Select();
        Assert.Equal(GalleryFitMode.Height, Assert.Single(changes));
        Assert.True(selection.IsSelected);
        Assert.False(width.IsChecked);
        Assert.False(fit.IsChecked);
        Assert.True(viewer.ViewZoom < widthZoom);
        changes.Clear();

        using TempDirectory directory = new();
        const string name = "missing.webm";
        MediaAssetReference asset = new(name, name, "original", name, Path.Combine(directory.Path, name), 0,
            new string('0', 64), "", name, true);
        MediaAssetSnapshot snapshot = new(directory.Path, MediaManifestStatus.Available, 1, "fixture", [asset]);
        GalleryEntry video = new(new(new("fixture", "gallery", "2"), directory.Path, name), snapshot, asset, null, null, false, false);
        BooruComponentDispatcher.Wait(viewer.ShowAsync(video, CancellationToken.None));
        Assert.True(fit.IsChecked);
        Assert.True(fit.IsEnabled);
        Assert.False(width.IsChecked);
        Assert.False(height.IsChecked);
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(fit, null);
        Assert.True(fit.IsChecked);
        Assert.False(width.IsChecked);
        Assert.False(height.IsChecked);
        Assert.Equal(GalleryFitMode.Fit, Assert.Single(changes));
        BooruComponentDispatcher.Wait(viewer.ShowAsync(picture, CancellationToken.None));
        Assert.True(fit.IsChecked);
        Assert.False(width.IsChecked);
        Assert.False(height.IsChecked);
        return;

        RadioButton FitRow(string id) => Assert.IsAssignableFrom<RadioButton>(
            Assert.Single(viewer.OverflowItems, button => AutomationProperties.GetAutomationId(button) == id));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAxisFitReachesBothEdgesAtDisplayScale(bool horizontal)
    {
        GalleryViewGeometry geometry = new(1000, 12000, 800, 600, 2, Rotation: horizontal ? 90 : 0,
            FitMode: horizontal ? GalleryFitMode.Height : GalleryFitMode.Width);
        double fittedLength = geometry.ScrollsHorizontally ? geometry.ViewportHeight : geometry.ViewportWidth;
        Assert.Equal(fittedLength * 2 / 1000, geometry.Fit, 10);
        double overflow = geometry.FitScrollLimit(geometry.Fit);
        double expectedLength = fittedLength * 12;
        double viewportLength = geometry.ScrollsHorizontally ? geometry.ViewportWidth : geometry.ViewportHeight;
        Assert.Equal((expectedLength - viewportLength) / 2, overflow, 10);

        GalleryViewPlacement first = GalleryViewMath.Fitted(geometry);
        Assert.False(first.Manual);
        Assert.Equal(overflow, geometry.FitScrollPan(first), 10);
        Assert.Equal(0, geometry.FitScrollPosition(first)!.Value, 10);

        GalleryViewPlacement last = GalleryViewMath.Clamped(geometry.WithFitScrollPan(first, -expectedLength), geometry);
        Assert.Equal(-overflow, geometry.FitScrollPan(last), 10);
        Assert.Equal(1, geometry.FitScrollPosition(last)!.Value, 10);

        GalleryViewGeometry resized = geometry with { ViewportWidth = 750, ViewportHeight = 450 };
        GalleryViewPlacement restored = GalleryViewMath.IfAutomatic(last, resized, 0.75);
        Assert.Equal(0.75, resized.FitScrollPosition(restored)!.Value, 10);
    }

    [Fact]
    public void TheUpscaleCapAlsoAppliesToSingleAxisFit()
    {
        GalleryFitPreferences preferences = new(true, 200) { Mode = GalleryFitMode.Width };
        GalleryViewGeometry geometry = new(100, 200, 800, 650, 1.5, preferences.MaxUpscale(1.5), FitMode: preferences.Mode);
        Assert.Equal(3, geometry.Fit);
        Assert.Equal(3, (geometry with { FitMode = GalleryFitMode.Height }).Fit);
        Assert.Null(geometry.FitScrollPosition(GalleryViewMath.Fitted(geometry)));
        Assert.Equal(0.6, (geometry with { SourceWidth = 2000 }).Fit, 10);
    }

    [Fact]
    public void SingleAxisDecodeKeepsTheFullPictureWithinTheFitBudget()
    {
        GalleryImageInfo tall = new(1000, 12000, "PNG", false);
        var (width, height) = GalleryViewGeometry.FitBounds(800, 600, GalleryFitMode.Width);
        Assert.Equal(new PixelSize(800, 9600), GalleryFitTarget.Calculate(tall, new(width, height)));
        var (wideWidth, wideHeight) = GalleryViewGeometry.FitBounds(6000, 2000, GalleryFitMode.Height);
        GalleryImageInfo wide = new(24000, 2000, "PNG", false);
        PixelSize bounded = GalleryFitTarget.Calculate(wide, new(wideWidth, wideHeight));
        Assert.True((long)bounded.Width * bounded.Height <= GalleryFitTarget.MaximumPixels);
        Assert.True(bounded.Width > 6000);
    }

    [Theory]
    [InlineData(" width ", "Width")]
    [InlineData("HEIGHT", "Height")]
    [InlineData("invalid", "Fit")]
    public void ConfigRoundTripsTheModeAndSettingsDraftsPreserveTheLiveChoice(string stored, string expected)
    {
        using TempDirectory profile = new();
        Config source = new() { DownloadPath = Path.Combine(profile.Path, "media"), GalleryFitMode = stored };
        Config copied = new();
        copied.CopyFrom(source.Clone());
        copied.CopySettingsFieldsFrom(new Config { DownloadPath = source.DownloadPath });
        copied.SaveConfig(profile.Path);
        Assert.Equal(expected, Config.LoadConfig(profile.Path).GalleryFitMode);
    }

    [Fact]
    public void OlderProfilesDefaultToFit()
    {
        using TempDirectory profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"RecheckTimer\":120}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Equal("Fit", config.GalleryFitMode);
        Assert.Equal(120, config.RecheckTimer);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void WidthAndHeightCanBeBoundAndStartWithoutShortcuts(ShortcutPlatform platform)
    {
        const string width = "gallery.fit-width", height = "gallery.fit-height";
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(defaults.For(width));
        Assert.Empty(defaults.For(height));

        ShortcutGesture widthKey = ShortcutGesture.Parse("Primary+Shift+F9");
        ShortcutGesture heightKey = ShortcutGesture.Parse("Primary+Shift+F12");
        ShortcutBindingsSection custom = new ShortcutBindingsSection().Set(width, [widthKey]).Set(height, [heightKey]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(custom, platform);
        Assert.Empty(bindings.Issues);
        KeyGesture widthGesture = widthKey.Resolve(platform);
        KeyGesture heightGesture = heightKey.Resolve(platform);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = widthGesture.Key, KeyModifiers = widthGesture.KeyModifiers }, width));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = heightGesture.Key, KeyModifiers = heightGesture.KeyModifiers }, height));
    }

    private static void WithTallImage(Action<GalleryViewer, Grid> test) =>
        WithTallImage((viewer, viewport, _) => test(viewer, viewport));

    private static void WithTallImage(Action<GalleryViewer, Grid, GalleryEntry> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory directory = new();
        ShortcutBindingsSection previous = ShortcutService.CurrentSection;
        ShortcutService.Publish(new());
        GalleryViewer viewer = new();
        Window window = new() { Width = 400, Height = 300, Content = viewer };
        try
        {
            const string file = "tall.png";
            string path = Path.Combine(directory.Path, file);
            using SKBitmap picture = new(640, 1280);
            picture.Erase(SKColors.Red);
            using SKData png = picture.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = png.ToArray();
            File.WriteAllBytes(path, bytes);
            MediaAssetReference asset = new(file, file, "original", file, path, bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "", file, true);
            MediaAssetSnapshot snapshot = new(directory.Path, MediaManifestStatus.Available, 1, "fixture", [asset]);
            GalleryEntry entry = new(new(new("fixture", "gallery", "1"), directory.Path, asset.AssetId),
                snapshot, asset, null, null, false, false);

            viewer.ConfigureFit(new GalleryFitPreferences { Mode = GalleryFitMode.Width });
            window.Show();
            window.UpdateLayout();
            BooruComponentDispatcher.Wait(viewer.ShowAsync(entry, CancellationToken.None));
            window.UpdateLayout();
            Grid viewport = Assert.Single(viewer.GetLogicalDescendants().OfType<Grid>(),
                control => AutomationProperties.GetAutomationId(control) == "GalleryViewport");
            Assert.True(viewer.CaptureState().PanY > 0);
            Assert.InRange(viewer.ViewZoom, 0.01, 0.8);
            test(viewer, viewport, entry);
        }
        finally
        {
            window.Close();
            BooruComponentDispatcher.Wait(viewer.DisposeAsync().AsTask());
            ShortcutService.Publish(previous);
        }
    });

    private static bool Press(GalleryViewer viewer, Key key)
    {
        KeyEventArgs input = new() { Key = key, Source = viewer };
        viewer.RouteKeyForTests(input);
        return input.Handled;
    }

    private static void ScrollDown(Grid viewport)
    {
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        PointerWheelEventArgs wheel = new(viewport, pointer, viewport, new Point(5, 5), 0,
            new PointerPointProperties(), KeyModifiers.None, new Vector(0, -1)) { RoutedEvent = InputElement.PointerWheelChangedEvent };
        viewport.RaiseEvent(wheel);
        Assert.True(wheel.Handled);
    }
}
