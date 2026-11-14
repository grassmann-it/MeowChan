using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// One deterministic rule for a documented state variation (<see cref="ThemeTokenVariation"/>): the seeds it reads and how the
/// colour follows from them. <paramref name="Description"/> is the rule in words, for the docs and test messages.
/// </summary>
internal sealed record ThemeVariationRule(ThemeTokenVariation Variation, IReadOnlyList<string> Sources,
    Func<Func<string, Color>, Color> Derive, string Description);

/// <summary>
/// The colours of one built-in theme dictionary as WorkspaceStyles.axaml defines them: every explicit brush key with its colour
/// and the overlay shadows. At runtime it is read from the loaded styles (<see cref="ThemeRuntime"/>), in tests from the XAML
/// file. The key set must be exactly the T1 inventory (<see cref="ThemeTokens"/>) of that theme, so a missing or unknown key
/// fails here instead of producing a partial palette.
/// </summary>
internal sealed class ThemePaletteBase
{
    internal ThemePaletteBase(ThemeTokenTheme theme, IReadOnlyDictionary<string, Color> colors, IReadOnlyDictionary<string, BoxShadows> shadows)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(shadows);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        string[] expectedColors = ThemePalette.ColorKeys(theme).ToArray();
        string[] expectedShadows = ThemePalette.ShadowKeys.ToArray();
        string? missing = expectedColors.FirstOrDefault(key => !colors.ContainsKey(key)) ?? expectedShadows.FirstOrDefault(key => !shadows.ContainsKey(key));
        if (missing != null) throw new ArgumentException($"The {theme} base palette has no \"{missing}\".", nameof(colors));
        string? unknown = colors.Keys.FirstOrDefault(key => map.Classify(key) is not (ThemeTokenClassification.Seed or ThemeTokenClassification.Mirror or ThemeTokenClassification.Variation))
            ?? shadows.Keys.FirstOrDefault(key => map.Classify(key) != ThemeTokenClassification.Shadow);
        if (unknown != null) throw new ArgumentException($"The {theme} base palette has the unclassified key \"{unknown}\".", nameof(colors));
        Theme = theme;
        Colors = colors.ToFrozenDictionary(StringComparer.Ordinal);
        Shadows = shadows.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal ThemeTokenTheme Theme { get; }
    internal FrozenDictionary<string, Color> Colors { get; }
    internal FrozenDictionary<string, BoxShadows> Shadows { get; }
}

/// <summary>
/// TH1 runtime palette: a base theme (Light or Dark) plus seed overrides gives the complete theme dictionary, i.e. every key the
/// base dictionary of WorkspaceStyles.axaml defines. Seeds come from the overrides or the base; mirrors copy their seed
/// (<see cref="ThemeTokenMap.Mirrors"/>); the documented variations follow the explicit rules in <see cref="Rules"/>; the overlay
/// shadows are composite resources and are taken from the base unchanged.
/// <para>
/// Unchanged base, unchanged result: a variation whose source seeds are all unchanged keeps the base value. Where a rule
/// reproduces today's XAML value this is the same colour; where it does not (<see cref="Pinned"/>) the base value is a pinned
/// exception and the rule only applies once one of its seeds is overridden. The pinned cases are listed in
/// <see cref="Rules"/> and pinned in <c>ThemePaletteTests</c>.
/// </para>
/// Pure data: no resources are read or written here (<see cref="ThemeRuntime"/> applies a palette). Overrides are not
/// contrast-checked; a caller runs <see cref="ContrastRules.Check"/> on <see cref="ToContrastPalette"/>.
/// </summary>
internal sealed class ThemePalette
{
    /// <summary>The pressed accent step of Light: every RGB channel times 0.8 (HSV value −20 %, hue and saturation kept).</summary>
    internal const double PressedStep = 0.8;
    /// <summary>The drag preview alpha: 0x66 = 102 of 255 = 40 %.</summary>
    internal const byte PreviewAlpha = 0x66;
    /// <summary>The Dark disabled outline: the interactive outline mixed half-way into the panel (sRGB).</summary>
    internal const double DisabledOutlineMix = 0.5;

    private static readonly FrozenDictionary<ThemeTokenVariation, ThemeVariationRule> DarkRules = new ThemeVariationRule[]
    {
        // Pinned exception: the rule gives #4C535B for the base, today's value is #4A525C.
        new(ThemeTokenVariation.DisabledOutline, ["Workspace.ControlBorder", "Workspace.Panel"],
            seed => Mix(seed("Workspace.ControlBorder"), seed("Workspace.Panel"), DisabledOutlineMix), "ControlBorder mixed 50 % into Panel"),
        // Dark hover is lighter (AccentHover); pressing returns to the accent itself.
        new(ThemeTokenVariation.PressedAccent, ["Workspace.Accent"], seed => seed("Workspace.Accent"), "Accent"),
        new(ThemeTokenVariation.PressedAccentBorder, ["Workspace.AccentHover"], seed => seed("Workspace.AccentHover"), "AccentHover"),
        new(ThemeTokenVariation.PressedAccentText, ["Workspace.AccentHover"], seed => seed("Workspace.AccentHover"), "AccentHover"),
        new(ThemeTokenVariation.AccentPreview, ["Workspace.Accent"], seed => WithAlpha(seed("Workspace.Accent"), PreviewAlpha), "Accent at 40 % alpha"),
    }.ToFrozenDictionary(rule => rule.Variation);

    private static readonly FrozenDictionary<ThemeTokenVariation, ThemeVariationRule> LightRules = new ThemeVariationRule[]
    {
        new(ThemeTokenVariation.DisabledOutline, ["Workspace.Border"], seed => seed("Workspace.Border"), "Border"),
        // #803B06 × 0.8 = #662F05 exactly.
        new(ThemeTokenVariation.PressedAccent, ["Workspace.Accent"], seed => Scale(seed("Workspace.Accent"), PressedStep), "Accent × 0.8"),
        new(ThemeTokenVariation.PressedAccentBorder, ["Workspace.Accent"], seed => Scale(seed("Workspace.Accent"), PressedStep), "Accent × 0.8 (pressed accent)"),
        new(ThemeTokenVariation.PressedAccentText, ["Workspace.Accent"], seed => Scale(seed("Workspace.Accent"), PressedStep), "Accent × 0.8 (pressed accent)"),
        new(ThemeTokenVariation.AccentPreview, ["Workspace.Accent"], seed => WithAlpha(seed("Workspace.Accent"), PreviewAlpha), "Accent at 40 % alpha"),
        // Pinned exception: the hovered scroll thumb is one pressed step darker than the thumb (ControlBorder). The rule gives
        // #5B5346 for the base; today's value is #575148, which only happens to equal Muted (not a text role).
        new(ThemeTokenVariation.ScrollBarHover, ["Workspace.ControlBorder"],
            seed => Scale(seed("Workspace.ControlBorder"), PressedStep), "ControlBorder × 0.8"),
    }.ToFrozenDictionary(rule => rule.Variation);

    private readonly FrozenDictionary<string, Color> _colors;

    private ThemePalette(ThemeTokenTheme theme, FrozenDictionary<string, Color> colors, FrozenDictionary<string, BoxShadows> shadows,
        FrozenSet<string> pinned, FrozenDictionary<string, Color> overrides)
    {
        Theme = theme;
        _colors = colors;
        Shadows = shadows;
        Pinned = pinned;
        Overrides = overrides;
    }

    internal ThemeTokenTheme Theme { get; }

    /// <summary>Every brush key of the base theme dictionary with its colour.</summary>
    internal IReadOnlyDictionary<string, Color> Colors => _colors;

    /// <summary>The overlay shadows, unchanged from the base (composite resources, not colour seeds).</summary>
    internal IReadOnlyDictionary<string, BoxShadows> Shadows { get; }

    /// <summary>Variation keys that kept the base value because their rule does not reproduce it and no source seed changed.</summary>
    internal IReadOnlySet<string> Pinned { get; }

    /// <summary>The effective overrides: only seeds whose colour differs from the base.</summary>
    internal IReadOnlyDictionary<string, Color> Overrides { get; }

    /// <summary>The variation rules of a base theme, one per documented variation that theme has.</summary>
    internal static IReadOnlyDictionary<ThemeTokenVariation, ThemeVariationRule> Rules(ThemeTokenTheme theme) => theme switch
    {
        ThemeTokenTheme.Dark => DarkRules,
        ThemeTokenTheme.Light => LightRules,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    /// <summary>The brush keys of a base theme dictionary: seeds, mirrors and variations (Light has its own extra overrides).</summary>
    internal static IEnumerable<string> ColorKeys(ThemeTokenTheme theme)
    {
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        return ThemeTokens.Seeds.Concat(map.Mirrors.Keys).Concat(map.Variations.Keys);
    }

    /// <summary>The shadow keys (identical in both themes).</summary>
    internal static IEnumerable<string> ShadowKeys => ThemeTokens.ShadowMirrors.Keys.Prepend(ThemeTokens.ShadowSeed);

    /// <summary>
    /// Builds the complete palette. <paramref name="overrides"/> may only name colour seeds (<see cref="ThemeTokens.Seeds"/>);
    /// a mirror, variation, shadow or unknown key is an error, not silently ignored. An override equal to the base value is
    /// no change.
    /// </summary>
    internal static ThemePalette Build(ThemePaletteBase basePalette, IReadOnlyDictionary<string, Color>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(basePalette);
        ThemeTokenTheme theme = basePalette.Theme;
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        var effective = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (key, color) in overrides ?? FrozenDictionary<string, Color>.Empty)
        {
            if (!ThemeTokens.Seeds.Contains(key))
                throw new ArgumentException($"\"{key}\" is not a colour seed; only Workspace.* seeds can be overridden.", nameof(overrides));
            if (basePalette.Colors[key] != color) effective[key] = color;
        }

        Color Seed(string key) => effective.TryGetValue(key, out Color color) ? color : basePalette.Colors[key];

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (string seed in ThemeTokens.Seeds) colors[seed] = Seed(seed);
        foreach (var (mirror, seed) in map.Mirrors) colors[mirror] = Seed(seed);

        IReadOnlyDictionary<ThemeTokenVariation, ThemeVariationRule> rules = Rules(theme);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, variation) in map.Variations)
        {
            ThemeVariationRule rule = rules.TryGetValue(variation, out ThemeVariationRule? found) ? found
                : throw new InvalidOperationException($"{theme} has no rule for the variation {variation} of \"{key}\".");
            Color derived = rule.Derive(Seed);
            bool sourcesUnchanged = rule.Sources.All(source => !effective.ContainsKey(source));
            Color baseValue = basePalette.Colors[key];
            if (sourcesUnchanged && derived != baseValue)
            {
                colors[key] = baseValue;
                pinned.Add(key);
            }
            else colors[key] = derived;
        }

        var shadows = ShadowKeys.ToDictionary(key => key, key => basePalette.Shadows[key], StringComparer.Ordinal);
        return new ThemePalette(theme, colors.ToFrozenDictionary(StringComparer.Ordinal), shadows.ToFrozenDictionary(StringComparer.Ordinal),
            pinned.ToFrozenSet(StringComparer.Ordinal), effective.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>The palette as the contrast checker reads it: <c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque.</summary>
    internal ContrastPalette ToContrastPalette() =>
        new(Theme == ThemeTokenTheme.Dark ? ContrastTheme.Dark : ContrastTheme.Light,
            _colors.ToDictionary(pair => pair.Key, pair => Hex(pair.Value), StringComparer.Ordinal));

    /// <summary>Upper-case hex as WorkspaceStyles.axaml writes it: <c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque.</summary>
    internal static string Hex(Color color) => color.A == byte.MaxValue
        ? string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>Every RGB channel times <paramref name="factor"/>, rounded half away from zero; alpha is kept.</summary>
    internal static Color Scale(Color color, double factor) =>
        Color.FromArgb(color.A, Channel(color.R * factor), Channel(color.G * factor), Channel(color.B * factor));

    /// <summary>
    /// <paramref name="top"/> mixed over <paramref name="under"/> with weight <paramref name="amount"/> per sRGB channel, rounded
    /// half away from zero (the arithmetic of a design mix, not the compositor's to-even rounding in <see cref="ContrastRules.Blend"/>).
    /// The alpha of <paramref name="top"/> is kept.
    /// </summary>
    internal static Color Mix(Color top, Color under, double amount) =>
        Color.FromArgb(top.A, Channel(top.R * amount + under.R * (1 - amount)), Channel(top.G * amount + under.G * (1 - amount)),
            Channel(top.B * amount + under.B * (1 - amount)));

    internal static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
