using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static partial class ReviewArtifacts
{
    // Transform the very same PNG, not a second render or a product theme override.
    private static void Grayscale(string directory)
    {
        using SKImage image = SKImage.FromEncodedData(Path.Combine(directory, "screenshot.png"));
        using SKSurface surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height));
        using SKColorFilter filter = SKColorFilter.CreateColorMatrix(new float[]
        {
            .2126f, .7152f, .0722f, 0, 0,
            .2126f, .7152f, .0722f, 0, 0,
            .2126f, .7152f, .0722f, 0, 0,
            0, 0, 0, 1, 0
        });
        using SKPaint paint = new() { ColorFilter = filter };
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, 0, 0, paint);
        using SKImage gray = surface.Snapshot();
        using SKData png = gray.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream output = File.Create(Path.Combine(directory, "grayscale.png"));
        png.SaveTo(output);
    }
}
