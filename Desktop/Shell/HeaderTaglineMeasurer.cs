using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MeowChan.Desktop;

/// <summary>
/// Shapes each form without a width constraint, using the header text block's typography and font fallbacks.
/// The cached metrics include glyph ink beyond the advances. Only the pixel rounding depends on the monitor scale.
/// </summary>
internal sealed class HeaderTaglineMeasurer
{
    private const int MaximumEntries = 256;

    private readonly record struct Key(string Text, Typeface Typeface, double Size, FlowDirection Direction,
        double LetterSpacing, FontFeatureCollection? Features);

    private readonly record struct Metrics(double RightEdge, double LeadingBearing);
    internal readonly record struct Footprint(double Width, double LeftInset);

    private readonly Dictionary<Key, Metrics> _cache = [];

    internal Footprint Measure(string text, TextBlock reference, double scale)
    {
        if (string.IsNullOrEmpty(text)) return default;
        Typeface typeface = new(reference.FontFamily, reference.FontStyle, reference.FontWeight, reference.FontStretch);
        Key key = new(text, typeface, reference.FontSize, reference.FlowDirection, reference.LetterSpacing, reference.FontFeatures);
        if (!_cache.TryGetValue(key, out Metrics metrics))
        {
            metrics = Shape(key);
            if (_cache.Count >= MaximumEntries) _cache.Clear();
            _cache[key] = metrics;
        }

        if (metrics.RightEdge <= 0) return default;
        // Keep one physical pixel clear at the leading clip edge, including any negative bearing.
        double leftInset = Math.Ceiling(Math.Max(0, 1 - metrics.LeadingBearing * scale)) / scale;
        double width = Math.Ceiling(metrics.RightEdge * scale) / scale + leftInset;
        return new Footprint(width, leftInset);
    }

    private static Metrics Shape(Key key)
    {
        using TextLayout layout = new(key.Text, key.Typeface, key.Size, textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap, textTrimming: TextTrimming.None, flowDirection: key.Direction,
            letterSpacing: key.LetterSpacing, fontFeatures: key.Features);
        if (layout.TextLines.Count != 1) return default;

        TextLine line = layout.TextLines[0];
        // Avalonia reports negative bearings when ink extends beyond the logical line.
        double rightEdge = line.WidthIncludingTrailingWhitespace - Math.Min(0, line.OverhangTrailing);
        return new Metrics(rightEdge, line.OverhangLeading);
    }
}
