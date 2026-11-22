using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Avalonia;

namespace MeowChan;

public partial class Config
{
    [JsonInclude, JsonConverter(typeof(DesktopPreferenceStringConverter))]
    internal string BooruViewerPlacement { get; set; } = string.Empty;

    internal (Size Size, PixelPoint Position)? ReadBooruViewerBounds()
    {
        string[] bounds = BooruViewerPlacement.Split(' ', 5);
        if (bounds.Length != 4
            || !double.TryParse(bounds[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(bounds[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            || !int.TryParse(bounds[2], CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(bounds[3], CultureInfo.InvariantCulture, out int y)
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return null;
        return (new Size(width, height), new PixelPoint(x, y));
    }

    internal void WriteBooruViewerBounds((Size Size, PixelPoint Position) bounds) =>
        BooruViewerPlacement = FormattableString.Invariant(
            $"{bounds.Size.Width:R} {bounds.Size.Height:R} {bounds.Position.X} {bounds.Position.Y}");

    public int BooruGalleryMaximumPixels { get; set; } = BooruGalleryMaximumPixelsDefault;
    internal const int BooruGalleryMaximumPixelsDefault = 40_000_000;
    internal const int BooruGalleryMaximumPixelsMinimum = 1_000_000;
    internal const int BooruGalleryMaximumPixelsMaximum = 80_000_000;

    public int BooruGalleryMaximumWidth { get; set; } = BooruGalleryMaximumWidthDefault;
    internal const int BooruGalleryMaximumWidthDefault = 2_048;
    internal const int BooruGalleryMaximumWidthMinimum = 256;
    internal const int BooruGalleryMaximumWidthMaximum = 4_096;

    public int BooruGalleryTimeoutSeconds { get; set; } = BooruGalleryTimeoutSecondsDefault;
    internal const int BooruGalleryTimeoutSecondsDefault = 60;
    internal const int BooruGalleryTimeoutSecondsMinimum = 1;
    internal const int BooruGalleryTimeoutSecondsMaximum = 90;

    private void ValidateBooruGalleryLimits()
    {
        if (BooruGalleryMaximumPixels is < BooruGalleryMaximumPixelsMinimum or > BooruGalleryMaximumPixelsMaximum)
            throw new ArgumentOutOfRangeException(nameof(BooruGalleryMaximumPixels), $"Supported range is {BooruGalleryMaximumPixelsMinimum} to {BooruGalleryMaximumPixelsMaximum}.");
        if (BooruGalleryMaximumWidth is < BooruGalleryMaximumWidthMinimum or > BooruGalleryMaximumWidthMaximum)
            throw new ArgumentOutOfRangeException(nameof(BooruGalleryMaximumWidth), $"Supported range is {BooruGalleryMaximumWidthMinimum} to {BooruGalleryMaximumWidthMaximum}.");
        if (BooruGalleryTimeoutSeconds is < BooruGalleryTimeoutSecondsMinimum or > BooruGalleryTimeoutSecondsMaximum)
            throw new ArgumentOutOfRangeException(nameof(BooruGalleryTimeoutSeconds), $"Supported range is {BooruGalleryTimeoutSecondsMinimum} to {BooruGalleryTimeoutSecondsMaximum}.");
    }

    private void CopyBooruGalleryLimitsFrom(Config source)
    {
        BooruGalleryMaximumPixels = source.BooruGalleryMaximumPixels;
        BooruGalleryMaximumWidth = source.BooruGalleryMaximumWidth;
        BooruGalleryTimeoutSeconds = source.BooruGalleryTimeoutSeconds;
    }
}
