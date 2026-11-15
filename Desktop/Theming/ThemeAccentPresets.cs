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
internal sealed record ThemeAccentPreset(string Id, string? LightHex, string? DarkHex)
{
    /// <summary>The preset's accent in a variant, or null for the built-in one.</summary>
    internal Color? For(ThemeTokenTheme theme) => (theme == ThemeTokenTheme.Light ? LightHex : DarkHex) is { } hex ? ThemeCustomization.ParseHex(hex) : null;
}

/// <summary>
/// TH4: the six accent presets above the role list of the colour dialog. Applying one sets the accent role of both variants
/// (<see cref="ThemeColorOverrides.WithRole"/>); the related colours follow as with any accent. The values live here and in
/// <c>WorkspaceStyles.axaml</c> only; a test runs every preset through <see cref="ThemeCustomization.Preview"/> for Light and Dark.
/// Simba-Orange is the built-in accent of Light; Dark's built-in accent is blue, so its Simba-Orange is a real orange there
/// and "Blau" keeps Dark's built-in blue.
/// </summary>
internal static class ThemeAccentPresets
{
    internal static IReadOnlyList<ThemeAccentPreset> All { get; } = Array.AsReadOnly(new ThemeAccentPreset[]
    {
        new("simba", null, "#FA963E"),
        new("mila", "#344257", "#8FA8CC"),
        new("blue", "#074E85", null),
        new("green", "#1C5C36", "#6DC793"),
        new("violet", "#56317A", "#BB96FA"),
        new("pink", "#752948", "#FA89B6"),
    });

    internal static ThemeAccentPreset? Find(string? id) => All.FirstOrDefault(preset => preset.Id == id);

    /// <summary>
    /// <paramref name="overrides"/> with the accent of both variants set to the preset. A variant whose preset colour is the built-in
    /// accent (or null) has its accent role reset instead of storing a value equal to the default.
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
