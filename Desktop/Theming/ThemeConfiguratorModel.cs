using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// One row of the colour dialog: a role in one variant as the preview shows it. <paramref name="Ratio"/> is the contrast of the
/// role's displayed pair (<see cref="ThemeUserColor.Counterpart"/>), unrounded; <paramref name="PairPasses"/> says whether it meets
/// <paramref name="Minimum"/>. <paramref name="Errors"/> and <paramref name="Warnings"/> count every failing pair of the whole gate
/// that involves the role (<see cref="ThemeCustomization.ForRole"/>), so a weak derived colour is named at its role.
/// </summary>
internal sealed record ThemeRoleView(ThemeUserColor Role, ThemeTokenTheme Theme, Color Color, string Hex, bool IsCustom, string CounterpartRoleId,
    double Ratio, double Minimum, bool PairPasses, int Errors, int Warnings)
{
    internal string RoleId => Role.Id;
    /// <summary>A pair that must reach 4.5 : 1 (text) rather than 3 : 1 (a border or mark).</summary>
    internal bool IsTextPair => Minimum >= ContrastGate.TextMinimum;
}

/// <summary>Why Apply is refused: the first variant (Light, then Dark) with a text pair below 4.5 : 1 and, when one role owns it, that role.</summary>
internal sealed record ThemeApplyBlock(ThemeTokenTheme Theme, string? RoleId, int ErrorCount);

/// <summary>How a typed colour was taken.</summary>
internal enum ThemeHexResult { Changed, Unchanged, Invalid }

/// <summary>
/// TH3: the state behind the colour dialog, free of any control so it can be tested: the saved overrides (<c>Config.ThemeOverrides</c>),
/// the draft being edited, and for each variant the preview and the per-role reading of it. Every edit builds a new immutable draft
/// (<see cref="ThemeColorOverrides"/>); a role set to its built-in colour is reset instead of stored, so "equal to the default" never
/// reaches the settings. The draft can be applied only when both variants pass the gate (<see cref="Block"/> is null) and it differs
/// from what is saved. Nothing is applied or saved here.
/// </summary>
internal sealed class ThemeConfiguratorModel
{
    private readonly Func<ThemeTokenTheme, ThemePaletteBase> _baseOf;
    private readonly Dictionary<ThemeTokenTheme, ThemePreview> _previews = new();
    private ThemeColorOverrides _draft;

    internal ThemeConfiguratorModel(ThemeColorOverrides saved, Func<ThemeTokenTheme, ThemePaletteBase> baseOf)
    {
        Saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _baseOf = baseOf ?? throw new ArgumentNullException(nameof(baseOf));
        _draft = saved;
    }

    /// <summary>Raised after every change of <see cref="Draft"/>.</summary>
    internal event EventHandler? Changed;

    /// <summary>What the profile has now.</summary>
    internal ThemeColorOverrides Saved { get; private set; }

    /// <summary>What the dialog shows and would apply.</summary>
    internal ThemeColorOverrides Draft => _draft;

    internal bool IsDirty => !_draft.Equals(Saved);

    /// <summary>The built-in colour of a role's primary seed in a variant (what "reset" returns to).</summary>
    internal Color BuiltIn(ThemeTokenTheme theme, string roleId) => _baseOf(theme).Colors[ThemeUserColors.Require(roleId).Seed];

    internal Color BuiltInAccent(ThemeTokenTheme theme) => BuiltIn(theme, ThemeUserColors.Accent);

    /// <summary>The palette and gate report the draft gives in a variant (cached until the draft changes).</summary>
    internal ThemePreview Preview(ThemeTokenTheme theme)
    {
        if (!_previews.TryGetValue(theme, out ThemePreview? preview))
            _previews[theme] = preview = ThemeCustomization.Preview(_baseOf(theme), _draft.ColorsFor(theme));
        return preview;
    }

    /// <summary>The eight roles of a variant in dialog order.</summary>
    internal IReadOnlyList<ThemeRoleView> Roles(ThemeTokenTheme theme) => ThemeUserColors.All.Select(role => Role(theme, role.Id)).ToArray();

    internal ThemeRoleView Role(ThemeTokenTheme theme, string roleId)
    {
        ThemeUserColor role = ThemeUserColors.Require(roleId);
        ThemePreview preview = Preview(theme);
        Color color = preview.Palette.Colors[role.Seed];
        (string measured, double minimum) = Measured(theme, role);
        Color counterpart = preview.Palette.Colors[role.Counterpart];
        double ratio = ContrastRules.Contrast(ThemeColorMath.Format(preview.Palette.Colors[measured]), ThemeColorMath.Format(counterpart));
        IReadOnlyList<ThemeContrastIssue> findings = ThemeCustomization.ForRole(preview.Contrast, theme, roleId);
        IReadOnlyDictionary<string, string> explicitSeeds = _draft.For(theme);
        return new ThemeRoleView(role, theme, color, ThemeColorMath.Format(color), role.Seeds.Any(explicitSeeds.ContainsKey),
            ThemeUserColors.OwnerOf(role.Counterpart)?.Id ?? roleId, ratio, minimum,
            ThemeColorMath.DisplayRatio(ratio) >= minimum, findings.Count(issue => issue.Severity == ThemeContrastSeverity.Error),
            findings.Count(issue => issue.Severity == ThemeContrastSeverity.Warning));
    }

    /// <summary>
    /// The colour whose contrast against the counterpart the row shows, and what it must reach. Normally the role's own seed and its
    /// minimum. A text pair (4.5) keeps the gate's text minimum. A border/mark pair (3 : 1) takes the lower value the gate reviewed for
    /// exactly that pair (the built-in Light border is 2.88 : 1 against 2.8 reviewed); when the gate has no rule for the seed itself
    /// (Dark's decorative border) the first related seed the gate does check against the counterpart (the control border, which follows
    /// the border) is shown instead.
    /// </summary>
    private static (string Seed, double Minimum) Measured(ThemeTokenTheme theme, ThemeUserColor role)
    {
        if (role.Minimum >= ContrastGate.TextMinimum) return (role.Seed, role.Minimum);
        IReadOnlyList<ContrastRule> rules = ContrastRules.For(theme == ThemeTokenTheme.Dark ? ContrastTheme.Dark : ContrastTheme.Light);
        double? Reviewed(string seed) => rules.Where(rule => rule.Backdrop == null && rule.Foreground == seed && rule.Background == role.Counterpart)
            .Select(rule => (double?)rule.Minimum).Min();
        foreach (string seed in role.Seeds)
            if (Reviewed(seed) is { } reviewed) return (seed, Math.Min(role.Minimum, reviewed));
        return (role.Seed, role.Minimum);
    }

    /// <summary>Sets a role's colour in a variant; the built-in colour resets the role instead. False when nothing changed.</summary>
    internal bool SetRole(ThemeTokenTheme theme, string roleId, Color color)
    {
        Color opaque = ThemeCustomization.ParseHex(ThemeColorMath.Format(color));
        ThemeColorOverrides next = opaque == BuiltIn(theme, roleId)
            ? _draft.ResetRole(theme, roleId) : _draft.WithRole(theme, roleId, opaque);
        return Replace(next);
    }

    /// <summary>The hex field: a valid <c>#RRGGBB</c> sets the role, anything else changes nothing.</summary>
    internal ThemeHexResult SetRoleHex(ThemeTokenTheme theme, string roleId, string? text)
    {
        if (!ThemeColorMath.TryParse(text, out Color color)) return ThemeHexResult.Invalid;
        return SetRole(theme, roleId, color) ? ThemeHexResult.Changed : ThemeHexResult.Unchanged;
    }

    internal bool ResetRole(ThemeTokenTheme theme, string roleId) => Replace(_draft.ResetRole(theme, roleId));

    /// <summary>"Reset all": both variants back to the built-in colours.</summary>
    internal bool ResetAll() => Replace(ThemeColorOverrides.Empty);

    /// <summary>Sets the accent of both variants to a preset (TH4).</summary>
    internal bool ApplyPreset(ThemeAccentPreset preset) => Replace(ThemeAccentPresets.Apply(_draft, preset, BuiltInAccent));

    /// <summary>The preset the draft's accent matches in both variants, or null.</summary>
    internal ThemeAccentPreset? ActivePreset => ThemeAccentPresets.Active(_draft, BuiltInAccent);

    /// <summary>Null while both variants pass the gate; otherwise why Apply is refused.</summary>
    internal ThemeApplyBlock? Block
    {
        get
        {
            foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Light, ThemeTokenTheme.Dark })
            {
                ThemePreview preview = Preview(theme);
                if (preview.Accepted) continue;
                string? owner = ThemeUserColors.All
                    .FirstOrDefault(role => ThemeCustomization.ForRole(preview.Contrast, theme, role.Id)
                        .Any(issue => issue.Severity == ThemeContrastSeverity.Error) && role.Seeds.Any(_draft.For(theme).ContainsKey))?.Id;
                return new ThemeApplyBlock(theme, owner, preview.Contrast.Errors.Count);
            }
            return null;
        }
    }

    /// <summary>The draft differs from what is saved and both variants pass the gate.</summary>
    internal bool CanApply => IsDirty && Block == null;

    /// <summary>The draft was saved: it is now the baseline.</summary>
    internal void MarkSaved(ThemeColorOverrides saved)
    {
        Saved = saved ?? throw new ArgumentNullException(nameof(saved));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Replace(ThemeColorOverrides next)
    {
        if (next.Equals(_draft)) return false;
        _draft = next;
        _previews.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
