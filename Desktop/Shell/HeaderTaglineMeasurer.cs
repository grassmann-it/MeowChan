using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MeowChan.Desktop;

/// <summary>
/// Measures how wide a tagline text is in the font of the header's text block, with a <see cref="TextLayout"/> that does not trim or wrap
/// (so a cut-off text is impossible to mistake for a fitting one). A width is measured once per (text, font, size) and then served from the
/// cache: the header asks again only after a width change of its column, a language change or a new quip, never in a layout pass.
/// </summary>
internal sealed class HeaderTaglineMeasurer
{
    // Texts × the two forms × a handful of languages; far above anything a session produces, so the cap is never hit in practice.
    private const int MaximumEntries = 256;

    private readonly record struct Key(string Text, string Family, double Size, FontStyle Style, FontWeight Weight, FontStretch Stretch);

    private readonly Dictionary<Key, double> _cache = [];

    /// <summary>How many widths were really measured (not served from the cache); for tests and diagnostics.</summary>
    internal int Measurements { get; private set; }

    /// <summary>The width in dip of <paramref name="text"/> in the font, size and style of <paramref name="reference"/>, rounded up to whole dip.</summary>
    internal double Width(string text, TextBlock reference) =>
        Width(text, new Typeface(reference.FontFamily, reference.FontStyle, reference.FontWeight, reference.FontStretch), reference.FontSize);

    internal double Width(string text, Typeface typeface, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        Key key = new(text, typeface.FontFamily.ToString(), fontSize, typeface.Style, typeface.Weight, typeface.Stretch);
        if (_cache.TryGetValue(key, out double known)) return known;
        TextLayout layout = new(text, typeface, fontSize, null, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None);
        double width = Math.Ceiling(layout.WidthIncludingTrailingWhitespace);
        layout.Dispose();
        Measurements++;
        if (_cache.Count >= MaximumEntries) _cache.Clear();
        _cache[key] = width;
        return width;
    }
}
