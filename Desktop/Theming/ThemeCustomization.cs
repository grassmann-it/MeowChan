using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;

namespace MeowChan.Desktop.Theming;

/// <summary>Severity of a contrast finding of the TH2 gate.</summary>
internal enum ThemeContrastSeverity { Warning, Error }

/// <summary>
/// One failing contrast pair. <paramref name="Foreground"/>/<paramref name="Background"/> (and <paramref name="Backdrop"/> for a
/// scrim) are resource keys as in <see cref="ContrastRules"/>; <paramref name="Ratio"/> is the unrounded WCAG ratio (null when a
/// colour was missing or not opaque: <paramref name="Kind"/> and <paramref name="Key"/> say which). <paramref name="Required"/> is
/// the threshold that was missed: 4.5 for a refused text pair, the reviewed rule minimum for a warning.
/// </summary>
internal sealed record ThemeContrastIssue(ThemeContrastSeverity Severity, string Foreground, string Background, string? Backdrop,
    double Required, double? Ratio, ContrastFailureKind Kind, string? Key);

/// <summary>The contrast report of a palette: errors refuse the override set, warnings are shown but allowed.</summary>
internal sealed record ThemeContrastReport(IReadOnlyList<ThemeContrastIssue> Errors, IReadOnlyList<ThemeContrastIssue> Warnings)
{
    internal bool Accepted => Errors.Count == 0;
    internal IEnumerable<ThemeContrastIssue> All => Errors.Concat(Warnings);
}

/// <summary>
/// The result of <see cref="ThemeCustomization.Preview"/>: the complete palette the overrides would give, which seeds were set
/// explicitly and which followed a role's primary colour, and the contrast report. Nothing is applied.
/// </summary>
internal sealed record ThemePreview(ThemePalette Palette, IReadOnlyDictionary<string, Color> Explicit, IReadOnlySet<string> Derived,
    ThemeContrastReport Contrast)
{
    internal ThemeTokenTheme Theme => Palette.Theme;
    internal bool Accepted => Contrast.Accepted;
}

/// <summary>
/// TH2: from user overrides to a checked palette, as pure functions. <see cref="Expand"/> adds the related seeds of every role
/// whose primary colour changed (<see cref="ThemeUserColors"/>, unless the related seed is set explicitly);
/// <see cref="Preview"/> builds the palette (<see cref="ThemePalette.Build"/>) and runs the gate (<see cref="CheckContrast"/>).
/// No overrides (or overrides equal to the base) derive nothing, so the result is exactly the built-in palette.
/// </summary>
internal static class ThemeCustomization
{
    /// <summary>The explicit overrides plus the derived related seeds; <c>Derived</c> names the latter.</summary>
    internal static (IReadOnlyDictionary<string, Color> Seeds, IReadOnlySet<string> Derived) Expand(ThemePaletteBase basePalette,
        IReadOnlyDictionary<string, Color>? overrides)
    {
        ArgumentNullException.ThrowIfNull(basePalette);
        var seeds = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (key, color) in overrides ?? FrozenDictionary<string, Color>.Empty)
        {
            if (!ThemeTokens.Seeds.Contains(key))
                throw new ArgumentException($"\"{key}\" is not a colour seed; only Workspace.* seeds can be overridden.", nameof(overrides));
            seeds[key] = color;
        }
        var explicitKeys = seeds.Keys.ToFrozenSet(StringComparer.Ordinal);
        var derived = new HashSet<string>(StringComparer.Ordinal);
        Color Current(string key) => seeds.TryGetValue(key, out Color color) ? color : basePalette.Colors[key];
        foreach (ThemeUserColor role in ThemeUserColors.All)
        {
            Color primaryBase = basePalette.Colors[role.Seed];
            if (!seeds.TryGetValue(role.Seed, out Color primary) || primary == primaryBase) continue;
            foreach (ThemeRelatedSeed related in role.Related)
            {
                if (explicitKeys.Contains(related.Seed)) continue;
                seeds[related.Seed] = Derive(related, primaryBase, basePalette.Colors[related.Seed], primary, Current);
                derived.Add(related.Seed);
            }
        }
        return (seeds, derived);
    }

    /// <summary>The palette and contrast report the overrides would give on this base, without applying anything (TH3 preview).</summary>
    internal static ThemePreview Preview(ThemePaletteBase basePalette, IReadOnlyDictionary<string, Color>? overrides)
    {
        var (seeds, derived) = Expand(basePalette, overrides);
        ThemePalette palette = ThemePalette.Build(basePalette, seeds);
        var explicitColors = seeds.Where(pair => !derived.Contains(pair.Key)).ToFrozenDictionary(StringComparer.Ordinal);
        return new ThemePreview(palette, explicitColors, derived.ToFrozenSet(StringComparer.Ordinal), RefuseTranslucentOverrides(CheckContrast(palette), basePalette, explicitColors));
    }

    // N9: an explicit override must be opaque. The pair check sees a translucent colour only for seeds that occur in a contrast pair (a translucent Border or
    // Scrim would pass), so every translucent explicit seed is refused here, unless it only repeats the built-in colour or the pair check already named it.
    private static ThemeContrastReport RefuseTranslucentOverrides(ThemeContrastReport report, ThemePaletteBase basePalette, IReadOnlyDictionary<string, Color> explicitColors)
    {
        List<ThemeContrastIssue>? added = null;
        foreach (var (seed, color) in explicitColors)
        {
            if (color.A == byte.MaxValue || basePalette.Colors[seed] == color || report.Errors.Any(issue => issue.Kind == ContrastFailureKind.InvalidColor && issue.Key == seed)) continue;
            (added ??= []).Add(new ThemeContrastIssue(ThemeContrastSeverity.Error, seed, seed, null, ContrastGate.TextMinimum, null, ContrastFailureKind.InvalidColor, seed));
        }
        return added == null ? report : new ThemeContrastReport(report.Errors.Concat(added).ToArray(), report.Warnings);
    }

    /// <summary>
    /// The TH2 gate over every pair of <see cref="ContrastRules.For"/>: a text pair (minimum 4.5 or more) below 4.5 : 1 is an
    /// error; a text pair between 4.5 and a stricter reviewed minimum, and a border/mark pair below its minimum (3 : 1 or a
    /// reviewed lower value), are warnings. A missing or non-opaque colour in a pair is an error. The built-in palettes have none.
    /// </summary>
    internal static ThemeContrastReport CheckContrast(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var errors = new List<ThemeContrastIssue>();
        var warnings = new List<ThemeContrastIssue>();
        foreach (ContrastFailure failure in ContrastRules.Check(palette.ToContrastPalette()))
        {
            ContrastRule rule = failure.Rule;
            bool text = rule.Minimum >= ContrastGate.TextMinimum;
            bool error = failure.Kind != ContrastFailureKind.InsufficientContrast || text && failure.Ratio < ContrastGate.TextMinimum;
            var issue = new ThemeContrastIssue(error ? ThemeContrastSeverity.Error : ThemeContrastSeverity.Warning, rule.Foreground,
                rule.Background, rule.Backdrop, error && text ? ContrastGate.TextMinimum : rule.Minimum, failure.Ratio, failure.Kind, failure.Key);
            (error ? errors : warnings).Add(issue);
        }
        return new ThemeContrastReport(errors.AsReadOnly(), warnings.AsReadOnly());
    }

    /// <summary>The findings that involve a role, i.e. a key that is one of its seeds, a mirror of one or a variation derived from one.</summary>
    internal static IReadOnlyList<ThemeContrastIssue> ForRole(ThemeContrastReport report, ThemeTokenTheme theme, string roleId)
    {
        ArgumentNullException.ThrowIfNull(report);
        FrozenSet<string> seeds = ThemeUserColors.Require(roleId).Seeds.ToFrozenSet(StringComparer.Ordinal);
        bool Touches(string? key) => key != null && SeedsOf(theme, key).Any(seeds.Contains);
        return report.All.Where(issue => Touches(issue.Foreground) || Touches(issue.Background) || Touches(issue.Backdrop)).ToArray();
    }

    /// <summary>The seeds a resource key follows: itself for a seed, its seed for a mirror, the rule's sources for a variation.</summary>
    internal static IEnumerable<string> SeedsOf(ThemeTokenTheme theme, string key)
    {
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        if (ThemeTokens.Seeds.Contains(key)) return [key];
        if (map.Mirrors.TryGetValue(key, out string? seed)) return [seed];
        if (map.Variations.TryGetValue(key, out ThemeTokenVariation variation)) return ThemePalette.Rules(theme)[variation].Sources;
        return [];
    }

    /// <summary>
    /// The colour of a related seed after its role's primary changed from <paramref name="primaryBase"/> to <paramref name="primary"/>
    /// (<see cref="ThemeSeedDerivation"/>). A related seed equal to its primary in the base stays equal to the new primary.
    /// <paramref name="current"/> resolves other seeds as far as they are known (explicit, derived before, else the base).
    /// </summary>
    internal static Color Derive(ThemeRelatedSeed related, Color primaryBase, Color relatedBase, Color primary, Func<string, Color> current)
    {
        ArgumentNullException.ThrowIfNull(related);
        ArgumentNullException.ThrowIfNull(current);
        if (related.Derivation == ThemeSeedDerivation.OnColor) return OnColor(related.Against.Select(current).DefaultIfEmpty(primary));
        if (relatedBase == primaryBase) return primary;
        switch (related.Derivation)
        {
            case ThemeSeedDerivation.Offset:
                return Color.FromArgb(relatedBase.A, Channel(primary.R + relatedBase.R - primaryBase.R),
                    Channel(primary.G + relatedBase.G - primaryBase.G), Channel(primary.B + relatedBase.B - primaryBase.B));
            case ThemeSeedDerivation.Follow:
            case ThemeSeedDerivation.Tint:
            {
                HslColor p0 = primaryBase.ToHsl(), r0 = relatedBase.ToHsl(), p1 = primary.ToHsl();
                double hue = ((p1.H + r0.H - p0.H) % 360 + 360) % 360;
                double saturation = related.Derivation == ThemeSeedDerivation.Follow ? p1.S + r0.S - p0.S
                    : p0.S > 0 ? r0.S * p1.S / p0.S : r0.S;
                double lightness = related.Derivation == ThemeSeedDerivation.Follow ? p1.L + r0.L - p0.L : r0.L;
                Color rgb = new HslColor(1, hue, Math.Clamp(saturation, 0, 1), Math.Clamp(lightness, 0, 1)).ToRgb();
                return Color.FromArgb(relatedBase.A, rgb.R, rgb.G, rgb.B);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(related), related.Derivation, "Unknown derivation.");
        }
    }

    /// <summary>Black or white, whichever has the higher minimum WCAG contrast against all backgrounds (ties: white).</summary>
    internal static Color OnColor(IEnumerable<Color> backgrounds)
    {
        ArgumentNullException.ThrowIfNull(backgrounds);
        Color white = Color.FromArgb(255, 255, 255, 255), black = Color.FromArgb(255, 0, 0, 0);
        string[] hexes = backgrounds.Select(color => ThemePalette.Hex(Color.FromArgb(255, color.R, color.G, color.B))).ToArray();
        double Worst(Color candidate) => hexes.Length == 0 ? 0 : hexes.Min(hex => ContrastRules.Contrast(ThemePalette.Hex(candidate), hex));
        return Worst(black) > Worst(white) ? black : white;
    }

    /// <summary>A normalized hex colour (<see cref="ThemeColorOverrides.TryNormalizeHex"/>) as a colour; anything else throws.</summary>
    internal static Color ParseHex(string hex)
    {
        if (!ThemeColorOverrides.TryNormalizeHex(hex, out string normalized))
            throw new FormatException("Expected #RRGGBB or #AARRGGBB.");
        uint value = uint.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return normalized.Length == 7
            ? Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static byte Channel(int value) => (byte)Math.Clamp(value, 0, 255);
}
