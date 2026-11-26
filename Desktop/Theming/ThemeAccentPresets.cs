using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH4: one accent colour with one click. <paramref name="Id"/> names the language-pack text <c>theme.accent.&lt;id&gt;</c>; the two
/// hex values are the accent of Light and of Dark (<c>#RRGGBB</c>, tuned separately so the accent passes the contrast gate on the
/// surface of its own variant). A null value keeps that variant's built-in accent.
/// </summary>
internal sealed record ThemeAccentPreset(string Id, string? LightHex, string? DarkHex, ThemePresetMarks? Marks = null)
{
    /// <summary>The preset's accent in a variant, or null for the built-in one.</summary>
    internal Color? For(ThemeTokenTheme theme) => (theme == ThemeTokenTheme.Light ? LightHex : DarkHex) is { } hex ? ThemeCustomization.ParseHex(hex) : null;

    /// <summary>
    /// The preset's colour of the favourite heart (<see cref="ThemeUserColors.Favorite"/>) or of the rating stars
    /// (<see cref="ThemeUserColors.Rating"/>) in a variant, or null for the built-in red or gold.
    /// </summary>
    internal Color? Mark(ThemeTokenTheme theme, string roleId)
    {
        string? hex = (roleId, theme) switch
        {
            (ThemeUserColors.Favorite, ThemeTokenTheme.Light) => Marks?.LightFavorite,
            (ThemeUserColors.Favorite, _) => Marks?.DarkFavorite,
            (ThemeUserColors.Rating, ThemeTokenTheme.Light) => Marks?.LightRating,
            (ThemeUserColors.Rating, _) => Marks?.DarkRating,
            _ => throw new ArgumentException($"\"{roleId}\" is not a mark colour of a preset.", nameof(roleId))
        };
        return hex is null ? null : ThemeCustomization.ParseHex(hex);
    }
}

/// <summary>
/// The heart and star colours a preset brings along (<c>#RRGGBB</c> per variant, null for the built-in red or gold). They are chosen to sit
/// apart from the preset's accent, from each other and from the error red, at 3 : 1 or more on every surface of their variant; the Dark
/// tones also on the dark disc of the tile badges.
/// </summary>
internal sealed record ThemePresetMarks(string? LightFavorite, string? DarkFavorite, string? LightRating = null, string? DarkRating = null);

/// <summary>
/// TH4: the six accent presets above the role list of the colour dialog. Applying one sets the accent role of both variants
/// (<see cref="ThemeColorOverrides.WithRole"/>); the related colours follow as with any accent. Each preset also brings a heart and a
/// star colour that suit its accent (Tom, 26.11.2026: not necessarily red and gold). The values live here and in
/// <c>WorkspaceStyles.axaml</c> only; a test runs every preset through <see cref="ThemeCustomization.Preview"/> for Light and Dark.
/// Simba-Orange is the built-in accent of Light; Dark's built-in accent is blue, so its Simba-Orange is a real orange there
/// and "Blau" keeps Dark's built-in blue.
/// </summary>
internal static class ThemeAccentPresets
{
    internal static IReadOnlyList<ThemeAccentPreset> All { get; } = Array.AsReadOnly(new ThemeAccentPreset[]
    {
        // Dark's orange accent sits next to the red and the gold: the heart moves to a rose, the stars to a pale yellow.
        new("simba", null, "#FA963E", new(null, "#FF5C8A", null, "#FFE680")),
        // A quiet slate: a berry heart (clear of the error red) and, in Dark, a softer amber.
        new("mila", "#344257", "#8FA8CC", new("#A8326A", "#F283A8", null, "#F2C14E")),
        // Red and gold are the complements of blue: the built-in colours stay.
        new("blue", "#074E85", null),
        // Red on green is the pair a red-green weakness loses: the heart is a magenta, the Dark gold a little warmer.
        new("green", "#1C5C36", "#6DC793", new("#B0246E", "#FF7EB6", null, "#FFD166")),
        // A pinkish red would read as the violet accent: the heart is a warm coral; gold is violet's complement and stays.
        new("violet", "#56317A", "#BB96FA", new("#C8321E", "#FF7A5C")),
        // The accent is itself a pink: the heart moves to a vermilion so that it is not taken for the selection; gold stays.
        new("pink", "#752948", "#FA89B6", new("#BF360C", "#FF7A45")),
    });

    /// <summary>The two mark roles a preset colours besides the accent.</summary>
    internal static IReadOnlyList<string> MarkRoles { get; } = Array.AsReadOnly(new[] { ThemeUserColors.Favorite, ThemeUserColors.Rating });

    internal static ThemeAccentPreset? Find(string? id) => All.FirstOrDefault(preset => preset.Id == id);

    /// <summary>
    /// <paramref name="overrides"/> with the accent of both variants set to the preset. A variant whose preset colour is the built-in
    /// accent (or null) has its accent role reset instead of storing a value equal to the default. The heart and star colours follow the
    /// preset too (<see cref="ThemeAccentPreset.Mark"/>), unless the user picked one by hand: a stored heart or star colour that no preset
    /// brings is the user's own and stays.
    /// </summary>
    internal static ThemeColorOverrides Apply(ThemeColorOverrides overrides, ThemeAccentPreset preset,
        Func<ThemeTokenTheme, Color> builtInAccent)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(builtInAccent);
        foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Light, ThemeTokenTheme.Dark })
            overrides = preset.For(theme) is { } color && color != builtInAccent(theme)
                ? overrides.WithRole(theme, ThemeUserColors.Accent, color)
                : overrides.ResetRole(theme, ThemeUserColors.Accent);
        foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Light, ThemeTokenTheme.Dark })
            foreach (string roleId in MarkRoles)
            {
                if (overrides.Get(theme, ThemeUserColors.Require(roleId).Seed) is { } stored && !All.Any(known => known.Mark(theme, roleId) == stored)) continue;
                overrides = preset.Mark(theme, roleId) is { } color ? overrides.WithRole(theme, roleId, color) : overrides.ResetRole(theme, roleId);
            }
        return overrides;
    }

    /// <summary>The preset whose accent both variants show in <paramref name="overrides"/>, or null (none, or a hand-picked colour).</summary>
    internal static ThemeAccentPreset? Active(ThemeColorOverrides overrides, Func<ThemeTokenTheme, Color> builtInAccent)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(builtInAccent);
        string accent = ThemeUserColors.Require(ThemeUserColors.Accent).Seed;
        Color Shown(ThemeTokenTheme theme) => overrides.Get(theme, accent) ?? builtInAccent(theme);
        return All.FirstOrDefault(preset => new[] { ThemeTokenTheme.Light, ThemeTokenTheme.Dark }
            .All(theme => (preset.For(theme) ?? builtInAccent(theme)) == Shown(theme)));
    }
}
