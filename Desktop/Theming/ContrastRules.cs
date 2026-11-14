using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeowChan.Desktop.Theming;

/// <summary>The base palette selects the existing Light/Dark-specific contrast contracts.</summary>
public enum ContrastTheme { Dark, Light }

/// <summary>Resolved resource colours, including control-state mirrors, not just theme seeds. No UI objects.</summary>
public sealed record ContrastPalette(ContrastTheme Theme, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// A minimum contrast between two resource keys. When <paramref name="Backdrop"/> is set, Background is an
/// ARGB scrim composited over that opaque resource first. Other pairs require opaque RGB colours.
/// Pins, mirror equality and UI structure remain the responsibility of the palette tests.
/// </summary>
public sealed record ContrastRule(string Foreground, string Background, double Minimum, string? Backdrop = null);

/// <summary>Invalid/missing input is a failure too: it must never look like a passing contrast check.</summary>
public enum ContrastFailureKind { MissingColor, InvalidColor, InsufficientContrast }

/// <summary>A failed rule, with the unrounded ratio when measurable, or the offending input key.</summary>
public sealed record ContrastFailure(ContrastRule Rule, ContrastFailureKind Kind, string? Key, double? Ratio);

/// <summary>
/// Palette review contracts extracted from PaletteContrastTests. Pure data and WCAG 2.2/sRGB arithmetic;
/// no resources are loaded or applied. Text 4.5 and graphics 3.0 remain distinct, as do the reviewed exceptions.
/// A caller decides how failures are presented; this class does not introduce a runtime theme policy.
/// </summary>
public static class ContrastRules
{
    private static readonly IReadOnlyList<ContrastRule> Dark = Build(ContrastTheme.Dark);
    private static readonly IReadOnlyList<ContrastRule> Light = Build(ContrastTheme.Light);

    /// <summary>All dictionary-based contrast pairs for a base, including the reviewed state-specific minima.</summary>
    public static IReadOnlyList<ContrastRule> For(ContrastTheme theme) => theme switch
    {
        ContrastTheme.Dark => Dark,
        ContrastTheme.Light => Light,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    /// <summary>
    /// Check every registered pair, or an explicit subset. Missing keys and malformed/unsupported colours are
    /// reported per rule; other pairs still run. Values are strict RGB, or ARGB for an explicit scrim background.
    /// Literal-colour/style pins and perceptual status separation are not palette-key contrast pairs.
    /// </summary>
    public static IReadOnlyList<ContrastFailure> Check(ContrastPalette palette, IEnumerable<ContrastRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(palette.Values);
        var failures = new List<ContrastFailure>();
        foreach (ContrastRule rule in rules ?? For(palette.Theme))
        {
            if (!double.IsFinite(rule.Minimum) || rule.Minimum < 1 || rule.Minimum > 21)
                throw new ArgumentOutOfRangeException(nameof(rules), "Contrast minima must be finite and between 1 and 21.");
            bool valid = Read(rule.Foreground, false, out string foreground);
            valid &= Read(rule.Background, rule.Backdrop != null, out string background);
            string backdrop = "";
            if (rule.Backdrop != null) valid &= Read(rule.Backdrop, false, out backdrop);
            if (!valid) continue;
            if (rule.Backdrop != null) background = CompositeOver(background, backdrop);
            double ratio = Contrast(foreground, background);
            if (ratio < rule.Minimum)
                failures.Add(new(rule, ContrastFailureKind.InsufficientContrast, null, ratio));

            bool Read(string key, bool alpha, out string value)
            {
                if (!palette.Values.TryGetValue(key, out string? color))
                {
                    value = "";
                    failures.Add(new(rule, ContrastFailureKind.MissingColor, key, null));
                    return false;
                }
                value = color!;
                if (IsColor(color, alpha)) return true;
                failures.Add(new(rule, ContrastFailureKind.InvalidColor, key, null));
                return false;
            }
        }
        return failures.AsReadOnly();
    }

    /// <summary>WCAG contrast of two opaque RGB colours, without display rounding.</summary>
    public static double Contrast(string first, string second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG 2.2 relative luminance; the sRGB breakpoint is 0.04045.</summary>
    public static double Luminance(string hex)
    {
        double[] c = Linear(hex);
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    }

    /// <summary>
    /// Composite in sRGB, rounding each channel to an integer with Math.Round's existing to-even semantics.
    /// Do not linearise first: palette review measured these compositor values, not a linear-light blend.
    /// </summary>
    public static string Blend(string top, string under, double opacity)
    {
        RequireColor(top, false);
        RequireColor(under, false);
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        return "#" + string.Concat(Enumerable.Range(0, 3).Select(index =>
            ((int)Math.Round(Channel(top, index) * opacity + Channel(under, index) * (1 - opacity)))
                .ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Composite an RGB/ARGB colour over an opaque RGB backdrop; alpha is never treated as red.</summary>
    public static string CompositeOver(string top, string under)
    {
        RequireColor(top, true);
        RequireColor(under, false);
        return top.Length == 9 ? Blend("#" + top[3..], under, Channel(top, 0) / 255d) : top;
    }

    /// <summary>
    /// Original dimmed-chip semantics: label and surface each blended over the same underlay, not the label
    /// over the blended surface. Kept separate so a later checker cannot accidentally change group opacity.
    /// </summary>
    public static double CompositeContrast(string foreground, string surface, double opacity, string under) =>
        Contrast(Blend(foreground, under, opacity), Blend(surface, under, opacity));

    /// <summary>CIELAB dE76 used by the status-colour separation pins (not a WCAG contrast ratio).</summary>
    public static double DeltaE(string first, string second)
    {
        double[] a = Lab(first), b = Lab(second);
        return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    }

    private static double[] Linear(string hex)
    {
        RequireColor(hex, false);
        return Enumerable.Range(0, 3).Select(index =>
        {
            double value = Channel(hex, index) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }).ToArray();
    }

    private static double[] Lab(string hex)
    {
        double[] c = Linear(hex);
        double x = (0.4124 * c[0] + 0.3576 * c[1] + 0.1805 * c[2]) / 0.95047;
        double y = 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
        double z = (0.0193 * c[0] + 0.1192 * c[1] + 0.9505 * c[2]) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16d / 116;
        return new[] { 116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)) };
    }

    private static int Channel(string hex, int index) =>
        int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static bool IsColor(string? hex, bool alpha) =>
        hex != null && (hex.Length == 7 || alpha && hex.Length == 9) && hex[0] == '#' &&
        hex.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0;

    private static void RequireColor(string hex, bool alpha)
    {
        if (!IsColor(hex, alpha)) throw new ArgumentException("Expected an opaque RGB colour, or ARGB with an explicit backdrop.", nameof(hex));
    }

    private static IReadOnlyList<ContrastRule> Build(ContrastTheme theme)
    {
        var rules = new List<ContrastRule>();
        void Add(string foreground, string background, double minimum) => rules.Add(new(foreground, background, minimum));

        // Body/secondary text. Muted is never drawn on an unrevealed spoiler; it intentionally has no such rule.
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover", "Workspace.Overlay",
                     "Workspace.AccentSurface", "Workspace.ChipSurface", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler" })
        {
            Add("Workspace.Text", surface, 4.5);
            if (surface != "Workspace.ReaderSpoiler") Add("Workspace.Muted", surface, 4.5);
        }
        foreach (string surface in new[] { "Workspace.ErrorSurface", "Workspace.WarningSurface", "Workspace.SuccessSurface" })
            Add("Workspace.Text", surface, 4.5);

        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover",
                     "Workspace.AccentSurface", "Workspace.ChipSurface", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler" })
            Add("Workspace.Accent", surface, 4.5);
        Add("Workspace.OnAccent", "Workspace.Accent", 4.5);
        Add("Workspace.OnAccent", "Workspace.AccentHover", 4.5);
        Add("ButtonSolidPrimaryPressedForeground", "ButtonSolidPrimaryPressedBackground", 4.5);
        Add("Workspace.AccentHover", "Workspace.AccentSurface", 3.0); // graphic, not text

        foreach (string status in new[] { "Error", "Warning", "Success" })
        {
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace." + status + "Surface" })
                Add("Workspace." + status, surface, 4.5);
            Add("Workspace." + status, "Workspace." + status + "Surface", 3.0);
            Add("Workspace." + status, "Workspace.Panel", 3.0);
        }
        Add("Workspace.Warning", "Workspace.ChipSurface", 3.0);
        Add("Workspace.Progress", "Workspace.ProgressTrack", 3.0);

        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay" })
        {
            Add("Workspace.ControlBorder", surface, 3.0);
            Add("Workspace.ChipBorder", surface, 3.0);
            Add("Workspace.Accent", surface, 3.0);
        }
        Add("Workspace.ControlBorder", "Workspace.AccentSurface", 3.0);
        Add("Workspace.Background", "Workspace.Accent", 3.0);
        Add("Workspace.CardEdge", "Workspace.Background", 3.0);

        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler" })
            Add("Workspace.ReaderGreentext", surface, 4.5);
        foreach (string text in new[] { "Workspace.Text", "Workspace.Muted", "Workspace.Accent" })
        {
            Add(text, "Workspace.ReaderCodeSurface", 4.5);
            Add(text, "Workspace.Card", 4.5);
        }
        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background", "Workspace.ReaderSpoiler" })
            Add("Workspace.ControlBorder", surface, 3.0);

        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover", "Workspace.AccentSurface", "Workspace.Overlay" })
            Add("Workspace.Progress", surface, 3.0);

        // Tone outlines, checked-media hover text and tag captions.
        foreach (string tone in new[] { "Warning", "Error", "Success", "Accent" })
            Add("Workspace." + tone, "Workspace." + tone + "Surface", theme == ContrastTheme.Dark ? 5.0 : 5.5);
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Hover", "Workspace.Raised", "Workspace.Panel", "Workspace.Background", "Workspace.Card" })
            Add("Workspace.AccentHoverText", surface, 4.5);
        foreach (string category in new[] { "Artist", "Copyright", "Character", "Meta", "General" })
            foreach (string surface in new[] { "Workspace.ChipSurface", "Workspace.Panel", "Workspace.Raised" })
                Add("Workspace.TagCategory." + category, surface, 4.5);

        // Hover boundary and selection marks; the fill itself is not required to reach 3:1.
        foreach (string surface in new[] { "Workspace.Hover", "Workspace.Raised", "Workspace.AccentSurface", "Workspace.Overlay" })
            Add("Workspace.ControlBorder", surface, 3.0);
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Panel", "Workspace.Overlay" })
            Add("Workspace.Accent", surface, 3.0);
        Add("TabItemLineHeaderForeground", "Workspace.Panel", 4.5);

        // Semi state text/glyphs, including pointer-over and pressed.
        Add("TextBoxSelectionForeground", "TextBoxSelectionBackground", 4.5);
        Add("TextBlockSelectionForeground", "TextBlockSelectionBackground", 4.5);
        foreach (string state in new[] { "Default", "Pointerover", "Pressed" })
        {
            Add("CheckBoxGlyphFill", "CheckBoxChecked" + state + "Background", 4.5);
            Add("RadioButtonCheckGlyphFill", "RadioButtonCheckIcon" + state + "Background", 4.5);
        }
        Add("BadgeForeground", "BadgePrimaryBadgeBackground", 4.5);
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Panel", "Workspace.Background", "Workspace.Raised", "Workspace.Card" })
        {
            Add("PaginationButtonSelectedForeground", surface, 4.5);
            Add("BreadcrumbItemPointeroverForeground", surface, 4.5);
        }
        Add("TabItemButtonHeaderSelectedForeground", "TabItemButtonHeaderSelectedBackground", 4.5);
        Add("RadioButtonButtonCheckedForeground", "RadioButtonButtonCheckedBackground", 4.5);
        Add("BadgeLightPrimaryBadgeForeground", "BadgeLightPrimaryBadgeBackground", 4.5);
        foreach (string surface in new[] { "BannerInformationBackground", "NotificationCardLightInformationBackground", "TabItemLineHeaderSelectedBackground", "TabItemButtonHeaderSelectedBackground" })
            Add("Workspace.Text", surface, 4.5);
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay" })
            foreach (string key in new[] { "TextBoxFocusBorderBrush", "ComboBoxSelectorBorderedFocusBorderBrush", "CheckBoxCheckedDefaultBackground",
                         "CheckBoxCheckedDefaultBorderBrush", "CheckBoxPointeroverBorderBrush", "RadioButtonCheckIconDefaultBackground",
                         "RadioButtonUncheckIconPointeroverBorderBrush", "SliderTrackForeground", "SliderThumbBorderBrush", "ProgressBarPrimaryForeground",
                         "AdornerLayerBorderBrush", "LoadingIconForeground", "TextBoxSelectionBackground", "TextBlockSelectionBackground",
                         "CheckBoxDefaultBorderBrush", "RadioButtonUncheckIconDefaultBorderBrush" })
                Add(key, surface, 3.0);
        Add("BannerInformationBorderBrush", "BannerInformationBackground", 3.0);
        Add("NotificationCardLightInformationBorderBrush", "NotificationCardLightInformationBackground", 3.0);
        Add("NotificationCardInformationIconForeground", "NotificationCardLightInformationBackground", 3.0);
        Add("BannerInformationBorderBrush", "Workspace.Panel", 3.0);
        Add("CheckBoxDefaultBorderBrush", "Workspace.AccentSurface", 3.0);
        Add("CheckBoxDefaultBorderBrush", "Workspace.Hover", 3.0);
        foreach (string key in new[] { "CheckBoxCheckedPointeroverBackground", "CheckBoxCheckedPressedBackground", "RadioButtonCheckIconPointeroverBackground",
                     "SliderThumbPointeroverBorderBrush", "SliderThumbPressedBorderBrush" })
            Add(key, "Workspace.Panel", 3.0);

        Add("Workspace.Success", "Workspace.SuccessSurface", 4.5);
        foreach (string around in new[] { "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.ChipSurface" })
        {
            Add("Workspace.Success", around, 3.0);
            Add("Workspace.Accent", around, 3.0);
        }
        Add("Workspace.Accent", "Workspace.AccentSurface", 3.0);
        Add("Workspace.Text", "Workspace.AccentSurface", 4.5);
        Add("Workspace.ChipBorder", "Workspace.AccentSurface", 3.0);
        Add("Workspace.ChipBorder", "Workspace.ChipSurface", 3.0);
        foreach (string tone in new[] { "Error", "Warning", "Accent", "Success" })
        {
            Add("Workspace." + tone, "Workspace." + tone + "Surface", 3.0);
            Add("Workspace." + tone, "Workspace.Panel", 3.0);
        }
        rules.Add(new("Workspace.Panel", "Workspace.Scrim", theme == ContrastTheme.Dark ? 1.1 : 3.0, "Workspace.Background"));

        if (theme == ContrastTheme.Dark)
        {
            Add("Workspace.Progress", "Workspace.ProgressTrack", 3.5);
            foreach (var (surface, minimum) in new[] { ("Workspace.Panel", 1.8), ("Workspace.Raised", 1.6), ("Workspace.Hover", 1.3), ("Workspace.AccentSurface", 1.35) })
                Add("Workspace.ProgressTrack", surface, minimum);
        }
        else
        {
            Add("Workspace.Progress", "Workspace.ProgressTrack", 3.4);
            foreach (string surface in new[] { "Workspace.Panel", "Workspace.Raised", "Workspace.Hover", "Workspace.AccentSurface", "Workspace.Card", "Workspace.Background" })
                Add("Workspace.Progress", surface, 8.4);
            Add("Workspace.Text", "Workspace.Hover", 10.7);
            Add("Workspace.Muted", "Workspace.Hover", 4.8);
            foreach (var (surface, minimum) in new[] { ("Workspace.Background", 3.0), ("Workspace.Panel", 3.0), ("Workspace.Card", 3.0), ("Workspace.Overlay", 3.0),
                         ("Workspace.Raised", 2.4), ("Workspace.Hover", 2.4), ("Workspace.AccentSurface", 2.4) })
                Add("Workspace.ProgressTrack", surface, minimum);
            foreach (var (surface, minimum) in new[] { ("Workspace.Panel", 2.8), ("Workspace.Background", 2.5), ("Workspace.Raised", 2.3), ("Workspace.AccentSurface", 2.3), ("Workspace.Card", 3.1) })
                Add("Workspace.Border", surface, minimum);
            foreach (string key in new[] { "CheckBoxDisabledForeground", "RadioButtonDisabledForeground", "ExpanderHeaderDisabledForeground", "TextBoxDisabledForeground" })
                foreach (string surface in new[] { "Workspace.Panel", "Workspace.Background", "Workspace.Card", "Workspace.Raised" })
                    Add(key, surface, 4.5);
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay", "Workspace.AccentSurface" })
                Add("RadioButtonUncheckIconDefaultBorderBrush", surface, 3.0);
            Add("RadioButtonUncheckIconDefaultBorderBrush", "Workspace.Panel", 4.5);
            foreach (string state in new[] { "Default", "Pointerover" })
                Add("RadioButtonCheckIcon" + state + "Background", "Workspace.Panel", 3.0);
            foreach (string key in new[] { "ExpanderIconForeground", "ExpanderContentForeground", "ExpanderHeaderForeground" })
                Add(key, "Workspace.Panel", 4.5);
            foreach (string key in new[] { "ExpanderSeparatorBorderBrush", "MenuItemSeparatorBackground", "CommandBarSeparatorBackground", "DateTimePickerSeparatorBackground", "SliderTickForeground" })
                Add(key, "Workspace.Panel", 2.8);
            Add("SliderTrackBackground", "Workspace.Panel", 3.0);
            foreach (string key in new[] { "SliderTrackForeground", "SliderThumbBorderBrush", "SliderThumbPointeroverBorderBrush", "SliderThumbPressedBorderBrush" })
                Add(key, "Workspace.Panel", 4.5);
            foreach (string key in new[] { "HyperlinkButtonForeground", "HyperlinkButtonPointeroverForeground", "HyperlinkButtonPressedForeground" })
                foreach (string surface in new[] { "Workspace.Panel", "Workspace.Hover", "Workspace.AccentSurface" })
                    Add(key, surface, 4.5);
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Card", "Workspace.Overlay" })
            {
                Add("ScrollBarThumbForeground", surface, 3.0);
                Add("ScrollBarThumbPointeroverForeground", surface, 3.0);
            }
        }
        // Identical pairs tested by multiple UI contracts need one check, but different minima must not collapse.
        return Array.AsReadOnly(rules.Distinct().ToArray());
    }
}
