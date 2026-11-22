using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Plan V4: zoom, pan and rotation of images and videos share one set of rules. These tests pin the pure parts (steps and
/// clamping, pan limits, quarter turns, the video surface matrix and the Windows crop), the catalog entry with its yielding
/// default, the texts and the settings limit. The surface itself needs a platform (LibVLC, a window); its proof is the smokes.
/// </summary>
[Collection("Localization")]
public sealed class VideoZoomRotateTests : IDisposable
{
    public VideoZoomRotateTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static GalleryViewGeometry Landscape(int rotation = 0, int maxPercent = GalleryZoomLimits.MaxPercentDefault, double scale = 1) =>
        new(1920, 1080, 1280, 720, scale, null, maxPercent, rotation);

    // ---- rotation steps ----

    [Theory]
    [InlineData(0, 0)] [InlineData(90, 90)] [InlineData(360, 0)] [InlineData(450, 90)] [InlineData(-90, 270)]
    [InlineData(-360, 0)] [InlineData(95, 90)] [InlineData(89, 0)] [InlineData(719, 630 % 360)]
    public void RotationNormalizesToTheQuarterTurnBelowIt(int degrees, int expected) =>
        Assert.Equal(expected, GalleryRotation.Normalize(degrees));

    [Fact]
    public void ClockwiseStepsCycleThroughFourOrientationsAndBackStepsReverseThem()
    {
        int rotation = 0;
        int[] seen = new int[4];
        for (int i = 0; i < 4; i++) { rotation = GalleryRotation.Next(rotation); seen[i] = rotation; }
        Assert.Equal(new[] { 90, 180, 270, 0 }, seen);
        Assert.Equal(270, GalleryRotation.Previous(0));
        Assert.Equal(0, GalleryRotation.Previous(90));
        Assert.True(GalleryRotation.IsQuarterTurn(90));
        Assert.True(GalleryRotation.IsQuarterTurn(270));
        Assert.False(GalleryRotation.IsQuarterTurn(0));
        Assert.False(GalleryRotation.IsQuarterTurn(180));
    }

    [Theory]
    [InlineData(10, 4, 1, -4, 10)]
    [InlineData(10, 4, 2, -10, -4)]
    [InlineData(10, 4, 3, 4, -10)]
    [InlineData(10, 4, 4, 10, 4)]
    [InlineData(10, 4, -1, 4, -10)]
    public void AViewVectorTurnsWithTheMediumSoTheSpotUnderTheCentreStays(double x, double y, int steps, double expectedX, double expectedY)
    {
        (double turnedX, double turnedY) = GalleryRotation.Turn(x, y, steps);
        Assert.Equal(expectedX, turnedX);
        Assert.Equal(expectedY, turnedY);
    }

    // ---- fit, limits and zoom steps ----

    [Fact]
    public void ARotatedQuarterFitsItsSwappedSizeIntoTheViewport()
    {
        GalleryViewGeometry upright = Landscape();
        GalleryViewGeometry turned = Landscape(90);
        Assert.Equal((1920, 1080), upright.Oriented);
        Assert.Equal((1080, 1920), turned.Oriented);
        Assert.Equal(1280d / 1920, upright.Fit, 12);
        Assert.Equal(720d / 1920, turned.Fit, 12); // The tall picture is limited by the viewport's height.
        Assert.Equal(upright.Fit, Landscape(180).Fit, 12);
    }

    [Fact]
    public void AVideoMayZoomAsFarAsTheSettingAllowsAndNeverBelowFit()
    {
        GalleryViewGeometry geometry = Landscape(maxPercent: 400);
        Assert.Equal(4, geometry.MaxZoom);
        Assert.Equal(4, geometry.ClampZoom(40));
        Assert.Equal(geometry.Fit, geometry.ClampZoom(0.01), 12);
        Assert.Equal(geometry.Fit, geometry.ClampZoom(double.NaN), 12);
        // A medium that only fits above the limit keeps a usable range: the maximum never undercuts Fit.
        GalleryViewGeometry tiny = new(8, 8, 1200, 800, 1, null, 200, 0);
        Assert.Equal(tiny.Fit, tiny.MaxZoom);
    }

    [Fact]
    public void TheDefaultLimitKeepsTheImageViewersSixteenFold()
    {
        GalleryViewGeometry geometry = Landscape();
        Assert.Equal(16, geometry.MaxZoom);
        Assert.Equal(GalleryGeometry.ClampZoom(50, geometry.Fit), geometry.ClampZoom(50));
        Assert.Equal(GalleryGeometry.ClampZoom(0.2, geometry.Fit), geometry.ClampZoom(0.2));
    }

    [Fact]
    public void LimitsClampToTheirDocumentedRange()
    {
        Assert.Equal((200, 1600, 6400), (GalleryZoomLimits.MaxPercentMinimum, GalleryZoomLimits.MaxPercentDefault, GalleryZoomLimits.MaxPercentMaximum));
        Assert.Equal(200, GalleryZoomLimits.ClampPercent(0));
        Assert.Equal(6400, GalleryZoomLimits.ClampPercent(int.MaxValue));
        Assert.Equal(1600, GalleryZoomLimits.ClampPercent(1600));
        Assert.Equal(2, Landscape(maxPercent: 0).MaxZoom); // A setting below the minimum is clamped to 200 %.
    }

    [Fact]
    public void ZoomStepsMultiplyByTheKeyFactorAndStopAtTheLimit()
    {
        GalleryViewGeometry geometry = Landscape(maxPercent: 400);
        GalleryViewPlacement view = GalleryViewMath.Fitted(geometry);
        Assert.False(view.Manual);
        GalleryViewPlacement next = GalleryViewMath.ZoomBy(view, geometry, 1.1);
        Assert.True(next.Manual);
        Assert.Equal(geometry.Fit * 1.1, next.Zoom, 12);
        for (int i = 0; i < 200; i++) next = GalleryViewMath.ZoomBy(next, geometry, 1.1);
        Assert.Equal(4, next.Zoom);
        Assert.True(next.Manual);
    }

    [Fact]
    public void ZoomingOutBelowFitReturnsToAnAutomaticFit()
    {
        GalleryViewGeometry geometry = Landscape();
        GalleryViewPlacement zoomed = GalleryViewMath.ZoomBy(GalleryViewMath.Fitted(geometry), geometry, 1.5);
        Assert.True(zoomed.Manual);
        GalleryViewPlacement back = GalleryViewMath.ZoomBy(zoomed, geometry, 1 / 1.5);
        Assert.False(back.Manual);
        Assert.Equal(geometry.Fit, back.Zoom, 12);
        Assert.Equal((0d, 0d), (back.PanX, back.PanY));
        // At Fit a further zoom out changes nothing (no flood of identical announcements).
        Assert.Equal(back, GalleryViewMath.ZoomBy(back, geometry, 1 / 1.1));
        Assert.Equal(back, GalleryViewMath.ZoomBy(back, geometry, double.NaN));
        Assert.Equal(back, GalleryViewMath.ZoomBy(back, geometry, -2));
    }

    [Fact]
    public void ActualPixelsIsOneToOneAndMayBeSmallerThanFitUntilZoomedBackOut()
    {
        // A small picture in a large viewport: Fit enlarges it to 2×, 1:1 is below Fit.
        GalleryViewGeometry geometry = new(800, 600, 1600, 1200, 1, null, 1600, 0);
        Assert.Equal(2, geometry.Fit, 12);
        GalleryViewPlacement actual = GalleryViewMath.ActualPixels(geometry);
        Assert.Equal(1, actual.Zoom);
        Assert.True(actual.Manual && actual.Actual);
        // From 1:1 zooming out stays (gently, never below 1:1); zooming in moves toward Fit and keeps the 1:1 mark until it arrives.
        Assert.Equal(actual, GalleryViewMath.ZoomBy(actual, geometry, 1 / 1.1));
        GalleryViewPlacement in1 = GalleryViewMath.ZoomBy(actual, geometry, 1.1);
        Assert.Equal(1.1, in1.Zoom, 12);
        Assert.True(in1.Actual);
        GalleryViewPlacement atFit = GalleryViewMath.ZoomBy(actual, geometry, 2.5);
        Assert.False(atFit.Actual);
        Assert.True(atFit.Manual);
        // A large picture: 1:1 is above Fit and zooming out leaves the 1:1 mark.
        GalleryViewGeometry big = new(8000, 6000, 1000, 700, 1, null, 1600, 0);
        Assert.True(big.Fit < 1);
        Assert.False(GalleryViewMath.ZoomBy(GalleryViewMath.ActualPixels(big), big, 1 / 1.1).Actual);
    }

    [Fact]
    public void TheWheelAnchorsTheZoomAtThePointer()
    {
        GalleryViewGeometry geometry = Landscape();
        GalleryViewPlacement view = GalleryViewMath.Fitted(geometry);
        // The pointer sits 200 dip right of the centre: after zooming in, that source spot is still under it.
        GalleryViewPlacement zoomed = GalleryViewMath.ZoomBy(view, geometry, 2, (200, 0));
        double ratio = zoomed.Zoom / view.Zoom;
        Assert.Equal(200 - 200 * ratio, zoomed.PanX, 9);
        Assert.Equal(0, zoomed.PanY, 9);
    }

    // ---- pan limits ----

    [Fact]
    public void ThereIsNoPanWhileTheMediumFitsAndItStopsAtTheEdgesWhenZoomedIn()
    {
        GalleryViewGeometry geometry = Landscape();
        Assert.Equal(0, geometry.PanX(500, geometry.Fit));
        Assert.Equal(0, geometry.PanY(-500, geometry.Fit));
        double zoom = geometry.Fit * 2; // 2560 × 1440 on screen in a 1280 × 720 viewport.
        Assert.Equal(640, geometry.PanX(10_000, zoom), 9);
        Assert.Equal(-640, geometry.PanX(-10_000, zoom), 9);
        Assert.Equal(360, geometry.PanY(10_000, zoom), 9);
        Assert.Equal(100, geometry.PanX(100, zoom));
    }

    [Fact]
    public void PanLimitsFollowTheRotatedShapeNotTheSourceShape()
    {
        GalleryViewGeometry turned = Landscape(90); // On screen 1080 wide, 1920 tall.
        double zoom = 1; // Physical pixel per source pixel.
        Assert.Equal(0, turned.PanX(900, zoom)); // 1080 < 1280: nothing to pan horizontally.
        Assert.Equal((1920 - 720) / 2d, turned.PanY(10_000, zoom), 9);
        GalleryViewGeometry upright = Landscape();
        Assert.Equal((1920 - 1280) / 2d, upright.PanX(10_000, zoom), 9);
        Assert.Equal((1080 - 720) / 2d, upright.PanY(10_000, zoom), 9);
    }

    [Fact]
    public void ADisplayScaleTurnsPhysicalZoomIntoDipForTheLimits()
    {
        GalleryViewGeometry geometry = Landscape(scale: 2);
        // 1:1 physical on a 2× display: 1920 source pixels are 960 dip.
        Assert.Equal(0, geometry.PanX(500, 1));
        Assert.Equal((1920 / 2d * 2 - 1280) / 2, geometry.PanX(10_000, 2), 9);
    }

    [Fact]
    public void DraggingBeyondTheEdgeIsClampedByClampedPlacement()
    {
        GalleryViewGeometry geometry = Landscape();
        GalleryViewPlacement view = new(geometry.Fit * 3, 9_999, -9_999, true, false);
        GalleryViewPlacement clamped = GalleryViewMath.Clamped(view, geometry);
        Assert.Equal(geometry.PanX(9_999, view.Zoom), clamped.PanX);
        Assert.Equal(geometry.PanY(-9_999, view.Zoom), clamped.PanY);
        Assert.True(clamped.PanX > 0 && clamped.PanY < 0);
    }

    [Fact]
    public void AViewportResizeKeepsFitAutomaticAndReclampsAManualZoom()
    {
        GalleryViewGeometry big = Landscape();
        GalleryViewPlacement fitted = GalleryViewMath.Fitted(big);
        GalleryViewGeometry small = big with { ViewportWidth = 640, ViewportHeight = 360 };
        GalleryViewPlacement refitted = GalleryViewMath.IfAutomatic(fitted, small);
        Assert.False(refitted.Manual);
        Assert.Equal(small.Fit, refitted.Zoom, 12);
        GalleryViewPlacement manual = new(big.Fit * 4, 400, 100, true, false);
        GalleryViewPlacement kept = GalleryViewMath.IfAutomatic(manual, big with { MaxZoomPercent = 200 });
        Assert.True(kept.Manual);
        Assert.Equal(2, kept.Zoom);
    }

    // ---- rotating the view ----

    [Fact]
    public void RotatingAFittedMediumFitsItAgainInItsNewOrientation()
    {
        GalleryViewGeometry upright = Landscape();
        GalleryViewGeometry turned = upright with { Rotation = 90 };
        GalleryViewPlacement view = GalleryViewMath.Rotated(GalleryViewMath.Fitted(upright), turned, 1);
        Assert.False(view.Manual);
        Assert.Equal(turned.Fit, view.Zoom, 12);
        Assert.True(view.Zoom < upright.Fit);
    }

    [Fact]
    public void RotatingAZoomedMediumTurnsThePanAndKeepsItInsideTheNewEdges()
    {
        GalleryViewGeometry upright = Landscape();
        GalleryViewGeometry turned = upright with { Rotation = 90 };
        GalleryViewPlacement before = new(2, 300, 100, true, false);
        GalleryViewPlacement after = GalleryViewMath.Rotated(before, turned, 1);
        Assert.True(after.Manual);
        Assert.Equal(2, after.Zoom);
        // (300, 100) turned a quarter clockwise is (-100, 300): inside the new limits (0 horizontal because 1080 fits? no: 2160 > 1280).
        Assert.Equal(turned.PanX(-100, 2), after.PanX);
        Assert.Equal(turned.PanY(300, 2), after.PanY);
        // Four turns bring the view back where it was.
        GalleryViewPlacement round = before;
        GalleryViewGeometry step = upright;
        for (int i = 0; i < 4; i++)
        {
            step = step with { Rotation = GalleryRotation.Next(step.Rotation) };
            round = GalleryViewMath.Rotated(round, step, 1);
        }
        Assert.Equal(0, step.Rotation);
        Assert.Equal(before.Zoom, round.Zoom, 9);
        Assert.Equal(before.PanX, round.PanX, 9);
        Assert.Equal(before.PanY, round.PanY, 9);
    }

    [Fact]
    public void RotatingAFittedPortraitFitsItsTurnedShapeAgain()
    {
        GalleryViewGeometry upright = Landscape();
        GalleryViewGeometry portrait = new GalleryViewGeometry(1080, 1920, 720, 1280, 1, null, 1600, 0);
        // A portrait video at Fit stays automatic after a quarter turn; only the zoom follows the new Fit.
        GalleryViewPlacement fitted = GalleryViewMath.Fitted(portrait);
        GalleryViewPlacement turned = GalleryViewMath.Rotated(fitted, portrait with { Rotation = 90 }, 1);
        Assert.False(turned.Manual);
        Assert.Equal((portrait with { Rotation = 90 }).Fit, turned.Zoom, 12);
        Assert.NotEqual(upright.Fit, turned.Zoom);
    }

    // ---- the video surface ----

    [Fact]
    public void AtFitTheSurfaceNeedsNoTransform()
    {
        GalleryViewGeometry geometry = Landscape();
        VideoSurfaceTransform transform = VideoSurfaceTransform.For(1920, 1080, 1280, 720, 1, GalleryViewMath.Fitted(geometry), 0);
        Assert.Equal(1, transform.Scale, 12);
        Assert.Equal((0d, 0d), (transform.TranslateX, transform.TranslateY));
        Matrix matrix = transform.ToMatrix();
        Assert.Equal(1, matrix.M11, 12); Assert.Equal(1, matrix.M22, 12);
        Assert.Equal(0, matrix.M12, 12); Assert.Equal(0, matrix.M21, 12);
        Assert.Equal((0d, 0d), (matrix.M31, matrix.M32));
    }

    [Fact]
    public void ZoomScalesTheFittedSurfaceByTheZoomRelativeToFit()
    {
        GalleryViewGeometry geometry = Landscape();
        GalleryViewPlacement view = new(geometry.Fit * 2.5, -120, 40, true, false);
        VideoSurfaceTransform transform = VideoSurfaceTransform.For(1920, 1080, 1280, 720, 1, view, 0);
        Assert.Equal(2.5, transform.Scale, 12);
        Assert.Equal((-120d, 40d), (transform.TranslateX, transform.TranslateY));
        // One to one: 1920 source pixels on a 1280 dip wide cell is 1.5 times the fitted size.
        Assert.Equal(1.5, VideoSurfaceTransform.For(1920, 1080, 1280, 720, 1, GalleryViewMath.ActualPixels(geometry), 0).Scale, 12);
    }

    [Fact]
    public void ARotatedFitScalesTheTurnedPictureIntoTheCell()
    {
        GalleryViewGeometry turned = Landscape(90);
        VideoSurfaceTransform transform = VideoSurfaceTransform.For(1920, 1080, 1280, 720, 1, GalleryViewMath.Fitted(turned), 90);
        // The 16:9 picture turned upright is limited by the cell's height: 720 of the 1920 it would need … in dip 720/1080 of its width.
        Assert.Equal(720d / 1920 / (720d / 1080), transform.Scale, 12);
        Assert.True(transform.Scale < 1);
        Assert.Equal(90, transform.Rotation);
    }

    [Fact]
    public void TheSurfaceMatrixScalesThenTurnsClockwiseThenMovesTheView()
    {
        VideoSurfaceTransform transform = new(2, 90, 10, 20);
        Matrix matrix = transform.ToMatrix();
        // (1, 0) scaled to (2, 0), turned a quarter clockwise on a y-down screen to (0, 2), then moved by (10, 20).
        Point mapped = matrix.Transform(new Point(1, 0));
        Assert.Equal(10, mapped.X, 9);
        Assert.Equal(22, mapped.Y, 9);
        Assert.Equal(new Point(30, 0), new VideoSurfaceTransform(1, 0, 30, 0).ToMatrix().Transform(new Point(0, 0)));
    }

    [Fact]
    public void ADegenerateSurfaceOrSourceNeverProducesANonFiniteMatrix()
    {
        GalleryViewPlacement view = new(1, 0, 0, true, false);
        Assert.Equal(VideoSurfaceTransform.Identity, VideoSurfaceTransform.For(0, 0, 100, 100, 1, view, 0));
        Assert.Equal(VideoSurfaceTransform.Identity, VideoSurfaceTransform.For(100, 100, 0, 100, 1, view, 0));
        Assert.Equal(VideoSurfaceTransform.Identity, VideoSurfaceTransform.For(100, 100, 100, 100, 0, view, 0));
        Assert.Equal(VideoSurfaceTransform.Identity, VideoSurfaceTransform.For(100, 100, 100, 100, 1, view with { Zoom = double.NaN }, 0));
    }

    // ---- the native (Windows) crop ----

    [Fact]
    public void TheWholePictureNeedsNoCropAtFitOrWhenNotZoomed()
    {
        GalleryViewGeometry geometry = Landscape();
        VideoNativeCrop crop = VideoNativeCrop.For(1920, 1080, geometry.Fit, 1, GalleryViewMath.Fitted(geometry));
        Assert.True(crop.IsFull);
        Assert.Equal(string.Empty, crop.Geometry);
        Assert.Equal((0, 0, 1920, 1080), (crop.Left, crop.Top, crop.Width, crop.Height));
    }

    [Fact]
    public void ZoomShowsTheCentreCropAndAPanMovesTheCropTheOtherWay()
    {
        double fit = 1280d / 1920;
        VideoNativeCrop centred = VideoNativeCrop.For(1920, 1080, fit, 1, new GalleryViewPlacement(fit * 2, 0, 0, true, false));
        Assert.Equal((480, 270, 960, 540), (centred.Left, centred.Top, centred.Width, centred.Height));
        Assert.Equal("960x540+480+270", centred.Geometry);
        // Dragging the picture 100 dip to the right shows 100 / 1.3333 source pixels more of its left side.
        VideoNativeCrop panned = VideoNativeCrop.For(1920, 1080, fit, 1, new GalleryViewPlacement(fit * 2, 100, 0, true, false));
        Assert.Equal(480 - (int)Math.Round(100 / (fit * 2)), panned.Left);
        Assert.Equal(270, panned.Top);
    }

    [Fact]
    public void TheCropNeverLeavesThePicture()
    {
        double fit = 1280d / 1920;
        VideoNativeCrop far = VideoNativeCrop.For(1920, 1080, fit, 1, new GalleryViewPlacement(fit * 4, -99_999, 99_999, true, false));
        Assert.Equal(1920 - far.Width, far.Left);
        Assert.Equal(0, far.Top);
        Assert.True(far.Left + far.Width <= 1920 && far.Top + far.Height <= 1080);
        Assert.True(VideoNativeCrop.For(1920, 1080, fit, 1, new GalleryViewPlacement(fit * 4, double.NaN, 0, true, false)).Left >= 0);
    }

    [Fact]
    public void TheNativeRotationUsesLibVlcsTransformFilterAndZeroRemovesIt()
    {
        Assert.Equal(new[] { ":video-filter=transform", ":transform-type=90" }, GalleryVideoPlayer.RotationOptions(90));
        Assert.Equal(new[] { ":video-filter=transform", ":transform-type=270" }, GalleryVideoPlayer.RotationOptions(-90));
        Assert.Equal(new[] { ":video-filter=transform", ":transform-type=180" }, GalleryVideoPlayer.RotationOptions(540));
        Assert.Equal(new[] { ":video-filter=" }, GalleryVideoPlayer.RotationOptions(360));
    }

    // ---- reset on media change ----

    [Fact]
    public void ANewMediumStartsUprightAndTheSameMediumShownAgainKeepsItsRotation()
    {
        Assert.Equal(0, GalleryViewMath.RotationForMedium(sameMedium: false, 270));
        Assert.Equal(270, GalleryViewMath.RotationForMedium(sameMedium: true, 270));
        Assert.Equal(90, GalleryViewMath.RotationForMedium(sameMedium: true, 450));
    }

    [Fact]
    public void TheViewerResetsZoomPanAndRotationWhenAMediumChangesAndDrivesTheVideoWithTheSameRules()
    {
        string viewer = Source("Desktop/GalleryViewer.cs"), transform = Source("Desktop/GalleryViewer.ViewTransform.cs");
        // The reset runs before the new entry is adopted, next to the zoom reset that always existed.
        int reset = viewer.IndexOf("ResetViewForMedium(entry);", StringComparison.Ordinal);
        int adopt = viewer.IndexOf("_entry = entry; _manualZoom = _actualZoom = false; _panX = _panY = 0;", StringComparison.Ordinal);
        Assert.True(reset > 0 && adopt > reset);
        Assert.Contains("GalleryViewMath.RotationForMedium(same, _rotation)", transform, StringComparison.Ordinal);
        // The image path, the video path and the keys use one geometry and one set of steps.
        Assert.Contains("GalleryViewMath.ZoomBy(before, geometry, factor", transform, StringComparison.Ordinal);
        Assert.Contains("_video!.ApplyViewTransform(geometry, Placement, DisplayScale)", transform, StringComparison.Ordinal);
        Assert.Contains("CommandBindings.Matches(e, \"gallery.rotate\")) RotateView()", viewer, StringComparison.Ordinal);
        Assert.Contains("CommandBindings.Matches(e, \"gallery.zoom-in\")) Zoom(1.1)", viewer, StringComparison.Ordinal);
        // The state that survives the reader carries the rotation of the same medium.
        Assert.Contains("_actualZoom, _rotation)", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSoftwareSurfaceIsOnlyTransformedNeverReDecodedAndLivesInAClippingHost()
    {
        string player = Source("Desktop/GalleryVideoPlayer.ViewTransform.cs");
        Assert.Contains("ClipToBounds = true", player, StringComparison.Ordinal);
        Assert.Contains("_surfaceTransform.Matrix = VideoSurfaceTransform.For(", player, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop()", Between(player, "internal void ApplyViewTransform", "private void ApplyNativeView"), StringComparison.Ordinal);
        // Transforming the picture must not touch the surface that decodes it.
        Assert.DoesNotContain("new SoftwareVideoSurface", player, StringComparison.Ordinal);
    }

    // ---- keys ----

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ShiftRRotatesTheGalleryMediumAndTheDefaultTableStaysConflictFree(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal("Shift+R", bindings.For("gallery.rotate").Single().Display(ShortcutPlatform.Linux));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.R, KeyModifiers = KeyModifiers.Shift }, "gallery.rotate"));
        // R alone stays the rating, Primary+Shift+R the board filters.
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.R }, "gallery.rotate"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.R }, "media.rating.edit"));
    }

    [Fact]
    public void TheRotateCommandIsAGalleryCommandWithAYieldingDefaultAndNoRevisionBumpOfItsOwn()
    {
        CommandDefinition command = CommandCatalog.Get("gallery.rotate");
        Assert.Equal(CommandScope.Gallery, command.Scope);
        Assert.True(command.IsImplemented);
        Assert.Equal("rotate-cw", command.Icon);
        Assert.Contains("gallery.rotate", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.True(EffectiveShortcutBindings.YieldsOn("gallery.rotate", ShortcutPlatform.Linux));
        Assert.True(EffectiveShortcutBindings.YieldsOn("gallery.rotate", ShortcutPlatform.MacOS));
        Assert.Equal("Rotate 90°", CommandCatalog.Label("gallery.rotate"));
        Texts.SetLanguage("de");
        Assert.Equal("90° drehen", CommandCatalog.Label("gallery.rotate"));
    }

    [Fact]
    public void ASavedBindingOfAnotherCommandOnShiftRWinsOverTheNewDefault()
    {
        ShortcutBindingsSection saved = new ShortcutBindingsSection().Set("favorites.toggle", [ShortcutGesture.Parse("Shift+R")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(saved, ShortcutPlatform.Linux);
        Assert.Equal("Shift+R", bindings.Display("favorites.toggle"));
        Assert.DoesNotContain(bindings.For("gallery.rotate"), gesture => gesture.Display(ShortcutPlatform.Linux) == "Shift+R");
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Fact]
    public void TheZoomKeysAndTheirDefaultsAreUnchanged()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.Equal("Plus", CommandCatalog.Get("gallery.zoom-in").Shortcut);
        Assert.Equal("Minus", CommandCatalog.Get("gallery.zoom-out").Shortcut);
        Assert.Equal("Primary+0", CommandCatalog.Get("gallery.fit").Shortcut);
        Assert.Equal("Z", CommandCatalog.Get("gallery.actual-size").Shortcut);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.D0, KeyModifiers = KeyModifiers.Control }, "gallery.fit"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.NumPad0 }, "gallery.fit"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Z }, "gallery.actual-size"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.NumPad1 }, "gallery.actual-size"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.D0 }, "gallery.fit"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.D1 }, "gallery.actual-size"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Add }, "gallery.zoom-in"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.R, KeyModifiers = KeyModifiers.Shift }, "gallery.zoom-in"));
    }

    // ---- gallery work views ----

    [Fact]
    public void TheRotateRowSitsInTheFlyoutOfEveryViewAndNeverOnTheBar()
    {
        WorkViewEntry row = WorkViewPolicy.Get("gallery.menu.rotate");
        Assert.Equal("gallery.rotate", row.CommandId);
        foreach (WorkView view in WorkViewSetting.All)
        {
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("gallery.menu.rotate", view));
            Assert.DoesNotContain("gallery.menu.rotate", WorkViewPolicy.GalleryBarToolIds(view));
        }
        Assert.Contains("WorkViewElement.SetId(_rotate, \"gallery.menu.rotate\")", Source("Desktop/GalleryViewer.WorkView.cs"), StringComparison.Ordinal);
    }

    // ---- spoken state and texts ----

    [Fact]
    public void ZoomAndRotationAreSpokenAsOneShortSentence()
    {
        Assert.Equal("Zoom 250%", GalleryViewAnnouncement.Compose(true, false, true, 2.5, 0));
        Assert.Equal("Fit to window", GalleryViewAnnouncement.Compose(true, false, false, 0.67, 0));
        Assert.Equal("Rotated 90 degrees", GalleryViewAnnouncement.Compose(false, true, false, 1, 90));
        Assert.Equal("Not rotated", GalleryViewAnnouncement.Compose(false, true, false, 1, 360));
        Assert.Equal("Fit to window, Rotated 270 degrees", GalleryViewAnnouncement.Compose(true, true, false, 1, -90));
        Assert.Equal(string.Empty, GalleryViewAnnouncement.Compose(false, false, true, 3, 90));
        Texts.SetLanguage("de");
        Assert.Equal("Zoom 250 %", GalleryViewAnnouncement.Compose(true, false, true, 2.5, 0));
        Assert.Equal("Um 180 Grad gedreht", GalleryViewAnnouncement.Compose(false, true, false, 1, 180));
    }

    [Fact]
    public void TheViewerOwnsAPoliteLiveRegionAndBundlesStepsIntoOneMessage()
    {
        string transform = Source("Desktop/GalleryViewer.ViewTransform.cs");
        Assert.Contains("AutomationProperties.SetLiveSetting(_viewAnnouncement, AutomationLiveSetting.Polite)", transform, StringComparison.Ordinal);
        Assert.Equal(400, GalleryViewer.ViewAnnouncementDelayMs);
    }

    [Theory]
    [InlineData("viewer.view.zoomAnnounce")] [InlineData("viewer.view.fitAnnounce")] [InlineData("viewer.view.rotationAnnounce")]
    [InlineData("viewer.view.rotationOff")] [InlineData("viewer.view.rotationState")] [InlineData("command.gallery.rotate")]
    [InlineData("settings.gallery.maxZoom")] [InlineData("settings.gallery.maxZoom.hint")]
    public void EveryNewTextHasARealTranslationInEveryPack(string key)
    {
        string english = Texts.T(key);
        Assert.False(string.IsNullOrWhiteSpace(english));
        foreach (string language in new[] { "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            string localized = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(localized), language + " " + key);
            Assert.NotEqual(key, localized);
            if (key == "viewer.view.rotationState" || key.StartsWith("viewer.view.zoom", StringComparison.Ordinal) || key.EndsWith("Announce", StringComparison.Ordinal))
                Assert.Equal(english.Contains("{0}"), localized.Contains("{0}"));
        }
    }

    [Fact]
    public void TheInfoLineAndThePlayerMenuReachTheSameRotation()
    {
        string player = Source("Desktop/GalleryVideoPlayer.Transport.cs"), viewer = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("CreateMenuItem(\"gallery.rotate\"", player, StringComparison.Ordinal);
        Assert.Contains("_video.RotateRequested += () => RotateView();", viewer, StringComparison.Ordinal);
        Assert.Contains("\"GalleryRotate\" => \"gallery.rotate\"", viewer, StringComparison.Ordinal);
    }

    // ---- settings ----

    [Fact]
    public void TheMaximumZoomIsASettingWithDefaultMinimumAndMaximum()
    {
        Assert.Equal(GalleryZoomLimits.MaxPercentDefault, new Config().GalleryMaxZoomPercent);
        Assert.Equal((200, 6400), (Config.GalleryMaxZoomPercentMinimum, Config.GalleryMaxZoomPercentMaximum));
        Config config = new() { GalleryMaxZoomPercent = 6400 };
        config.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { GalleryMaxZoomPercent = 199 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { GalleryMaxZoomPercent = 6401 }.Validate());
        Assert.Contains("ConfigureZoomLimits(_config.GalleryMaxZoomPercent)", Source("Desktop/Shell/MainWindow.GalleryPreferences.cs"), StringComparison.Ordinal);
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, relative));
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal), to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, start);
        return text[from..to];
    }
}
