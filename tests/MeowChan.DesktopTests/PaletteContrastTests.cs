using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// Palette review 2026-11-13: the minimum contrast of the key colour pairs in both theme dictionaries of
// WorkspaceStyles.axaml, computed from the hex values (WCAG 2.2 relative luminance), not from rendered pixels.
// Text needs 4.5:1, boundaries and marks 3:1. No Avalonia application.
public sealed class PaletteContrastTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] TextSurfaces =
    {
        "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover", "Workspace.Overlay",
        "Workspace.AccentSurface", "Workspace.ChipSurface", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler"
    };

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void BodyAndSecondaryTextReadOnEverySurface(string theme)
    {
        foreach (string surface in TextSurfaces)
        {
            AssertContrast(theme, "Workspace.Text", surface, 4.5);
            // Spoiler and the hidden text colour are the same value until revealed; Muted is never drawn on it.
            if (surface != "Workspace.ReaderSpoiler") AssertContrast(theme, "Workspace.Muted", surface, 4.5);
        }
        foreach (string surface in new[] { "Workspace.ErrorSurface", "Workspace.WarningSurface", "Workspace.SuccessSurface" })
            AssertContrast(theme, "Workspace.Text", surface, 4.5);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void LinksAccentAndTextOnAccentReadAsText(string theme)
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover",
                     "Workspace.AccentSurface", "Workspace.ChipSurface", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler" })
            AssertContrast(theme, "Workspace.Accent", surface, 4.5);
        AssertContrast(theme, "Workspace.OnAccent", "Workspace.Accent", 4.5);
        AssertContrast(theme, "Workspace.OnAccent", "Workspace.AccentHover", 4.5);
        AssertContrast(theme, "ButtonSolidPrimaryPressedForeground", "ButtonSolidPrimaryPressedBackground", 4.5);
        // The pressed/hover accent as an icon tint on the selection surface (media toggle) is a graphic: 3:1.
        AssertContrast(theme, "Workspace.AccentHover", "Workspace.AccentSurface", 3.0);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void StatusColoursReadAsTextAndAsBorders(string theme)
    {
        foreach (string status in new[] { "Error", "Warning", "Success" })
        {
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace." + status + "Surface" })
                AssertContrast(theme, "Workspace." + status, surface, 4.5);
            // The banner outline on its own tinted surface and on the panel next to it.
            AssertContrast(theme, "Workspace." + status, "Workspace." + status + "Surface", 3.0);
            AssertContrast(theme, "Workspace." + status, "Workspace.Panel", 3.0);
        }
        AssertContrast(theme, "Workspace.Warning", "Workspace.ChipSurface", 3.0); // tile favourite star fill
        AssertContrast(theme, "Workspace.Progress", "Workspace.ProgressTrack", 3.0);
    }

    // WCAG 1.4.11: the boundary of a button or field needs 3:1 against what surrounds it, in Dark as in Light.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void InteractiveOutlinesAndFocusRingsReachThreeToOne(string theme)
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay" })
        {
            AssertContrast(theme, "Workspace.ControlBorder", surface, 3.0);
            AssertContrast(theme, "Workspace.ChipBorder", surface, 3.0);
            AssertContrast(theme, "Workspace.Accent", surface, 3.0);
        }
        AssertContrast(theme, "Workspace.ControlBorder", "Workspace.AccentSurface", 3.0);
        AssertContrast(theme, "Workspace.Background", "Workspace.Accent", 3.0); // inner focus ring on the accent fill
        AssertContrast(theme, "Workspace.CardEdge", "Workspace.Background", 3.0); // reader post cards are workspace cards on the page (D3)
        // The field outlines Semi leaves invisible are set to the interactive outline in both themes.
        foreach (string key in new[] { "TextBoxDefaultBorderBrush", "TextBoxPointeroverBorderBrush", "ComboBoxSelectorBorderBrush", "ComboBoxSelectorPointeroverBorderBrush" })
            Assert.Equal(Color(theme, "Workspace.ControlBorder"), Color(theme, key));
        Assert.Equal(Color(theme, "Workspace.ControlBorder"), Color(theme, "ButtonDefaultPointeroverBorderBrush"));
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ReaderInlineColoursReadOnEveryPostSurface(string theme)
    {
        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background", "Workspace.ReaderCodeSurface", "Workspace.ReaderSpoiler" })
            AssertContrast(theme, "Workspace.ReaderGreentext", surface, 4.5);
        foreach (string text in new[] { "Workspace.Text", "Workspace.Muted", "Workspace.Accent" })
        {
            AssertContrast(theme, text, "Workspace.ReaderCodeSurface", 4.5);
            AssertContrast(theme, text, "Workspace.Card", 4.5); // dead references and notes use Muted; a post is a Card
        }
    }

    // Light/Dark review 2026-11-13, item 8: a hidden spoiler is one area in its own colour, only 1.10:1 (Light) and 1.09:1 (Dark)
    // against the active card. The reader strokes it with the interactive outline (Workspace.ControlBorder), which needs 3:1
    // against every surface a post sits on (card, active card, the page for the opening post) and against the spoiler area inside.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void HiddenSpoilerOutlineReadsOnEveryPostSurfaceAndAgainstTheSpoilerArea(string theme)
    {
        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background", "Workspace.ReaderSpoiler" })
            AssertContrast(theme, "Workspace.ControlBorder", surface, 3.0);
    }

    // The dark bars of the Light theme are the download progress bars (Workspace.Progress is the dark accent brown #4A2203 there; title
    // bar, header, footer and toolbars are warm Panel/Background tones). The fill is the bar: it needs 3:1 against every surface it
    // is drawn on and against its own track. Scrollbar thumbs are bars too.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ProgressBarFillReadsOnItsTrackAndOnEverySurfaceItIsDrawnOn(string theme)
    {
        AssertContrast(theme, "Workspace.Progress", "Workspace.ProgressTrack", 3.0);
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover", "Workspace.AccentSurface", "Workspace.Overlay" })
            AssertContrast(theme, "Workspace.Progress", surface, 3.0);
    }

    // Owner decision: the Dark fill is the accent blue again (it was a light grey). UX review 2026-11-13: the track moved from #30363E
    // (the Hover fill, 1.00:1, so the empty part vanished on a hovered row) to #424A54; the fill reads on it at 3.67:1.
    [Fact]
    public void DarkProgressFillIsTheAccentBlueAndReadsOnItsTrack()
    {
        Assert.Equal(Color("Dark", "Workspace.Accent"), Color("Dark", "Workspace.Progress"));
        AssertContrast("Dark", "Workspace.Progress", "Workspace.ProgressTrack", 3.5);
    }

    // The empty part must not be the colour of any row fill, and stays faintly visible on the row states of the Downloads list.
    [Theory]
    [InlineData("Workspace.Panel", 1.8), InlineData("Workspace.Raised", 1.6), InlineData("Workspace.Hover", 1.3), InlineData("Workspace.AccentSurface", 1.35)]
    public void DarkProgressTrackIsNotTheHoverFillAndStaysVisibleOnTheRowStates(string surface, double minimum)
    {
        Assert.Equal("#424A54", Color("Dark", "Workspace.ProgressTrack"));
        Assert.NotEqual(Color("Dark", "Workspace.Hover"), Color("Dark", "Workspace.ProgressTrack"));
        AssertContrast("Dark", "Workspace.ProgressTrack", surface, minimum);
    }

    // Light progress variant A: the fill is the dark accent brown (one step below the pressed accent #662F05), not a neutral grey, and
    // 3.4:1 or more on the track and 8.5:1 or more on every row state; the count text beside it stays readable on the weakest row (Hover).
    [Fact]
    public void LightProgressFillIsTheDarkAccentBrownAndReadsOnTheTrackAndEveryRowState()
    {
        Assert.Equal("#4A2203", Color("Light", "Workspace.Progress"));
        double red = Channel(Color("Light", "Workspace.Progress"), 0), blue = Channel(Color("Light", "Workspace.Progress"), 2);
        Assert.True(red > blue, "the Light progress fill is warm (brown), not a neutral grey");
        AssertContrast("Light", "Workspace.Progress", "Workspace.ProgressTrack", 3.4);
        foreach (string surface in new[] { "Workspace.Panel", "Workspace.Raised", "Workspace.Hover", "Workspace.AccentSurface", "Workspace.Card", "Workspace.Background" })
            AssertContrast("Light", "Workspace.Progress", surface, 8.4);
        AssertContrast("Light", "Workspace.Text", "Workspace.Hover", 10.7);
        AssertContrast("Light", "Workspace.Muted", "Workspace.Hover", 4.8);
    }

    // The Downloads progress bar and the inspector bar are 4 dip high (3 dip left one clean pixel row after antialiasing) and the
    // saved count is the SemiBold number over the bar end.
    [Fact]
    public void ProgressBarsAreFourDipHighAndTheSavedCountIsSemiBold()
    {
        string window = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(window, "<ProgressBar [^>]*Height=\"4\" MinHeight=\"0\" MaxHeight=\"4\"").Count);
        Assert.DoesNotContain("MaxHeight=\"3\"", window);
        Assert.Contains("Text=\"{Binding CompletedMedia}\" Classes=\"mono\" FontSize=\"{StaticResource Workspace.Text.Meta}\" FontWeight=\"SemiBold\"", window);
    }

    // Owner decision: the empty track is clearly visible in Light, a warm mid-grey (#857D74). 3:1 against the surfaces of the lists and
    // panels is pinned; against Raised, Hover and AccentSurface 3:1 cannot hold together with a 3:1 progress fill (that needs a track
    // luminance of at most 0.19 and at least 0.199), so those three are held to 2.4:1, well above the earlier 1.05 to 1.45.
    [Theory]
    [InlineData("Workspace.Background", 3.0), InlineData("Workspace.Panel", 3.0), InlineData("Workspace.Card", 3.0), InlineData("Workspace.Overlay", 3.0)]
    [InlineData("Workspace.Raised", 2.4), InlineData("Workspace.Hover", 2.4), InlineData("Workspace.AccentSurface", 2.4)]
    public void LightProgressTrackIsVisibleAgainstTheSurfaces(string surface, double minimum) =>
        AssertContrast("Light", "Workspace.ProgressTrack", surface, minimum);

    // UX review 2026-11-13: the decorative line colour was #A89F92 in Light (2.22:1 on Panel, 1.79:1 on Raised); #948A7D keeps lines visible
    // on the column head and the selected row. Still decorative (below 3:1, the interactive outline is ControlBorder), and the mirrored
    // Semi keys follow it so grid lines and disabled outlines are one value.
    [Theory]
    [InlineData("Workspace.Panel", 2.8), InlineData("Workspace.Background", 2.5), InlineData("Workspace.Raised", 2.3), InlineData("Workspace.AccentSurface", 2.3), InlineData("Workspace.Card", 3.1)]
    public void LightDecorativeBorderIsVisibleOnEverySurfaceAndTheMirroredKeysFollowIt(string surface, double minimum)
    {
        Assert.Equal("#948A7D", Color("Light", "Workspace.Border"));
        AssertContrast("Light", "Workspace.Border", surface, minimum);
        foreach (string key in new[] { "DataGridLineBrush", "ButtonDefaultDisabledBorderBrush", "ButtonSolidDisabledBorderBrush" })
            Assert.Equal(Color("Light", "Workspace.Border"), Color("Light", key));
    }

    // The status chips (Border.workspace-status-*) have a 1-px outline in the tone colour: the tint alone is 1.0 to 1.3:1 on Panel.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void StatusChipOutlineInTheToneReadsOnItsOwnTint(string theme)
    {
        foreach (string tone in new[] { "Warning", "Error", "Success", "Accent" })
        {
            string surface = tone == "Accent" ? "Workspace.AccentSurface" : "Workspace." + tone + "Surface";
            AssertContrast(theme, "Workspace." + tone, surface, theme == "Dark" ? 5.0 : 5.5);
        }
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        foreach ((string state, string tone) in new[] { ("active", "Accent"), ("warning", "Warning"), ("error", "Error"), ("success", "Success") })
        {
            string line = styles.Split('\n').Single(l => l.Contains("Selector=\"Border.workspace-status-" + state + "\""));
            Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource Workspace." + tone + "}\"", line);
            Assert.Contains("Property=\"BorderThickness\" Value=\"1\"", line);
        }
    }

    // AccentHover is a button surface (white text 6.20:1 in Light); as a text colour it fell to 3.82:1 on Hover. The checked media toggle
    // under the pointer uses its own text token, 4.5:1 or more on every surface the toggle sits on.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void AccentHoverTextReadsOnEverySurfaceTheCheckedMediaToggleSitsOn(string theme)
    {
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Hover", "Workspace.Raised", "Workspace.Panel", "Workspace.Background", "Workspace.Card" })
            AssertContrast(theme, "Workspace.AccentHoverText", surface, 4.5);
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource Workspace.AccentHoverText}\"", styles);
        Assert.DoesNotContain("Property=\"Foreground\" Value=\"{DynamicResource Workspace.AccentHover}\"", styles);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TagCategoryCaptionColoursReadOnChipsAndPanels(string theme)
    {
        foreach (string category in new[] { "Artist", "Copyright", "Character", "Meta", "General" })
            foreach (string surface in new[] { "Workspace.ChipSurface", "Workspace.Panel", "Workspace.Raised" })
                AssertContrast(theme, "Workspace.TagCategory." + category, surface, 4.5);
    }

    // Light list 4, 7, 12-15: Semi's translucent greys and blue are replaced by palette values (see the comment in WorkspaceStyles.axaml).
    [Fact]
    public void LightDisabledTextIsOpaqueMutedInsteadOfThirtyFivePercentBlack()
    {
        foreach (string key in new[] { "CheckBoxDisabledForeground", "RadioButtonDisabledForeground", "ExpanderHeaderDisabledForeground", "TextBoxDisabledForeground" })
        {
            Assert.Equal(Color("Light", "Workspace.Muted"), Color("Light", key));
            foreach (string surface in new[] { "Workspace.Panel", "Workspace.Background", "Workspace.Card", "Workspace.Raised" })
                AssertContrast("Light", key, surface, 4.5);
        }
    }

    [Fact]
    public void LightRadioButtonsUseTheInteractiveOutlineAndTheAccentTonesNotSemiBlue()
    {
        // The ring is a non-text boundary: 3:1 on every surface (4.12:1 on Background, 4.64:1 on Panel, 3.75:1 on Raised).
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay", "Workspace.AccentSurface" })
            AssertContrast("Light", "RadioButtonUncheckIconDefaultBorderBrush", surface, 3.0);
        AssertContrast("Light", "RadioButtonUncheckIconDefaultBorderBrush", "Workspace.Panel", 4.5);
        Assert.Equal(Color("Light", "Workspace.Accent"), Color("Light", "RadioButtonUncheckIconPointeroverBorderBrush"));
        Assert.Equal(Color("Light", "Workspace.Accent"), Color("Light", "RadioButtonCheckIconDefaultBackground"));
        Assert.Equal(Color("Light", "Workspace.Accent"), Color("Light", "RadioButtonCheckIconDefaultBorderBrush"));
        Assert.Equal(Color("Light", "Workspace.AccentHover"), Color("Light", "RadioButtonCheckIconPointeroverBackground"));
        Assert.Equal(Color("Light", "Workspace.AccentHover"), Color("Light", "RadioButtonCheckIconPointeroverBorderBrush"));
        Assert.Equal(Color("Light", "ButtonSolidPrimaryPressedBackground"), Color("Light", "RadioButtonCheckIconPressedBackground"));
        Assert.Equal(Color("Light", "ButtonSolidPrimaryPressedBackground"), Color("Light", "RadioButtonCheckIconPressedBorderBrush"));
        Assert.Equal(Color("Light", "ButtonSolidPrimaryPressedBackground"), Color("Light", "RadioButtonUncheckIconPressedBorderBrush"));
        foreach (string state in new[] { "Default", "Pointerover" })
            AssertContrast("Light", "RadioButtonCheckIcon" + state + "Background", "Workspace.Panel", 3.0);
    }

    [Fact]
    public void LightExpanderSeparatorAndSliderKeysUsePaletteValues()
    {
        AssertContrast("Light", "ExpanderIconForeground", "Workspace.Panel", 4.5);
        AssertContrast("Light", "ExpanderContentForeground", "Workspace.Panel", 4.5);
        AssertContrast("Light", "ExpanderHeaderForeground", "Workspace.Panel", 4.5);
        Assert.Equal(Color("Light", "Workspace.Muted"), Color("Light", "ExpanderIconForeground"));
        Assert.Equal(Color("Light", "Workspace.Text"), Color("Light", "ExpanderContentForeground"));
        Assert.Equal(Color("Light", "Workspace.Text"), Color("Light", "ExpanderHeaderForeground"));
        foreach (string key in new[] { "ExpanderSeparatorBorderBrush", "MenuItemSeparatorBackground", "CommandBarSeparatorBackground", "DateTimePickerSeparatorBackground", "SliderTickForeground" })
        {
            Assert.Equal(Color("Light", "Workspace.Border"), Color("Light", key));
            AssertContrast("Light", key, "Workspace.Panel", 2.8);
        }
        Assert.Equal(Color("Light", "Workspace.ProgressTrack"), Color("Light", "SliderTrackBackground"));
        AssertContrast("Light", "SliderTrackBackground", "Workspace.Panel", 3.0);
        foreach (string key in new[] { "SliderTrackForeground", "SliderThumbBorderBrush", "SliderThumbPointeroverBorderBrush", "SliderThumbPressedBorderBrush" })
            AssertContrast("Light", key, "Workspace.Panel", 4.5);
        Assert.Equal(Color("Light", "Workspace.Accent"), Color("Light", "SliderTrackForeground"));
        Assert.Equal(Color("Light", "Workspace.Accent"), Color("Light", "SliderThumbBorderBrush"));
        // A hyperlink button (provision) reads as text in all of its states.
        foreach (string key in new[] { "HyperlinkButtonForeground", "HyperlinkButtonPointeroverForeground", "HyperlinkButtonPressedForeground" })
            foreach (string surface in new[] { "Workspace.Panel", "Workspace.Hover", "Workspace.AccentSurface" })
                AssertContrast("Light", key, surface, 4.5);
    }

    // The dimming rows of the review: disabled chips at 0.75 (label 6.17:1 on ChipSurface), the disabled settings field label in Muted
    // (6.66:1 on Panel, was 60 % opacity at 4.32:1) and the archive quote bar at 0.8 (4.79:1 on Card, was 0.55 at 2.71:1).
    [Fact]
    public void DimmedRowsKeepTheirTextAndMarksReadable()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains("<Style Selector=\"Button.chip:disabled\"><Setter Property=\"Opacity\" Value=\"0.75\" /></Style>", styles);
        Assert.DoesNotContain("Button.chip:disabled\"><Setter Property=\"Opacity\" Value=\"0.6\"", styles);
        string settings = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        Assert.Contains("Selector=\"TextBlock.workspace-field-label:disabled\"><Setter Property=\"Foreground\" Value=\"{DynamicResource Workspace.Muted}\" />", settings);
        Assert.DoesNotContain("workspace-field-label:disabled\"><Setter Property=\"Opacity\"", settings);
        string archive = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.Contains("Fill=\"{DynamicResource Workspace.Accent}\" Opacity=\"0.8\"", archive);
        Assert.DoesNotContain("Opacity=\"0.55\"", archive);
        // 0.75 x the chip label (Workspace.Text on ChipSurface composited over Panel) and the 0.8 quote bar over Card, in Light.
        Assert.True(Composite("#1A1A1A", "Workspace.ChipSurface", 0.75, "Workspace.Panel") >= 4.5);
        Assert.True(CompositeOver("Workspace.Accent", "Workspace.Card", 0.8) >= 4.5);
    }

    private static double Composite(string foregroundHex, string surfaceKey, double opacity, string underKey)
    {
        return ContrastRules.CompositeContrast(foregroundHex, Color("Light", surfaceKey), opacity, Color("Light", underKey));
    }

    private static double CompositeOver(string fillKey, string underKey, double opacity) =>
        Contrast(Blend(Color("Light", fillKey), Color("Light", underKey), opacity), Color("Light", underKey));

    private static string Blend(string top, string under, double alpha) => ContrastRules.Blend(top, under, alpha);

    [Fact]
    public void LightScrollbarThumbsReadOnTheSurfacesTheyScrollOver()
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Card", "Workspace.Overlay" })
        {
            AssertContrast("Light", "ScrollBarThumbForeground", surface, 3.0);
            AssertContrast("Light", "ScrollBarThumbPointeroverForeground", surface, 3.0);
        }
    }

    // Status colours are an addition to text, icon and surface, but they should still not collapse into one another
    // or into the accent for a viewer with normal colour vision (CIELAB dE76 from the dictionary values).
    [Theory]
    [InlineData("Dark", 24.0)]
    [InlineData("Light", 17.0)]
    public void StatusColoursStayApartFromEachOtherAndFromTheAccent(string theme, double minimum)
    {
        string[] keys = { "Workspace.Error", "Workspace.Warning", "Workspace.Success", "Workspace.Accent" };
        for (int i = 0; i < keys.Length; i++)
            for (int j = i + 1; j < keys.Length; j++)
            {
                double distance = DeltaE(Color(theme, keys[i]), Color(theme, keys[j]));
                Assert.True(distance >= minimum, $"{theme}: {keys[i]} and {keys[j]} are only {distance:0.0} dE apart");
            }
    }

    // The export dialog card (Panel) must still stand out from the page the Scrim dims behind it (WCAG 1.4.11).
    // Dark is a documented soft edge (the card has CardEdge); Light is held to 3:1.
    [Theory, InlineData("Dark", 1.1), InlineData("Light", 3.0)]
    public void ExportDialogCardStandsOutFromTheScrimBackdrop(string theme, double minimum)
    {
        string scrim = Color(theme, "Workspace.Scrim");
        Assert.Equal(9, scrim.Length); // #AARRGGBB
        double alpha = Channel(scrim, 0) / 255d;
        string under = Color(theme, "Workspace.Background");
        // Blend in sRGB space like the compositor does, then measure the result against the card.
        string dimmed = "#" + string.Concat(Enumerable.Range(1, 3).Select(index =>
            ((int)Math.Round(Channel(under, index - 1) * (1 - alpha) + Channel(scrim, index) * alpha)).ToString("X2", CultureInfo.InvariantCulture)));
        Assert.Equal(dimmed, ContrastRules.CompositeOver(scrim, under));
        ContrastRule rule = Assert.Single(ContrastRules.For(Base(theme)), rule => rule.Backdrop == "Workspace.Background");
        Assert.Equal(minimum, rule.Minimum);
        Assert.Empty(ContrastRules.Check(Palette(theme), new[] { rule }));
        double ratio = Contrast(Color(theme, "Workspace.Panel"), dimmed);
        Assert.True(ratio >= minimum, $"{theme}: dialog card on the dimmed background is {ratio:0.00}:1, needs {minimum:0.0}:1");
    }

    // UX review 2026-11-13 (hover and pop-up contrast): a hover fill alone cannot reach 3:1 against its resting surface while text
    // keeps 4.5:1 on it, so the 1-px ControlBorder outline carries the hover boundary. It must clear 3:1 on the Hover fill too; the
    // earlier Light #7A7062 was 2.99:1 there.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void HoverOutlineReachesThreeToOneOnTheHoverFill(string theme)
    {
        foreach (string surface in new[] { "Workspace.Hover", "Workspace.Raised", "Workspace.AccentSurface", "Workspace.Overlay" })
            AssertContrast(theme, "Workspace.ControlBorder", surface, 3.0);
        // The selection bar and the pressed outline: the accent on the selected fill and on the bars around it.
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Panel", "Workspace.Overlay" })
            AssertContrast(theme, "Workspace.Accent", surface, 3.0);
    }

    [Fact]
    public void LightControlBorderIsTheDarkerWarmGreyInEveryKeyThatMirrorsIt()
    {
        Assert.Equal("#726858", Color("Light", "Workspace.ControlBorder"));
        foreach (string key in new[] { "Workspace.CardEdge", "ButtonDefaultPointeroverBorderBrush", "TextBoxDefaultBorderBrush", "TextBoxPointeroverBorderBrush",
                     "ComboBoxSelectorBorderBrush", "ComboBoxSelectorPointeroverBorderBrush", "CheckBoxDefaultBorderBrush", "ScrollBarThumbForeground",
                     "Workspace.ChipBorder", "Workspace.ChipSourceBorder", "ClosableTagBorder" })
            Assert.Equal(Color("Light", "Workspace.ControlBorder"), Color("Light", key));
    }

    // Semi's own overlay and row keys follow the palette in both themes: one overlay surface with the interactive outline, one hover fill,
    // pressed and selected on the accent surface, palette text, Muted for shortcut and disabled text, and no Semi blue.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void SemiOverlaysAndRowsUseThePaletteStates(string theme)
    {
        static void Same(string theme, string token, params string[] keys)
        {
            foreach (string key in keys) Assert.True(Color(theme, token) == Color(theme, key), $"{theme}: {key} is {Color(theme, key)}, expected {token} {Color(theme, token)}");
        }
        Same(theme, "Workspace.Overlay", "MenuFlyoutBackground", "ComboBoxPopupBackground", "AutoCompleteBoxPopupBackground", "PopConfirmBackground",
            "NotificationCardBackground", "FlyoutBackground", "ToolTipBackground");
        Same(theme, "Workspace.ControlBorder", "MenuFlyoutBorderBrush", "ComboBoxPopupBorderBrush", "AutoCompleteBoxPopupBorderBrush", "PopConfirmBorderBrush",
            "NotificationCardBorderBrush", "FlyoutBorderBrush", "ToolTipBorderBrush", "TabItemLinePipePointeroverBorderBrush");
        Same(theme, "Workspace.Hover", "MenuItemPointeroverBackground", "ComboBoxItemPointeroverBackground", "ComboBoxItemFocusBackground",
            "ListBoxItemPointeroverBackground", "TabItemLineHeaderPointeroverBackground", "ComboBoxSelectorPointeroverBackground", "DataGridRowPointeroverBackground");
        Same(theme, "Workspace.AccentSurface", "MenuItemPressedBackground", "ComboBoxItemPressedBackground", "ListBoxItemPressedBackground",
            "TabItemLineHeaderPressedBackground", "ComboBoxSelectorPressedBackground", "ComboBoxItemSelectedBackground",
            "ComboBoxItemSelectedPointeroverBackground", "ListBoxItemSelectedBackground", "ListBoxItemSelectedPointeroverBackground");
        Same(theme, "Workspace.Text", "ToolTipForeground", "FlyoutForeground", "MenuItemForeground", "MenuItemPointeroverForeground", "ComboBoxItemForeground",
            "ComboBoxItemPointeroverForeground", "ComboBoxItemSelectedForeground", "ListBoxItemPointeroverForeground", "ListBoxItemSelectedForeground",
            "TabItemLineHeaderPointeroverForeground", "TabItemLineHeaderSelectedForeground");
        Same(theme, "Workspace.Muted", "MenuItemInputGestureTextForeground", "MenuItemDisabledForeground", "MenuItemDisabledInputGestureTextForeground",
            "ListBoxItemDisabledForeground", "TabItemHeaderDisabledForeground", "TabItemLineHeaderForeground", "PopConfirmContentForeground");
        Same(theme, "Workspace.Accent", "TabItemLinePipeSelectedBackground", "TabItemIconSelectedForeground", "TabItemLinePipePressedBorderBrush",
            "ComboBoxSelectorPressedBorderBrush", "ComboBoxSelectorFocusBorderBrush");
        Same(theme, "Workspace.Raised", "ComboBoxSelectorBackground", "ComboBoxSelectorFocusBackground");
        // The resting line-tab label was Semi's 62 % black, 4.40:1 on the Light panel.
        AssertContrast(theme, "TabItemLineHeaderForeground", "Workspace.Panel", 4.5);
        XElement dictionary = Assert.Single(Styles().Descendants(A + "ResourceDictionary"), element => (string?)element.Attribute(X + "Key") == theme);
        Assert.DoesNotContain(dictionary.Elements(), element => string.Equals(element.Value.Trim(), "#54A9FF", StringComparison.OrdinalIgnoreCase));
    }

    // 2026-11-14: Semi's primary blue (#54A9FF in Dark, #0064FA in Light) and its blue family were still behind field focus, text selection,
    // check box and radio button states, slider, progress, pagination, selected tabs, info banner and toast, loading icon and badge. Every one
    // of those keys is an accent/palette token now, and the pairs that carry text, glyphs and state boundaries keep their contrast.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void SemiBlueLeftoversAreOnThePaletteAccent(string theme)
    {
        static void Same(string theme, string token, params string[] keys)
        {
            foreach (string key in keys) Assert.True(Color(theme, token) == Color(theme, key), $"{theme}: {key} is {Color(theme, key)}, expected {token} {Color(theme, token)}");
        }
        Same(theme, "Workspace.Accent", "TextBoxFocusBorderBrush", "ComboBoxSelectorBorderedFocusBorderBrush", "ComboBoxSelectorBorderedPressedBorderBrush",
            "AdornerLayerBorderBrush", "ToggleButtonPrimaryIndeterminateBorderBrush", "TextBoxSelectionBackground", "TextBlockSelectionBackground",
            "CheckBoxCheckedDefaultBackground", "CheckBoxCheckedDefaultBorderBrush", "CheckBoxPointeroverBorderBrush", "CheckBoxCardCheckedBorderBrush",
            "RadioButtonCheckIconDefaultBackground", "RadioButtonCheckIconDefaultBorderBrush", "RadioButtonUncheckIconPointeroverBorderBrush",
            "RadioButtonButtonCheckedForeground", "RadioButtonCardCheckDefaultBorderBrush", "SliderTrackForeground", "SliderThumbBorderBrush",
            "ProgressBarIndicatorBrush", "ProgressBarPrimaryForeground", "PaginationButtonSelectedForeground", "TabItemButtonHeaderSelectedForeground",
            "BreadcrumbItemPointeroverForeground", "BannerInformationBorderBrush", "NotificationCardInformationIconForeground",
            "NotificationCardLightInformationBorderBrush", "LoadingIconForeground", "BadgePrimaryBadgeBackground", "BadgeInvertedPrimaryBadgeForeground",
            "BadgeLightPrimaryBadgeForeground");
        Same(theme, "Workspace.AccentHover", "CheckBoxCheckedPointeroverBackground", "CheckBoxCheckedPointeroverBorderBrush", "CheckBoxCardCheckedPointeroverBorderBrush",
            "RadioButtonCheckIconPointeroverBackground", "RadioButtonCheckIconPointeroverBorderBrush", "RadioButtonCardCheckPointeroverBorderBrush",
            "SliderThumbPointeroverBorderBrush");
        Same(theme, "Workspace.AccentSurface", "PaginationButtonSelectedBackground", "TabItemLineHeaderSelectedBackground", "TabItemButtonHeaderSelectedBackground",
            "BannerInformationBackground", "NotificationCardLightInformationBackground", "BadgeLightPrimaryBadgeBackground", "CheckBoxCardCheckedBackground",
            "RadioButtonCardCheckedBackground", "RadioButtonButtonCheckedBackground");
        Same(theme, "Workspace.OnAccent", "TextBoxSelectionForeground", "TextBlockSelectionForeground", "CheckBoxGlyphFill", "RadioButtonCheckGlyphFill", "BadgeForeground");
        Same(theme, "Workspace.ControlBorder", "CheckBoxDefaultBorderBrush", "RadioButtonUncheckIconDefaultBorderBrush", "TextBoxBorderedPointeroverBorderBrush",
            "ComboBoxSelectorBorderedPointeroverBorderBrush");
        // Pressed: the pressed accent of the buttons in Light, the accent itself in Dark (buttons keep their own pressed keys).
        string pressed = Color(theme, "ButtonSolidPrimaryPressedBackground");
        foreach (string key in new[] { "CheckBoxCheckedPressedBackground", "CheckBoxCheckedPressedBorderBrush", "RadioButtonCheckIconPressedBackground",
                     "RadioButtonCheckIconPressedBorderBrush", "SliderThumbPressedBorderBrush" })
            Assert.True(pressed == Color(theme, key), $"{theme}: {key} is {Color(theme, key)}, expected the pressed accent {pressed}");
        // The disabled checked fill and the disabled thumb edge are one quiet outline tone, not blue; it matches the disabled button outline.
        foreach (string key in new[] { "CheckBoxCheckedDisabledBackground", "CheckBoxCheckedDisabledBorderBrush", "RadioButtonCheckIconDisabledBackground",
                     "RadioButtonCheckIconDisabledBorderBrush", "SliderThumbDisabledBorderBrush", "CheckBoxCardCheckedDisabledBorderBrush" })
            Assert.Equal(Color(theme, "ButtonSolidDisabledBorderBrush"), Color(theme, key));

        // Text and glyphs on the accent: selection (TextBox and selectable text), the check and radio dot on the checked fill in every state
        // (hover is AccentHover, pressed the pressed accent), the badge label on its fill.
        AssertContrast(theme, "TextBoxSelectionForeground", "TextBoxSelectionBackground", 4.5);
        AssertContrast(theme, "TextBlockSelectionForeground", "TextBlockSelectionBackground", 4.5);
        AssertContrast(theme, "CheckBoxGlyphFill", "CheckBoxCheckedDefaultBackground", 4.5);
        AssertContrast(theme, "CheckBoxGlyphFill", "CheckBoxCheckedPointeroverBackground", 4.5);
        AssertContrast(theme, "CheckBoxGlyphFill", "CheckBoxCheckedPressedBackground", 4.5);
        AssertContrast(theme, "RadioButtonCheckGlyphFill", "RadioButtonCheckIconDefaultBackground", 4.5);
        AssertContrast(theme, "RadioButtonCheckGlyphFill", "RadioButtonCheckIconPointeroverBackground", 4.5);
        AssertContrast(theme, "RadioButtonCheckGlyphFill", "RadioButtonCheckIconPressedBackground", 4.5);
        AssertContrast(theme, "BadgeForeground", "BadgePrimaryBadgeBackground", 4.5);
        // Accent text on the surface the selected control sits on: pagination, tab, button radio, light badge and inverted badge.
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.Panel", "Workspace.Background", "Workspace.Raised", "Workspace.Card" })
        {
            AssertContrast(theme, "PaginationButtonSelectedForeground", surface, 4.5);
            AssertContrast(theme, "BreadcrumbItemPointeroverForeground", surface, 4.5);
        }
        AssertContrast(theme, "TabItemButtonHeaderSelectedForeground", "TabItemButtonHeaderSelectedBackground", 4.5);
        AssertContrast(theme, "RadioButtonButtonCheckedForeground", "RadioButtonButtonCheckedBackground", 4.5);
        AssertContrast(theme, "BadgeLightPrimaryBadgeForeground", "BadgeLightPrimaryBadgeBackground", 4.5);
        AssertContrast(theme, "Workspace.Text", "BannerInformationBackground", 4.5); // banner and toast text on the info surface
        AssertContrast(theme, "Workspace.Text", "NotificationCardLightInformationBackground", 4.5);
        AssertContrast(theme, "Workspace.Text", "TabItemLineHeaderSelectedBackground", 4.5);
        AssertContrast(theme, "Workspace.Text", "TabItemButtonHeaderSelectedBackground", 4.5);
        // State boundaries and marks (3:1): focus and checked fills against every surface they sit on, the info edge on its own surface and
        // the panel, the info icon and loading icon on the toast/banner surface and the overlay.
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay" })
        {
            foreach (string key in new[] { "TextBoxFocusBorderBrush", "ComboBoxSelectorBorderedFocusBorderBrush", "CheckBoxCheckedDefaultBackground",
                         "CheckBoxCheckedDefaultBorderBrush", "CheckBoxPointeroverBorderBrush", "RadioButtonCheckIconDefaultBackground",
                         "RadioButtonUncheckIconPointeroverBorderBrush", "SliderTrackForeground", "SliderThumbBorderBrush", "ProgressBarPrimaryForeground",
                         "AdornerLayerBorderBrush", "LoadingIconForeground", "TextBoxSelectionBackground", "TextBlockSelectionBackground",
                         "CheckBoxDefaultBorderBrush", "RadioButtonUncheckIconDefaultBorderBrush" })
                AssertContrast(theme, key, surface, 3.0);
        }
        AssertContrast(theme, "BannerInformationBorderBrush", "BannerInformationBackground", 3.0);
        AssertContrast(theme, "NotificationCardLightInformationBorderBrush", "NotificationCardLightInformationBackground", 3.0);
        AssertContrast(theme, "NotificationCardInformationIconForeground", "NotificationCardLightInformationBackground", 3.0);
        AssertContrast(theme, "BannerInformationBorderBrush", "Workspace.Panel", 3.0);
        AssertContrast(theme, "CheckBoxDefaultBorderBrush", "Workspace.AccentSurface", 3.0);
        AssertContrast(theme, "CheckBoxDefaultBorderBrush", "Workspace.Hover", 3.0);
        AssertContrast(theme, "CheckBoxCheckedPointeroverBackground", "Workspace.Panel", 3.0);
        AssertContrast(theme, "CheckBoxCheckedPressedBackground", "Workspace.Panel", 3.0);
        AssertContrast(theme, "RadioButtonCheckIconPointeroverBackground", "Workspace.Panel", 3.0);
        AssertContrast(theme, "SliderThumbPointeroverBorderBrush", "Workspace.Panel", 3.0);
        AssertContrast(theme, "SliderThumbPressedBorderBrush", "Workspace.Panel", 3.0);

        // The two drag previews keep Semi's translucent look but on the accent hue (#66 alpha over the accent RGB).
        foreach (string key in new[] { "GridSplitterPreviewBackground", "DataGridDropLocationIndicatorBackground" })
            Assert.Equal("#66" + Color(theme, "Workspace.Accent")[1..], Color(theme, key));

        // No value in the theme dictionary is a Semi blue/indigo-family value any more (alpha ignored).
        string[] semiBlues =
        {
            "54A9FF", "7FC1FF", "A9D7FF", "135CB8", "0A4694", "053170", "2990FF", "1D75DB", "D4ECFF", "EFF8FF", "0041C5",
            "0064FA", "0062D6", "004FB3", "98CDFD", "EAF5FF", "CBE7FE", "65B2FC", "3295FB", "003D8F", "002C6B"
        };
        XElement dictionary = Assert.Single(Styles().Descendants(A + "ResourceDictionary"), element => (string?)element.Attribute(X + "Key") == theme);
        foreach (XElement element in dictionary.Elements())
        {
            string value = element.Value.Trim().TrimStart('#').ToUpperInvariant();
            if (value.Length == 8) value = value[2..];
            Assert.False(semiBlues.Contains(value), $"{theme}: {(string?)element.Attribute(X + "Key")} is the Semi blue #{value}");
        }
    }

    // The red close hover is #C42B1C in both themes; where the fill is under 3:1 on the title bar (Dark, 2.95:1) its edge carries the boundary.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void CaptionCloseHoverHasAThreeToOneBoundary(string theme)
    {
        double fill = Contrast("#C42B1C", Color(theme, "Workspace.Panel"));
        double edge = Contrast(Color(theme, "Workspace.CaptionCloseEdge"), Color(theme, "Workspace.Panel"));
        Assert.True(Math.Max(fill, edge) >= 3.0, $"{theme}: close hover fill {fill:0.00}:1, edge {edge:0.00}:1 on the bar");
        Assert.True(Contrast("#FFFFFF", "#C42B1C") >= 4.5);
    }

    // A11y review A12 (plugin lifecycle step chips, banner severity icons): the chip text and outline pairs of WorkspaceStyles.axaml.
    // Done step: Success text and 1-px outline on SuccessSurface; next step: Accent 2-px outline on AccentSurface (a graphic: 3:1,
    // its text is Workspace.Text); neutral step: ChipBorder on its AccentSurface fill; every outline also against the panel and the raised
    // chip surface it sits next to. The banner severity icons are drawn in the edge colour on the banner's own tint (3:1, a graphic).
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void StepChipsAndBannerSeverityIconsReadInBothThemes(string theme)
    {
        // Done: text 4.5:1, outline 3:1 on the fill and against the page it sits on.
        AssertContrast(theme, "Workspace.Success", "Workspace.SuccessSurface", 4.5);
        foreach (string around in new[] { "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.ChipSurface" })
            AssertContrast(theme, "Workspace.Success", around, 3.0);
        // Next: the outline separates the chip from the panel and from its own fill; the label is Workspace.Text (checked on every surface).
        AssertContrast(theme, "Workspace.Accent", "Workspace.AccentSurface", 3.0);
        foreach (string around in new[] { "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.ChipSurface" })
            AssertContrast(theme, "Workspace.Accent", around, 3.0);
        AssertContrast(theme, "Workspace.Text", "Workspace.AccentSurface", 4.5);
        // Neutral and later steps: the chip outline on the accent fill the chip-badge style uses.
        AssertContrast(theme, "Workspace.ChipBorder", "Workspace.AccentSurface", 3.0);
        AssertContrast(theme, "Workspace.ChipBorder", "Workspace.ChipSurface", 3.0);
        // Banner severity icons: error, warning, information (accent) and success strokes on their own tint and on the panel.
        foreach (var (tone, surface) in new[] { ("Error", "ErrorSurface"), ("Warning", "WarningSurface"), ("Accent", "AccentSurface"), ("Success", "SuccessSurface") })
        {
            AssertContrast(theme, "Workspace." + tone, "Workspace." + surface, 3.0);
            AssertContrast(theme, "Workspace." + tone, "Workspace.Panel", 3.0);
        }
    }

    // A11y review AX-02: the Settings ToggleSwitch ran on Semi's own colours (measured from rendered pixels: Light off track #DAD7D0 on the #E3DFD8
    // page 1.08:1 and on track #3BB346 2.05:1, Dark off track #2E3031 on #121416 1.39:1, white knob on the Light on track 2.72:1 and on the Dark on
    // track 2.24:1). The tracks are now palette tokens (off = ControlBorder, on = Accent, knob = OnAccent): 3:1 on every surface in both states,
    // the knob 4.5:1 on every track. On/off is still also carried by the knob position, never by colour alone.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ToggleSwitchTracksAndKnobReachNonTextContrastInBothThemes(string theme)
    {
        string page = Color(theme, "Workspace.Background");
        Assert.True(Contrast(theme == "Dark" ? "#2E3031" : "#DAD7D0", page) < 3.0, "the Semi off track this replaces was below 3:1");
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay", "Workspace.Hover", "Workspace.AccentSurface" })
        {
            AssertContrast(theme, "ToggleSwitchContainerUnCheckedDefaultBackground", surface, 3.0);
            AssertContrast(theme, "ToggleSwitchContainerCheckedDefaultBackground", surface, 3.0);
        }
        foreach (string key in new[] { "ToggleSwitchContainerUnCheckedPointeroverBackground", "ToggleSwitchContainerUnCheckedPressedBackground",
                     "ToggleSwitchContainerCheckedPointeroverBackground", "ToggleSwitchContainerCheckedPressedBackground" })
            AssertContrast(theme, key, "Workspace.Panel", 3.0);
        foreach (string state in new[] { "UnCheckedDefault", "UnCheckedPointerover", "UnCheckedPressed", "CheckedDefault", "CheckedPointerover", "CheckedPressed" })
            AssertContrast(theme, "ToggleSwitchIndicatorBackground", "ToggleSwitchContainer" + state + "Background", 4.5);
        // The tracks are the theme's own tokens, so a custom accent or outline colour moves the switch with it.
        Assert.Equal(Color(theme, "Workspace.ControlBorder"), Color(theme, "ToggleSwitchContainerUnCheckedDefaultBackground"));
        Assert.Equal(Color(theme, "Workspace.Accent"), Color(theme, "ToggleSwitchContainerCheckedDefaultBackground"));
        Assert.Equal(Color(theme, "Workspace.OnAccent"), Color(theme, "ToggleSwitchIndicatorBackground"));
        // The two resting tracks are different colours (the knob position stays the primary on/off cue).
        Assert.NotEqual(Color(theme, "ToggleSwitchContainerUnCheckedDefaultBackground"), Color(theme, "ToggleSwitchContainerCheckedDefaultBackground"));
    }

    private static int Channel(string hex, int index) =>
        int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static void AssertContrast(string theme, string foreground, string background, double minimum)
    {
        // Every original assertion is retained as a contract pin. It must also exist, with the exact same
        // minimum and state keys, in the reusable product data and pass through the product checker.
        ContrastRule rule = Assert.Single(ContrastRules.For(Base(theme)), rule => rule.Foreground == foreground &&
            rule.Background == background && rule.Minimum == minimum && rule.Backdrop == null);
        Assert.Empty(ContrastRules.Check(Palette(theme), new[] { rule }));
        double ratio = Contrast(Color(theme, foreground), Color(theme, background));
        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum:0.0}:1");
    }

    private static double Contrast(string first, string second) => ContrastRules.Contrast(first, second);

    private static double DeltaE(string first, string second) => ContrastRules.DeltaE(first, second);

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ResolvedPalettePassesAllProductContrastRules(string theme) =>
        Assert.Empty(ContrastRules.Check(Palette(theme)));

    private static ContrastTheme Base(string theme) => Enum.Parse<ContrastTheme>(theme);

    private static ContrastPalette Palette(string theme)
    {
        XElement dictionary = Assert.Single(Styles().Descendants(A + "ResourceDictionary"), element => (string?)element.Attribute(X + "Key") == theme);
        return new(Base(theme), dictionary.Elements().ToDictionary(element => (string)element.Attribute(X + "Key")!, element => element.Value.Trim(), StringComparer.Ordinal));
    }

    private static string Color(string theme, string key) =>
        Assert.Single(Assert.Single(Styles().Descendants(A + "ResourceDictionary"), element => (string?)element.Attribute(X + "Key") == theme)
            .Elements(), element => (string?)element.Attribute(X + "Key") == key).Value.Trim();

    private static XElement Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
