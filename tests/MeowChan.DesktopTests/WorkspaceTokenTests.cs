using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Parsed source contracts for the D0 design tokens in WorkspaceStyles.axaml: no Avalonia application,
// no rendering. They pin the token set, its values in both themes and the code mirror of the title bar.
public sealed class WorkspaceTokenTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] SurfaceKeys =
    {
        "Workspace.Card", "Workspace.CardEdge", "Workspace.Overlay", "Workspace.Shadow.Overlay", "Workspace.Scrim"
    };

    [Theory]
    [InlineData("Dark", "Workspace.Card", "#20242A")]
    [InlineData("Dark", "Workspace.CardEdge", "#5A636E")]
    [InlineData("Dark", "Workspace.Scrim", "#66000000")]
    [InlineData("Dark", "Workspace.Shadow.Overlay", "0 4 16 0 #66000000")]
    [InlineData("Light", "Workspace.Card", "#FAF7F1")]
    [InlineData("Light", "Workspace.CardEdge", "#726858")]
    [InlineData("Light", "Workspace.Scrim", "#66000000")]
    [InlineData("Light", "Workspace.Shadow.Overlay", "0 4 16 0 #26000000")]
    public void SurfaceTokensExistInBothThemesWithTheDesignValues(string theme, string key, string value) =>
        Assert.Equal(value, Entry(Theme(theme), key).Value.Trim());

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void OverlayEqualsPanelAndTheLightCardEdgeEqualsTheControlBorder(string theme)
    {
        XElement dictionary = Theme(theme);
        Assert.Equal(Entry(dictionary, "Workspace.Panel").Value, Entry(dictionary, "Workspace.Overlay").Value);
        if (theme == "Light")
            Assert.Equal(Entry(dictionary, "Workspace.ControlBorder").Value, Entry(dictionary, "Workspace.CardEdge").Value);
        foreach (string key in SurfaceKeys) Assert.NotNull(Entry(dictionary, key));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void CardEdgeIsVisibleOnTheCardAndOnTheWindowBackground(string theme)
    {
        XElement dictionary = Theme(theme);
        double edge = Luminance(Entry(dictionary, "Workspace.CardEdge").Value.Trim());
        // Computed, not natively measured. Dark keeps the reader study's 3.03:1 edge (design language 4.3 proposed a softer #4A525C; D3 kept the shipped reader edge).
        Assert.True(Contrast(edge, Luminance(Entry(dictionary, "Workspace.Card").Value.Trim())) >= 1.9);
        Assert.True(Contrast(edge, Luminance(Entry(dictionary, "Workspace.Background").Value.Trim())) >= 2.3);
        Assert.NotEqual(Entry(dictionary, "Workspace.Card").Value, Entry(dictionary, "Workspace.Panel").Value);
    }

    [Theory]
    [InlineData("Workspace.Space.2", "2")]
    [InlineData("Workspace.Space.32", "32")]
    [InlineData("Workspace.Text.Caption", "11")]
    [InlineData("Workspace.Text.Meta", "12")]
    [InlineData("Workspace.Text.Body", "13")]
    [InlineData("Workspace.Text.Section", "14")]
    [InlineData("Workspace.Text.Subtitle", "16")]
    [InlineData("Workspace.Text.Title", "20")]
    [InlineData("Workspace.Size.Control", "30")]
    [InlineData("Workspace.Size.Input", "32")]
    [InlineData("Workspace.Size.Nav", "36")]
    [InlineData("Workspace.Size.Chip", "24")]
    [InlineData("Workspace.Size.ChipCompact", "18")]
    [InlineData("Workspace.Size.TitleBar", "48")]
    [InlineData("Workspace.Size.Caption", "44")]
    [InlineData("Workspace.Size.Icon", "16")]
    [InlineData("Workspace.Size.IconCaption", "10")]
    [InlineData("Workspace.Radius.None", "0")]
    [InlineData("Workspace.Radius.Badge", "3")]
    [InlineData("Workspace.Radius.Control", "5")]
    [InlineData("Workspace.Radius.Group", "8")]
    [InlineData("Workspace.Radius.Chip", "12")]
    [InlineData("Workspace.Radius.ChipCompact", "9")]
    [InlineData("Workspace.Inset.ChipCompact", "6,1")]
    [InlineData("Workspace.Inset.Card", "12,10,12,12")]
    [InlineData("Workspace.Inset.Overlay", "12")]
    public void ThemeNeutralTokensKeepTheirDesignValues(string key, string value) =>
        Assert.Equal(value, Entry(Root(), key).Value.Trim());

    [Fact]
    public void TitleBarTokenMatchesTheCodeMirror() =>
        Assert.Equal(WorkspaceMetrics.TitleBarHeight,
            double.Parse(Entry(Root(), "Workspace.Size.TitleBar").Value, CultureInfo.InvariantCulture));

    [Fact]
    public void ExistingKeysThatOtherCodeReadsAreStillDefined()
    {
        string[] legacy =
        {
            "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Border", "Workspace.ControlBorder",
            "Workspace.Text", "Workspace.Muted", "Workspace.Accent", "Workspace.AccentSurface",
            "Workspace.Card", "Workspace.CardEdge", "Workspace.Hover", "Workspace.OnAccent", "Workspace.AccentHover",
            "Workspace.Error", "Workspace.ErrorSurface", "Workspace.Warning", "Workspace.WarningSurface",
            "Workspace.Success", "Workspace.SuccessSurface", "Workspace.Progress", "Workspace.ProgressTrack"
        };
        foreach (string theme in new[] { "Dark", "Light" })
            foreach (string key in legacy) Assert.NotNull(Entry(Theme(theme), key));
        foreach (string key in new[] { "Workspace.Space.4", "Workspace.Space.8", "Workspace.Space.12", "Workspace.Space.16",
                     "Workspace.Space.24", "Workspace.Inset.Section", "Workspace.Inset.Message", "Workspace.Inset.Footer",
                     "Workspace.Inset.Chip" })
            Assert.NotNull(Entry(Root(), key));
    }

    private static XElement Root() => XDocument.Load(Source("Desktop/WorkspaceStyles.axaml")).Root!;

    private static XElement Theme(string theme) => Root().Descendants()
        .Single(element => element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(X + "Key") == theme);

    private static XElement Entry(XElement scope, string key) => scope.Descendants()
        .Single(element => (string?)element.Attribute(X + "Key") == key);

    private static double Luminance(string hex)
    {
        Color color = Color.Parse(hex);
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
