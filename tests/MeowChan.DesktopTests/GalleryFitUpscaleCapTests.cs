using Avalonia;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// P5 part A: the gallery's Fit may stay unlimited (default, today's behaviour) or cap how far a small image is enlarged.
public sealed class GalleryFitUpscaleCapTests
{
    [Theory]
    [InlineData(224, 255, 1000, 700, 1.0)]
    [InlineData(224, 255, 1000, 700, 1.5)]
    [InlineData(17, 11, 1200, 800, 2.0)]
    [InlineData(6000, 4000, 900, 600, 1.0)]
    public void WithoutACapFitIsTheUnchangedViewportFit(int width, int height, double viewportWidth, double viewportHeight, double scale)
    {
        double expected = Math.Min(viewportWidth * scale / width, viewportHeight * scale / height);
        Assert.Equal(expected, GalleryGeometry.Fit(width, height, viewportWidth, viewportHeight, scale));
        Assert.Equal(expected, GalleryGeometry.Fit(width, height, viewportWidth, viewportHeight, scale, null));
        Assert.Equal(expected, GalleryGeometry.Fit(width, height, viewportWidth, viewportHeight, scale, new GalleryFitPreferences().MaxUpscale(scale)));
    }

    [Fact]
    public void ThePreferenceDefaultsToNoLimit()
    {
        GalleryFitPreferences preferences = new();
        Assert.False(preferences.Limit);
        Assert.Equal(200, preferences.MaxUpscalePercent);
        Assert.Null(preferences.MaxUpscale(1));
        Assert.Null(preferences.MaxUpscale(2));
        Assert.Null(new GalleryFitPreferences(Limit: false, MaxUpscalePercent: 100).MaxUpscale(1)); // the percent alone does nothing
    }

    [Theory]
    [InlineData(100, 1.0, 1.0)]
    [InlineData(200, 1.0, 2.0)]
    [InlineData(1600, 1.0, 16.0)]
    [InlineData(200, 2.0, 4.0)]   // percent of the device-independent size: scales with the display
    [InlineData(100, 1.5, 1.5)]
    [InlineData(50, 1.0, 1.0)]    // below the minimum: clamped to 100 %
    [InlineData(5000, 1.0, 16.0)] // above the maximum: clamped to 1600 %
    public void TheCapIsThePercentOfTheDeviceIndependentSize(int percent, double scale, double cap)
        => Assert.Equal(cap, new GalleryFitPreferences(true, percent).MaxUpscale(scale)!.Value, 10);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableDisplayScaleMeansNoCap(double scale) => Assert.Null(new GalleryFitPreferences(true, 200).MaxUpscale(scale));

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(200, 2.0)]
    [InlineData(1600, 16.0)]
    public void ASmallImageStopsAtTheCapInsteadOfFillingTheViewport(int percent, double cap)
    {
        // 50x50 px in a 1000x700 viewport would fit at 14x.
        double unlimited = GalleryGeometry.Fit(50, 50, 1000, 700, 1);
        double limited = GalleryGeometry.Fit(50, 50, 1000, 700, 1, new GalleryFitPreferences(true, percent).MaxUpscale(1));
        Assert.Equal(14, unlimited, 10);
        Assert.Equal(Math.Min(cap, 14), limited, 10);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(1600)]
    public void TheCapNeverShrinksAFitThatAlreadyReducesTheImage(int percent)
    {
        double? cap = new GalleryFitPreferences(true, percent).MaxUpscale(1);
        Assert.Equal(0.1, GalleryGeometry.Fit(6000, 4000, 600, 400, 1, cap), 10);                 // large image: down to 10 %
        Assert.Equal(1.0, GalleryGeometry.Fit(1000, 700, 1000, 700, 1, cap), 10);                 // exactly fitting: 1:1
        // Even a cap below 1 (a pathological scale) is raised to 1, so the cap can never push an image under its own size.
        Assert.Equal(0.25, GalleryGeometry.Fit(4000, 4000, 1000, 1000, 1, 0.5), 10);
        Assert.Equal(1.0, GalleryGeometry.Fit(100, 100, 1000, 1000, 1, 0.5), 10);
    }

    [Fact]
    public void TheCapAppliesPerDisplayScale()
    {
        // 224x255 on a 1.5x display in a 1000x700 viewport: the viewport fit is 4.12 physical px per source px.
        double unlimited = GalleryGeometry.Fit(224, 255, 1000, 700, 1.5);
        double cap = new GalleryFitPreferences(true, 200).MaxUpscale(1.5)!.Value;
        Assert.Equal(3.0, cap);
        Assert.Equal(cap, GalleryGeometry.Fit(224, 255, 1000, 700, 1.5, cap));
        Assert.True(unlimited > cap);
    }

    [Theory]
    [InlineData(50, 50, 1000, 700)]
    [InlineData(2000, 1500, 1000, 700)]
    [InlineData(6000, 4000, 1000, 700)]
    public void TheFitDecodeNeverExceedsTheSourceAndSoNeverTheCappedSize(int width, int height, double viewportWidth, double viewportHeight)
    {
        GalleryImageInfo info = new(width, height, "png", false);
        PixelSize target = GalleryFitTarget.Calculate(info, new GalleryDecodeRequest.Fit(viewportWidth * 2, viewportHeight * 2));
        Assert.True(target.Width <= width && target.Height <= height);
        // The smallest cap (100 % at scale 1) already displays the whole source, so the decode is never larger than the capped view.
        double cap = new GalleryFitPreferences(true, 100).MaxUpscale(1)!.Value;
        Assert.True(target.Width <= width * cap + 1e-9 && target.Height <= height * cap + 1e-9);
    }
}
