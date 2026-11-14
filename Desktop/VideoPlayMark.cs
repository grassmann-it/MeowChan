using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.Desktop;

/// <summary>
/// The size and place of the video play mark on a preview (pure functions, no control). The mark scales with the picture that is
/// really shown: <see cref="Ratio"/> of its shorter side, between <see cref="MinimumDiameter"/> and <see cref="MaximumDiameter"/>, and
/// never larger than that shorter side, so it cannot overflow a tiny picture. It sits on the centre of the shown picture, not of the
/// slot that holds it. The mark itself is never a click target (it is not hit-testable): the whole picture stays the target, which
/// keeps a circle of less than 24 dip, and any larger one, as easy to hit as the picture is.
/// </summary>
internal static class VideoPlayMark
{
    /// <summary>The diameter as a share of the picture's shorter side.</summary>
    internal const double Ratio = 0.45;
    /// <summary>The smallest and the largest circle (dip); a picture shorter than the smallest gets a circle as small as its shorter side.</summary>
    internal const double MinimumDiameter = 20, MaximumDiameter = 44;
    /// <summary>The triangle's share of the circle, as the 44 dip badge had it (14 x 18 dip).</summary>
    internal const double GlyphWidthRatio = 14d / 44, GlyphHeightRatio = 18d / 44;

    /// <summary>The circle's diameter on a picture of this size; 0 when there is no usable picture.</summary>
    internal static double Diameter(Size picture)
    {
        double shorter = Math.Min(picture.Width, picture.Height);
        if (!double.IsFinite(shorter) || shorter <= 0) return 0;
        return Math.Min(shorter, Math.Clamp(Ratio * shorter, MinimumDiameter, MaximumDiameter));
    }

    /// <summary>The triangle's bounding box for a circle of this diameter.</summary>
    internal static Size GlyphSize(double diameter) => new(diameter * GlyphWidthRatio, diameter * GlyphHeightRatio);

    /// <summary>
    /// How far right of the circle's centre the triangle's bounding box sits: a sixth of its width puts the triangle's centre of mass
    /// (a third from its base), not its box, on the circle's centre. The optical offset the 44 dip badge had (2 of 14 dip), scaled.
    /// </summary>
    internal static double GlyphOffset(double diameter) => GlyphSize(diameter).Width / 6;

    /// <summary>The circle's square on the picture's rectangle: centred on it, inside it.</summary>
    internal static Rect Place(Rect picture)
    {
        double diameter = Diameter(picture.Size);
        return new Rect(picture.Center.X - diameter / 2, picture.Center.Y - diameter / 2, diameter, diameter);
    }
}

/// <summary>
/// The play mark: a disc with a triangle. It takes its size from <see cref="Diameter"/> (the owner derives it from the shown picture
/// with <see cref="VideoPlayMark.Diameter"/>), has the same dark look in both themes and is not hit-testable.
/// </summary>
internal sealed class VideoPlayMarkView : Border
{
    private readonly Path _glyph = new()
    {
        Stretch = Stretch.Uniform, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private double _diameter = double.NaN;

    internal VideoPlayMarkView()
    {
        IsHitTestVisible = false;
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        // Theme-independent on purpose (palette review, item 4): the mark lies on arbitrary pictures, and a light disc on a light picture
        // has too little contrast. Dark disc, white triangle and ring in both themes.
        Background = new SolidColorBrush(Color.FromArgb(205, 13, 16, 22));
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        _glyph.Fill = Brushes.White;
        // Parsed on first use from the UI path: Geometry.Parse needs the render platform, never a static initializer.
        _glyph.AttachedToVisualTree += (_, _) => _glyph.Data ??= Geometry.Parse("M 0,0 L 0,18 L 14,9 Z");
        Child = _glyph;
        Diameter = VideoPlayMark.MaximumDiameter;
    }

    /// <summary>The circle's diameter (dip); the triangle and its optical offset follow.</summary>
    internal double Diameter
    {
        get => _diameter;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value == _diameter) return;
            _diameter = value;
            Width = Height = value;
            CornerRadius = new CornerRadius(value / 2);
            Size glyph = VideoPlayMark.GlyphSize(value);
            _glyph.Width = glyph.Width; _glyph.Height = glyph.Height;
            // A left margin of twice the offset moves the centred box by the offset.
            _glyph.Margin = new Thickness(VideoPlayMark.GlyphOffset(value) * 2, 0, 0, 0);
        }
    }
}
