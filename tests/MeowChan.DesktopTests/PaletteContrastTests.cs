using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

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

    // The red close hover is #C42B1C in both themes; where the fill is under 3:1 on the title bar (Dark, 2.95:1) its edge carries the boundary.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void CaptionCloseHoverHasAThreeToOneBoundary(string theme)
    {
        double fill = Contrast("#C42B1C", Color(theme, "Workspace.Panel"));
        double edge = Contrast(Color(theme, "Workspace.CaptionCloseEdge"), Color(theme, "Workspace.Panel"));
        Assert.True(Math.Max(fill, edge) >= 3.0, $"{theme}: close hover fill {fill:0.00}:1, edge {edge:0.00}:1 on the bar");
        Assert.True(Contrast("#FFFFFF", "#C42B1C") >= 4.5);
    }

    private static int Channel(string hex, int index) =>
        int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static void AssertContrast(string theme, string foreground, string background, double minimum)
    {
        double ratio = Contrast(Color(theme, foreground), Color(theme, background));
        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum:0.0}:1");
    }

    private static double Contrast(string first, string second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double[] Linear(string hex) => Enumerable.Range(0, 3).Select(index =>
    {
        double value = int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }).ToArray();

    private static double Luminance(string hex)
    {
        double[] c = Linear(hex);
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
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

    private static double DeltaE(string first, string second)
    {
        double[] a = Lab(first), b = Lab(second);
        return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    }

    private static string Color(string theme, string key) =>
        Assert.Single(Assert.Single(Styles().Descendants(A + "ResourceDictionary"), element => (string?)element.Attribute(X + "Key") == theme)
            .Elements(), element => (string?)element.Attribute(X + "Key") == key).Value.Trim();

    private static XElement Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
