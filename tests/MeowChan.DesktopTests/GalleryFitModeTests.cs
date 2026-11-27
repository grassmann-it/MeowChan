using Avalonia;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class GalleryFitModeTests
{
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

        ShortcutGesture widthKey = ShortcutGesture.Parse("Primary+Alt+F8");
        ShortcutGesture heightKey = ShortcutGesture.Parse("Primary+Alt+F9");
        ShortcutBindingsSection custom = new ShortcutBindingsSection().Set(width, [widthKey]).Set(height, [heightKey]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(custom, platform);
        KeyGesture widthGesture = widthKey.Resolve(platform);
        KeyGesture heightGesture = heightKey.Resolve(platform);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = widthGesture.Key, KeyModifiers = widthGesture.KeyModifiers }, width));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = heightGesture.Key, KeyModifiers = heightGesture.KeyModifiers }, height));
        Assert.Empty(bindings.Issues);
    }
}
