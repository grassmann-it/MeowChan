using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Theming;

/// <summary>How a related seed follows its role's primary colour once the user changed it (<see cref="ThemeCustomization.Derive"/>).</summary>
internal enum ThemeSeedDerivation
{
    /// <summary>
    /// Keeps its offset to the primary in HSL: hue, saturation and lightness of the new primary plus the base difference
    /// (related − primary), clamped. A hover tone stays "a bit lighter", a pressed tone "a bit darker".
    /// </summary>
    Follow,
    /// <summary>
    /// A tinted surface: keeps its own base lightness (so text on it keeps its contrast), takes the new primary's hue (plus the
    /// base hue difference) and scales its saturation by new/base primary saturation (a grey primary gives a grey surface).
    /// </summary>
    Tint,
    /// <summary>A surface step: the new primary plus the base RGB difference per channel (Raised is "Panel plus a step").</summary>
    Offset,
    /// <summary>Text on the primary: black or white, whichever has the higher minimum contrast against the listed backgrounds.</summary>
    OnColor,
}

/// <summary>
/// A seed that follows a role's primary colour. <paramref name="Against"/> lists the backgrounds of an <see cref="ThemeSeedDerivation.OnColor"/>
/// seed (resolved after the seeds before it in the role, so a derived hover tone counts); empty for the other derivations.
/// </summary>
internal sealed record ThemeRelatedSeed(string Seed, ThemeSeedDerivation Derivation, IReadOnlyList<string> Against)
{
    internal ThemeRelatedSeed(string seed, ThemeSeedDerivation derivation) : this(seed, derivation, Array.Empty<string>()) { }
}

/// <summary>
/// One colour the configurator offers (TH3): a stable <see cref="Id"/> (settings, help, language-pack keys
/// <c>theme.color.&lt;id&gt;</c>), its primary seed and the related seeds that follow it. <see cref="Counterpart"/> and
/// <see cref="Minimum"/> name the one contrast to show next to the colour ("4.8 : 1 – good"); the full gate is
/// <see cref="ThemeCustomization.CheckContrast"/>.
/// </summary>
internal sealed record ThemeUserColor(string Id, string Seed, IReadOnlyList<ThemeRelatedSeed> Related, string Counterpart, double Minimum)
{
    /// <summary>The primary seed first, then the related seeds in derivation order.</summary>
    internal IEnumerable<string> Seeds => Related.Select(related => related.Seed).Prepend(Seed);
}

/// <summary>
/// TH2: the curated, user-facing colour roles, defined only here. Each maps to real <see cref="ThemeTokens.Seeds"/>; every
/// other seed stays at its base value unless set explicitly (hand-edited settings, later theme files). No seed belongs to two
/// roles. The order is the configurator's order.
/// </summary>
internal static class ThemeUserColors
{
    internal const string Accent = "accent", Background = "background", Surface = "surface", Text = "text", Muted = "muted",
        Border = "border", Favorite = "favorite", Error = "error";

    internal static IReadOnlyList<ThemeUserColor> All { get; } = Array.AsReadOnly(new ThemeUserColor[]
    {
        // Akzent. Hover tones keep their step, the selected/tinted surfaces keep their lightness, the text on the accent is
        // black or white. Chip selection and the progress fill are the accent in both bases (Light: a darker accent step).
        new(Accent, "Workspace.Accent",
        [
            new("Workspace.AccentHover", ThemeSeedDerivation.Follow),
            new("Workspace.OnAccent", ThemeSeedDerivation.OnColor, ["Workspace.Accent", "Workspace.AccentHover"]),
            new("Workspace.AccentHoverText", ThemeSeedDerivation.Follow),
            new("Workspace.AccentSurface", ThemeSeedDerivation.Tint),
            new("Workspace.ChipSelectedSurface", ThemeSeedDerivation.Tint),
            new("Workspace.ChipSelectedBorder", ThemeSeedDerivation.Follow),
            new("Workspace.Progress", ThemeSeedDerivation.Follow),
        ], "Workspace.Panel", ContrastGate.TextMinimum),
        // Hintergrund.
        new(Background, "Workspace.Background", [], "Workspace.Text", ContrastGate.TextMinimum),
        // Fläche: the panel and the surfaces stepped from it (raised controls, cards, popups, chips, hover fill).
        new(Surface, "Workspace.Panel",
        [
            new("Workspace.Raised", ThemeSeedDerivation.Offset),
            new("Workspace.Card", ThemeSeedDerivation.Offset),
            new("Workspace.Overlay", ThemeSeedDerivation.Offset),
            new("Workspace.ChipSurface", ThemeSeedDerivation.Offset),
            new("Workspace.Hover", ThemeSeedDerivation.Offset),
        ], "Workspace.Text", ContrastGate.TextMinimum),
        new(Text, "Workspace.Text", [], "Workspace.Background", ContrastGate.TextMinimum),
        // Gedämpft; the "general" tag category is the muted text in both bases.
        new(Muted, "Workspace.Muted", [new("Workspace.TagCategory.General", ThemeSeedDerivation.Follow)], "Workspace.Panel", ContrastGate.TextMinimum),
        // Rand: the decorative border; the interactive outlines (controls, chips, card edges) keep their step to it.
        new(Border, "Workspace.Border",
        [
            new("Workspace.ControlBorder", ThemeSeedDerivation.Follow),
            new("Workspace.ChipBorder", ThemeSeedDerivation.Follow),
            new("Workspace.ChipSourceBorder", ThemeSeedDerivation.Follow),
            new("Workspace.CardEdge", ThemeSeedDerivation.Follow),
        ], "Workspace.Panel", ContrastGate.GraphicMinimum),
        new(Favorite, "Workspace.Favorite", [], "Workspace.Panel", ContrastGate.GraphicMinimum),
        // Fehler; the error surface keeps its lightness, the exclude-chip outline is the error colour.
        new(Error, "Workspace.Error",
        [
            new("Workspace.ErrorSurface", ThemeSeedDerivation.Tint),
            new("Workspace.ChipExcludeBorder", ThemeSeedDerivation.Follow),
        ], "Workspace.Panel", ContrastGate.TextMinimum),
    });

    private static readonly FrozenDictionary<string, ThemeUserColor> ById = All.ToFrozenDictionary(role => role.Id, StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, ThemeUserColor> BySeed =
        All.SelectMany(role => role.Seeds.Select(seed => (seed, role))).ToFrozenDictionary(pair => pair.seed, pair => pair.role, StringComparer.Ordinal);

    internal static ThemeUserColor? Find(string? id) => id != null && ById.TryGetValue(id, out ThemeUserColor? role) ? role : null;

    internal static ThemeUserColor Require(string id) =>
        Find(id) ?? throw new ArgumentException($"\"{id}\" is not a theme colour role.", nameof(id));

    /// <summary>The role a seed belongs to (as primary or related seed), or null for a seed no role offers.</summary>
    internal static ThemeUserColor? OwnerOf(string seed) => BySeed.GetValueOrDefault(seed);
}

/// <summary>The two thresholds of the TH2 gate: text below 4.5 : 1 is refused, a border or mark below its minimum (3 : 1) warns.</summary>
internal static class ContrastGate
{
    internal const double TextMinimum = 4.5, GraphicMinimum = 3.0;
}
