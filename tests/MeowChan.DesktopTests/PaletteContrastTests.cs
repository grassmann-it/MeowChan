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

    // The charcoal bars of the Light theme are the download progress bars (Workspace.Progress is #333333 there; title bar, header,
    // footer and toolbars are warm Panel/Background tones, not dark grey). The fill is the bar: it needs 3:1 against every surface it
    // is drawn on and against its own track. The empty track is only the remaining extent (the count beside it says the same) and
    // stays a soft tone, so it is deliberately not held to 3:1. Scrollbar thumbs are bars too.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ProgressBarFillReadsOnItsTrackAndOnEverySurfaceItIsDrawnOn(string theme)
    {
        AssertContrast(theme, "Workspace.Progress", "Workspace.ProgressTrack", 3.0);
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Hover", "Workspace.AccentSurface", "Workspace.Overlay" })
            AssertContrast(theme, "Workspace.Progress", surface, 3.0);
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
