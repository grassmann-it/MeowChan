using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// QW-20: the marked-favorite tint is a theme token; Gold (1.2:1 on the Light panel) is only for Dark.
public sealed class FavoriteTintContrastTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void FavoriteTintHasAtLeastThreeToOneAgainstThePanelsItSitsOn(string theme)
    {
        XElement styles = XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../..", "Desktop/WorkspaceStyles.axaml"))).Root!;
        XElement dictionary = Assert.Single(styles.Descendants(A + "ResourceDictionary")
            .Where(element => (string?)element.Attribute(X + "Key") == theme));
        double tint = Luminance(Brush(dictionary, "Workspace.Favorite"));
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised" })
            Assert.True(Ratio(tint, Luminance(Brush(dictionary, surface))) >= 3.0, theme + " Workspace.Favorite on " + surface);
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;

    private static string Brush(XElement dictionary, string key) => Assert.Single(dictionary.Elements()
        .Where(element => (string?)element.Attribute(X + "Key") == key)).Value.Trim();

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            double value = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}
