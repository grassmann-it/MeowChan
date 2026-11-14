using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH3: the sentences of the colour dialog built from a model state, in the language shown. The contrast is always written out
/// ("4.8 : 1 – good", "3.1 : 1 – too weak for text"), never left to a colour; the ratio is cut to one decimal
/// (<see cref="ThemeColorMath.DisplayRatio"/>), so a pair the gate refuses never reads as 4.5. Pure apart from <see cref="Texts"/>.
/// </summary>
internal static class ThemeColorsText
{
    /// <summary>The name of Light or Dark, as the tabs and the preview title say it.</summary>
    internal static string VariantName(ThemeTokenTheme theme) => Texts.T(theme == ThemeTokenTheme.Light ? "theme.light" : "theme.dark");

    /// <summary>The name of a role (<c>theme.color.&lt;id&gt;</c>).</summary>
    internal static string RoleName(string roleId) => Texts.T("theme.color." + roleId);

    /// <summary>The name of an accent preset (<c>theme.accent.&lt;id&gt;</c>).</summary>
    internal static string PresetName(string presetId) => Texts.T("theme.accent." + presetId);

    /// <summary>"4.8 : 1 – good", "3.1 : 1 – too weak for text" or "2.4 : 1 – too weak for borders and marks": the displayed pair of a role.</summary>
    internal static string Contrast(ThemeRoleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string key = view.PairPasses ? "theme.colors.contrast.good" : view.IsTextPair ? "theme.colors.contrast.weakText" : "theme.colors.contrast.weakMark";
        return Texts.T(key, Ratio(view.Ratio));
    }

    /// <summary>The ratio cut to one decimal, in the language's number format ("4.8", German "4,8"; always one decimal).</summary>
    internal static string Ratio(double ratio) => ThemeColorMath.DisplayRatio(ratio).ToString("0.0", Texts.Culture);

    /// <summary>"Measured against: Surface": what the displayed ratio of a role is taken against.</summary>
    internal static string Against(ThemeRoleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Texts.T("theme.colors.contrast.against", RoleName(view.CounterpartRoleId));
    }

    /// <summary>
    /// The second line of a role: other text pairs of the role below 4.5 : 1 (they block Apply), else borders and marks below 3 : 1
    /// (they only warn); empty when there is nothing more to say.
    /// </summary>
    internal static string Extra(ThemeRoleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        // A failing displayed text pair is one of the role's errors and is already said by <see cref="Contrast"/>.
        int others = view.PairPasses ? view.Errors : Math.Max(0, view.Errors - (view.IsTextPair ? 1 : 0));
        if (others > 0) return Texts.T("theme.colors.contrast.otherErrors", others);
        return view.Warnings > 0 && view.PairPasses ? Texts.T("theme.colors.contrast.warnings", view.Warnings) : string.Empty;
    }

    /// <summary>"Default" or "Changed".</summary>
    internal static string State(ThemeRoleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Texts.T(view.IsCustom ? "theme.colors.state.custom" : "theme.colors.state.default");
    }

    /// <summary>The sentence under Apply while the gate refuses the draft (naming the role when one owns the failure), or null.</summary>
    internal static string? BlockText(ThemeApplyBlock? block) => block == null ? null
        : block.RoleId is { } role ? Texts.T("theme.colors.blocked", VariantName(block.Theme), RoleName(role))
        : Texts.T("theme.colors.blockedGeneric", VariantName(block.Theme));
}
