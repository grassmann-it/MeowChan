using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace MeowChan.Desktop.Theming;

internal enum ThemeTokenTheme { Dark, Light }
internal enum ThemeTokenClassification { Unknown, Seed, Mirror, Variation, Shadow }

// These are findings, NOT seed aliases. Their derivation rules (TH1) live in ThemePalette.Rules, which keeps
// today's value pinned where a rule does not reproduce it and the source seeds are unchanged.
internal enum ThemeTokenVariation
{
    // Dark has its own disabled outline; Light currently reuses the decorative border.
    DisabledOutline,
    // Dark reuses Accent; Light has a separate, darker pressed tone with no Workspace seed.
    PressedAccent,
    // Dark reuses AccentHover; Light uses the separate pressed tone, not AccentHover.
    PressedAccentBorder,
    // Breadcrumb/hyperlink text, not an outline: Dark uses AccentHover (breadcrumb only);
    // Light uses the unseeded pressed tone.
    PressedAccentText,
    // Both themes use the accent RGB with 40% alpha, not an opaque seed mirror.
    AccentPreview,
    ToolbarToggleSurface,
    // Light-only thumb hover happens to equal Muted text; it is not a text role.
    ScrollBarHover
}

/// <summary>
/// T1 inventory of WorkspaceStyles.axaml, not a theme loader or a palette replacement.
/// Only exact, semantically justified aliases are mirrors. Workspace colour roles remain
/// independent seeds even when their current values coincide (e.g. Overlay/Panel,
/// ChipSurface/Raised, Progress/Accent in Dark). State variations are explicit findings;
/// <see cref="ThemePalette"/> gives each one an explicit derivation rule (TH1).
/// No resource dictionaries, brushes or application state are read or changed here.
/// </summary>
internal static class ThemeTokens
{
    internal static FrozenSet<string> Seeds { get; } = new[]
    {
        "Workspace.Background",
        "Workspace.Panel",
        "Workspace.Raised",
        "Workspace.Border",
        "Workspace.ControlBorder",
        "Workspace.Text",
        "Workspace.Muted",
        "Workspace.Accent",
        "Workspace.AccentSurface",
        "Workspace.Card",
        "Workspace.CardEdge",
        "Workspace.Overlay",
        "Workspace.Scrim",
        "Workspace.ReaderGreentext",
        "Workspace.ReaderCodeSurface",
        "Workspace.ReaderSpoiler",
        "Workspace.Hover",
        "Workspace.OnAccent",
        "Workspace.AccentHover",
        "Workspace.AccentHoverText",
        "Workspace.Error",
        "Workspace.ErrorSurface",
        "Workspace.Warning",
        "Workspace.WarningSurface",
        "Workspace.Success",
        "Workspace.SuccessSurface",
        "Workspace.Progress",
        "Workspace.ProgressTrack",
        "Workspace.Stats.Disk",
        "Workspace.Stats.DiskSurface",
        "Workspace.Stats.Read",
        "Workspace.Stats.ReadSurface",
        "Workspace.ChipSurface",
        "Workspace.ChipBorder",
        "Workspace.ChipSourceBorder",
        "Workspace.ChipExcludeBorder",
        "Workspace.ChipSelectedSurface",
        "Workspace.ChipSelectedBorder",
        "Workspace.TagCategory.Artist",
        "Workspace.TagCategory.Copyright",
        "Workspace.TagCategory.Character",
        "Workspace.TagCategory.Meta",
        "Workspace.TagCategory.General",
        "Workspace.CaptionCloseEdge",
        "Workspace.Favorite",
    }.ToFrozenSet(StringComparer.Ordinal);

    // BoxShadows are composite resources, NOT JSON colour seeds. Kept in the inventory
    // so dictionary coverage has no blanket ignore; their full shadow values are checked.
    internal const string ShadowSeed = "Workspace.Shadow.Overlay";
    internal static FrozenDictionary<string, string> ShadowMirrors { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MenuFlyoutBorderBoxShadow"] = ShadowSeed,
            ["FlyoutBorderBoxShadow"] = ShadowSeed,
            ["ComboBoxPopupBoxShadow"] = ShadowSeed,
            ["AutoCompleteBoxPopupBoxShadow"] = ShadowSeed,
            ["NotificationCardBoxShadows"] = ShadowSeed,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ThemeTokenMap Dark = CreateMap(light: false);
    private static readonly ThemeTokenMap Light = CreateMap(light: true);

    internal static ThemeTokenMap ForTheme(ThemeTokenTheme theme) => theme switch
    {
        ThemeTokenTheme.Dark => Dark,
        ThemeTokenTheme.Light => Light,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    private static ThemeTokenMap CreateMap(bool light)
    {
        // Common keys exist in BOTH dictionaries. Mapping is by role, not a hex lookup:
        // popups -> Overlay, chip surface/border -> chip seeds, selected rows -> AccentSurface.
        // Semi progress fills intentionally follow Accent, NOT Workspace.Progress (Light differs).
        var mirrors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ButtonDefaultPrimaryForeground"] = "Workspace.Text",
            ["ButtonDefaultPointeroverBackground"] = "Workspace.Hover",
            ["ButtonDefaultPressedBackground"] = "Workspace.AccentSurface",
            ["ButtonDefaultDisabledForeground"] = "Workspace.Muted",
            ["ButtonDefaultPointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["ButtonDefaultPressedBorderBrush"] = "Workspace.Accent",
            ["ButtonSolidForeground"] = "Workspace.OnAccent",
            ["ButtonSolidPrimaryBackground"] = "Workspace.Accent",
            ["ButtonSolidPrimaryBorderBrush"] = "Workspace.Accent",
            ["ButtonSolidPrimaryPointeroverBackground"] = "Workspace.AccentHover",
            ["ButtonSolidPrimaryPointeroverBorderBrush"] = "Workspace.AccentHover",
            ["ButtonSolidPrimaryPressedForeground"] = "Workspace.OnAccent",
            ["ButtonSolidDisabledBackground"] = "Workspace.Raised",
            ["ButtonSolidDisabledForeground"] = "Workspace.Muted",
            ["DataGridRowPointeroverBackground"] = "Workspace.Hover",
            ["DataGridRowSelectedBackground"] = "Workspace.AccentSurface",
            ["DataGridRowSelectedPointeroverBackground"] = "Workspace.AccentSurface",
            ["DataGridCellFocusBorderBrush"] = "Workspace.Accent",
            ["DataGridLineBrush"] = "Workspace.Border",
            ["TextBoxDefaultBorderBrush"] = "Workspace.ControlBorder",
            ["TextBoxPointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["ComboBoxSelectorBorderBrush"] = "Workspace.ControlBorder",
            ["ComboBoxSelectorPointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["ClosableTagForeground"] = "Workspace.Text",
            ["ClosableTagBackground"] = "Workspace.ChipSurface",
            ["ClosableTagBorder"] = "Workspace.ChipBorder",
            ["ClosableTagCloseButtonForeground"] = "Workspace.Muted",
            ["ClosableTagCloseButtonPointeroverForeground"] = "Workspace.Text",
            ["ClosableTagCloseButtonPressedForeground"] = "Workspace.Accent",
            ["MenuFlyoutBackground"] = "Workspace.Overlay",
            ["ComboBoxPopupBackground"] = "Workspace.Overlay",
            ["AutoCompleteBoxPopupBackground"] = "Workspace.Overlay",
            ["PopConfirmBackground"] = "Workspace.Overlay",
            ["NotificationCardBackground"] = "Workspace.Overlay",
            ["FlyoutBackground"] = "Workspace.Overlay",
            ["ToolTipBackground"] = "Workspace.Overlay",
            ["MenuFlyoutBorderBrush"] = "Workspace.ControlBorder",
            ["ComboBoxPopupBorderBrush"] = "Workspace.ControlBorder",
            ["AutoCompleteBoxPopupBorderBrush"] = "Workspace.ControlBorder",
            ["PopConfirmBorderBrush"] = "Workspace.ControlBorder",
            ["NotificationCardBorderBrush"] = "Workspace.ControlBorder",
            ["FlyoutBorderBrush"] = "Workspace.ControlBorder",
            ["ToolTipBorderBrush"] = "Workspace.ControlBorder",
            ["ToolTipForeground"] = "Workspace.Text",
            ["FlyoutForeground"] = "Workspace.Text",
            ["PopConfirmContentForeground"] = "Workspace.Muted",
            ["MenuItemPointeroverBackground"] = "Workspace.Hover",
            ["ComboBoxItemPointeroverBackground"] = "Workspace.Hover",
            ["ComboBoxItemFocusBackground"] = "Workspace.Hover",
            ["ListBoxItemPointeroverBackground"] = "Workspace.Hover",
            ["TabItemLineHeaderPointeroverBackground"] = "Workspace.Hover",
            ["ComboBoxSelectorPointeroverBackground"] = "Workspace.Hover",
            ["MenuItemPressedBackground"] = "Workspace.AccentSurface",
            ["ComboBoxItemPressedBackground"] = "Workspace.AccentSurface",
            ["ListBoxItemPressedBackground"] = "Workspace.AccentSurface",
            ["TabItemLineHeaderPressedBackground"] = "Workspace.AccentSurface",
            ["ComboBoxSelectorPressedBackground"] = "Workspace.AccentSurface",
            ["ComboBoxItemSelectedBackground"] = "Workspace.AccentSurface",
            ["ComboBoxItemSelectedPointeroverBackground"] = "Workspace.AccentSurface",
            ["ListBoxItemSelectedBackground"] = "Workspace.AccentSurface",
            ["ListBoxItemSelectedPointeroverBackground"] = "Workspace.AccentSurface",
            ["MenuItemForeground"] = "Workspace.Text",
            ["MenuItemPointeroverForeground"] = "Workspace.Text",
            ["ComboBoxItemForeground"] = "Workspace.Text",
            ["ComboBoxItemPointeroverForeground"] = "Workspace.Text",
            ["ComboBoxItemFocusForeground"] = "Workspace.Text",
            ["ComboBoxItemPressedForeground"] = "Workspace.Text",
            ["ComboBoxItemSelectedForeground"] = "Workspace.Text",
            ["ListBoxItemPointeroverForeground"] = "Workspace.Text",
            ["ListBoxItemPressedForeground"] = "Workspace.Text",
            ["ListBoxItemSelectedForeground"] = "Workspace.Text",
            ["MenuItemInputGestureTextForeground"] = "Workspace.Muted",
            ["MenuItemDisabledForeground"] = "Workspace.Muted",
            ["MenuItemDisabledInputGestureTextForeground"] = "Workspace.Muted",
            ["ListBoxItemDisabledForeground"] = "Workspace.Muted",
            ["TabItemHeaderDisabledForeground"] = "Workspace.Muted",
            ["TabItemLineHeaderForeground"] = "Workspace.Muted",
            ["TabItemLineHeaderPointeroverForeground"] = "Workspace.Text",
            ["TabItemLineHeaderSelectedForeground"] = "Workspace.Text",
            ["TabItemLinePipePointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["TabItemLinePipePressedBorderBrush"] = "Workspace.Accent",
            ["TabItemLinePipeSelectedBackground"] = "Workspace.Accent",
            ["TabItemIconSelectedForeground"] = "Workspace.Accent",
            ["ComboBoxSelectorBackground"] = "Workspace.Raised",
            ["ComboBoxSelectorFocusBackground"] = "Workspace.Raised",
            ["ComboBoxSelectorFocusBorderBrush"] = "Workspace.Accent",
            ["ComboBoxSelectorPressedBorderBrush"] = "Workspace.Accent",
            ["TextBoxFocusBorderBrush"] = "Workspace.Accent",
            ["TextBoxBorderedPointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["ComboBoxSelectorBorderedFocusBorderBrush"] = "Workspace.Accent",
            ["ComboBoxSelectorBorderedPointeroverBorderBrush"] = "Workspace.ControlBorder",
            ["ComboBoxSelectorBorderedPressedBorderBrush"] = "Workspace.Accent",
            ["TextBoxSelectionBackground"] = "Workspace.Accent",
            ["TextBoxSelectionForeground"] = "Workspace.OnAccent",
            ["TextBlockSelectionBackground"] = "Workspace.Accent",
            ["TextBlockSelectionForeground"] = "Workspace.OnAccent",
            ["AdornerLayerBorderBrush"] = "Workspace.Accent",
            ["ToggleButtonPrimaryIndeterminateBorderBrush"] = "Workspace.Accent",
            ["CheckBoxCheckedDefaultBackground"] = "Workspace.Accent",
            ["CheckBoxCheckedDefaultBorderBrush"] = "Workspace.Accent",
            ["CheckBoxCheckedPointeroverBackground"] = "Workspace.AccentHover",
            ["CheckBoxCheckedPointeroverBorderBrush"] = "Workspace.AccentHover",
            ["CheckBoxDefaultBorderBrush"] = "Workspace.ControlBorder",
            ["CheckBoxPointeroverBorderBrush"] = "Workspace.Accent",
            ["CheckBoxGlyphFill"] = "Workspace.OnAccent",
            ["CheckBoxCardCheckedBackground"] = "Workspace.AccentSurface",
            ["CheckBoxCardCheckedBorderBrush"] = "Workspace.Accent",
            ["CheckBoxCardCheckedPointeroverBorderBrush"] = "Workspace.AccentHover",
            ["RadioButtonCheckIconDefaultBackground"] = "Workspace.Accent",
            ["RadioButtonCheckIconDefaultBorderBrush"] = "Workspace.Accent",
            ["RadioButtonCheckIconPointeroverBackground"] = "Workspace.AccentHover",
            ["RadioButtonCheckIconPointeroverBorderBrush"] = "Workspace.AccentHover",
            ["RadioButtonUncheckIconDefaultBorderBrush"] = "Workspace.ControlBorder",
            ["RadioButtonUncheckIconPointeroverBorderBrush"] = "Workspace.Accent",
            ["RadioButtonCheckGlyphFill"] = "Workspace.OnAccent",
            ["RadioButtonButtonCheckedForeground"] = "Workspace.Accent",
            ["RadioButtonButtonCheckedBackground"] = "Workspace.AccentSurface",
            ["RadioButtonCardCheckedBackground"] = "Workspace.AccentSurface",
            ["RadioButtonCardCheckDefaultBorderBrush"] = "Workspace.Accent",
            ["RadioButtonCardCheckPointeroverBorderBrush"] = "Workspace.AccentHover",
            ["SliderTrackForeground"] = "Workspace.Accent",
            ["SliderThumbBorderBrush"] = "Workspace.Accent",
            ["SliderThumbPointeroverBorderBrush"] = "Workspace.AccentHover",
            // AX-02: ToggleSwitch tracks and knob (switch off = ControlBorder, on = Accent, knob = OnAccent; all 3:1 or better on the surfaces).
            ["ToggleSwitchContainerUnCheckedDefaultBackground"] = "Workspace.ControlBorder",
            ["ToggleSwitchContainerUnCheckedPointeroverBackground"] = "Workspace.ControlBorder",
            ["ToggleSwitchContainerUnCheckedPressedBackground"] = "Workspace.ControlBorder",
            ["ToggleSwitchContainerCheckedDefaultBackground"] = "Workspace.Accent",
            ["ToggleSwitchContainerCheckedPointeroverBackground"] = "Workspace.AccentHover",
            ["ToggleSwitchIndicatorBackground"] = "Workspace.OnAccent",
            ["ProgressBarIndicatorBrush"] = "Workspace.Accent",
            ["ProgressBarPrimaryForeground"] = "Workspace.Accent",
            ["PaginationButtonSelectedForeground"] = "Workspace.Accent",
            ["PaginationButtonSelectedBackground"] = "Workspace.AccentSurface",
            ["TabItemLineHeaderSelectedBackground"] = "Workspace.AccentSurface",
            ["TabItemButtonHeaderSelectedBackground"] = "Workspace.AccentSurface",
            ["TabItemButtonHeaderSelectedForeground"] = "Workspace.Accent",
            ["BreadcrumbItemPointeroverForeground"] = "Workspace.Accent",
            ["BannerInformationBackground"] = "Workspace.AccentSurface",
            ["BannerInformationBorderBrush"] = "Workspace.Accent",
            ["NotificationCardInformationIconForeground"] = "Workspace.Accent",
            ["NotificationCardLightInformationBackground"] = "Workspace.AccentSurface",
            ["NotificationCardLightInformationBorderBrush"] = "Workspace.Accent",
            ["LoadingIconForeground"] = "Workspace.Accent",
            ["BadgePrimaryBadgeBackground"] = "Workspace.Accent",
            ["BadgeForeground"] = "Workspace.OnAccent",
            ["BadgeInvertedPrimaryBadgeForeground"] = "Workspace.Accent",
            ["BadgeLightPrimaryBadgeBackground"] = "Workspace.AccentSurface",
            ["BadgeLightPrimaryBadgeForeground"] = "Workspace.Accent",
        };
        var variations = new Dictionary<string, ThemeTokenVariation>(StringComparer.Ordinal)
        {
            ["ButtonDefaultDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["ButtonSolidDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["CheckBoxCheckedDisabledBackground"] = ThemeTokenVariation.DisabledOutline,
            ["CheckBoxCheckedDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["CheckBoxCardCheckedDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["RadioButtonCheckIconDisabledBackground"] = ThemeTokenVariation.DisabledOutline,
            ["RadioButtonCheckIconDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["SliderThumbDisabledBorderBrush"] = ThemeTokenVariation.DisabledOutline,
            ["ToggleSwitchContainerCheckedDisabledBackground"] = ThemeTokenVariation.DisabledOutline,
            ["ButtonSolidPrimaryPressedBackground"] = ThemeTokenVariation.PressedAccent,
            ["ButtonSolidPrimaryPressedBorderBrush"] = ThemeTokenVariation.PressedAccent,
            ["CheckBoxCheckedPressedBackground"] = ThemeTokenVariation.PressedAccent,
            ["CheckBoxCheckedPressedBorderBrush"] = ThemeTokenVariation.PressedAccent,
            ["RadioButtonCheckIconPressedBackground"] = ThemeTokenVariation.PressedAccent,
            ["RadioButtonCheckIconPressedBorderBrush"] = ThemeTokenVariation.PressedAccent,
            ["SliderThumbPressedBorderBrush"] = ThemeTokenVariation.PressedAccent,
            ["ToggleSwitchContainerCheckedPressedBackground"] = ThemeTokenVariation.PressedAccent,
            ["CheckBoxPressedBorderBrush"] = ThemeTokenVariation.PressedAccentBorder,
            ["CheckBoxCardCheckedPressedBorderBrush"] = ThemeTokenVariation.PressedAccentBorder,
            ["RadioButtonUncheckIconPressedBorderBrush"] = ThemeTokenVariation.PressedAccentBorder,
            ["RadioButtonCardCheckPressedBorderBrush"] = ThemeTokenVariation.PressedAccentBorder,
            ["BreadcrumbItemPressedForeground"] = ThemeTokenVariation.PressedAccentText,
            ["GridSplitterPreviewBackground"] = ThemeTokenVariation.AccentPreview,
            ["Workspace.ToolbarToggleSurface"] = ThemeTokenVariation.ToolbarToggleSurface,
            ["DataGridDropLocationIndicatorBackground"] = ThemeTokenVariation.AccentPreview,
        };

        if (light)
        {
            // These overrides exist only in Light. Dark inherits Semi/Ursa defaults;
            // absence is NOT permission to invent the corresponding Dark overrides.
            mirrors.Add("ScrollBarThumbForeground", "Workspace.ControlBorder");
            mirrors.Add("TextBoxPlaceholderForeground", "Workspace.Muted");
            mirrors.Add("ComboBoxSelectorPlaceHolderForeground", "Workspace.Muted");
            mirrors.Add("CheckBoxDisabledForeground", "Workspace.Muted");
            mirrors.Add("RadioButtonDisabledForeground", "Workspace.Muted");
            mirrors.Add("ExpanderHeaderDisabledForeground", "Workspace.Muted");
            mirrors.Add("TextBoxDisabledForeground", "Workspace.Muted");
            mirrors.Add("ExpanderIconForeground", "Workspace.Muted");
            mirrors.Add("ExpanderContentForeground", "Workspace.Text");
            mirrors.Add("ExpanderHeaderForeground", "Workspace.Text");
            mirrors.Add("ExpanderSeparatorBorderBrush", "Workspace.Border");
            mirrors.Add("MenuItemSeparatorBackground", "Workspace.Border");
            mirrors.Add("CommandBarSeparatorBackground", "Workspace.Border");
            mirrors.Add("DateTimePickerSeparatorBackground", "Workspace.Border");
            // Empty slider/progress rails share the neutral track role; their fills do not.
            mirrors.Add("SliderTrackBackground", "Workspace.ProgressTrack");
            mirrors.Add("SliderTickForeground", "Workspace.Border");
            mirrors.Add("HyperlinkButtonForeground", "Workspace.Accent");
            mirrors.Add("HyperlinkButtonPointeroverForeground", "Workspace.AccentHoverText");
            variations.Add("HyperlinkButtonPressedForeground", ThemeTokenVariation.PressedAccentText);
            variations.Add("ScrollBarThumbPointeroverForeground", ThemeTokenVariation.ScrollBarHover);
        }
        return new ThemeTokenMap(mirrors.ToFrozenDictionary(StringComparer.Ordinal),
            variations.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>Immutable, case-sensitive classification for one built-in theme's explicit resources.</summary>
internal sealed record ThemeTokenMap(
    FrozenDictionary<string, string> Mirrors,
    FrozenDictionary<string, ThemeTokenVariation> Variations)
{
    internal ThemeTokenClassification Classify(string key)
    {
        if (ThemeTokens.Seeds.Contains(key)) return ThemeTokenClassification.Seed;
        if (Mirrors.ContainsKey(key)) return ThemeTokenClassification.Mirror;
        if (Variations.ContainsKey(key)) return ThemeTokenClassification.Variation;
        if (key == ThemeTokens.ShadowSeed || ThemeTokens.ShadowMirrors.ContainsKey(key))
            return ThemeTokenClassification.Shadow;
        return ThemeTokenClassification.Unknown;
    }
}
