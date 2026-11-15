using System;
using System.Globalization;
using Avalonia.Media;

namespace MeowChan.Desktop.Theming;

/// <summary>A colour as the picker shows it: hue in degrees (0 up to, not including, 360), saturation and brightness from 0 to 1.</summary>
internal readonly record struct PickerHsv(double Hue, double Saturation, double Value)
{
    /// <summary>The same colour with every part brought into its range (the hue wraps around).</summary>
    internal PickerHsv Clamped() => new(((Hue % 360) + 360) % 360, Math.Clamp(Saturation, 0, 1), Math.Clamp(Value, 0, 1));
}

/// <summary>
/// TH3: the arithmetic of the colour picker and its hex field, as pure functions. HSV is the picker's model (hue, saturation,
/// brightness sliders); the colour itself is always an opaque RGB colour, stored as <c>#RRGGBB</c>. Going from a colour to HSV
/// and back gives the same colour (sampled in the round-trip test: a coarse grid of the whole cube plus 200000 random colours); going from HSV to a colour
/// and back may move a slider by less than the step of one colour, so the picker keeps its own sliders while the user drags.
/// </summary>
internal static class ThemeColorMath
{
    /// <summary>The picker's HSV reading of an opaque colour (a transparent colour is read by its RGB).</summary>
    internal static PickerHsv ToHsv(Color color)
    {
        HsvColor hsv = color.ToHsv();
        return new PickerHsv(hsv.H >= 360 ? 0 : hsv.H, hsv.S, hsv.V);
    }

    /// <summary>The opaque colour of an HSV reading (parts outside their range are brought into it first).</summary>
    internal static Color FromHsv(PickerHsv hsv)
    {
        PickerHsv value = hsv.Clamped();
        return new HsvColor(1, value.Hue, value.Saturation, value.Value).ToRgb();
    }

    /// <summary><c>#RRGGBB</c> in upper case, the way the settings store a colour; alpha is not shown (the roles are opaque).</summary>
    internal static string Format(Color color) => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>
    /// The hex field: <c>#RRGGBB</c> or <c>RRGGBB</c> (either case, blanks around it ignored). Anything else, including the short
    /// <c>#RGB</c> form and a colour with alpha, is not accepted: false, and <paramref name="color"/> is default.
    /// </summary>
    internal static bool TryParse(string? text, out Color color)
    {
        color = default;
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (trimmed[0] == '#') trimmed = trimmed[1..];
        if (trimmed.Length != 6 || trimmed.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0) return false;
        color = ThemeCustomization.ParseHex("#" + trimmed);
        return true;
    }

    /// <summary>
    /// Ratio as the dialog shows it: cut (not rounded) to one decimal, so "4.5 : 1" never hides a 4.46 that the gate refuses.
    /// </summary>
    internal static double DisplayRatio(double ratio) => Math.Floor(ratio * 10 + 1e-9) / 10;
}
