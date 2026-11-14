using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests.DesignLint;

// A round bordered button already is a circle: its icon must not be a circled catalog icon (feather help-circle, x-circle, plus-circle,
// info, circle ...), which would draw two rings ("Fragezeichen im Kreis ist doppelt gemoppelt"). The round button is the shared
// help button (class help-round); this lint keeps it the only round button of the style file and keeps circled masks out of its owners.
public sealed class RoundButtonSingleCircleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    // Catalog masks that draw a ring of their own.
    private static readonly string[] CircledMasks = ["help-circle", "x-circle", "plus-circle", "circle", "info", "check-circle", "alert-circle", "alert-triangle"];

    [Fact]
    public void TheHelpRoundButtonIsTheOnlyFullyRoundButtonStyle()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        string[] round = styles.Descendants(A + "Style")
            .Where(style => ((string?)style.Attribute("Selector") ?? "").StartsWith("Button.", StringComparison.Ordinal))
            .Where(style => style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "CornerRadius" && (string?)setter.Attribute("Value") == "16"))
            .Select(style => (string)style.Attribute("Selector")!).ToArray();
        // A new round button style needs the same review: its icon has no ring of its own.
        Assert.Equal(["Button.help-round"], round);
    }

    [Fact]
    public void OnlyTheSharedHelpButtonAddsTheRoundClassAndItsContentIsNoCircledMask()
    {
        List<string> users = [];
        foreach (string file in ProductFiles())
        {
            string text = File.ReadAllText(file);
            if (text.Contains("help-round", StringComparison.Ordinal) && !file.EndsWith("WorkspaceStyles.axaml", StringComparison.Ordinal)) users.Add(Path.GetFileName(file));
        }
        Assert.Equal(["HelpButton.cs"], users.Order(StringComparer.Ordinal).ToArray());
        string help = string.Join("\n", File.ReadAllLines(Root("Desktop/Controls/HelpButton.cs")).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        foreach (string mask in CircledMasks) Assert.DoesNotContain("\"" + mask + "\"", help, StringComparison.Ordinal);
    }

    [Fact]
    public void NoHelpButtonOwnerReplacesTheContentOfTheHelpButtonWithACircledMask()
    {
        // The help button is declared by key (HelpKey="…") and keeps its own content; no owner sets Content on it.
        Regex content = new("HelpButton[^>]*\\sContent\\s*=", RegexOptions.CultureInvariant);
        foreach (string file in ProductFiles().Where(file => file.EndsWith(".axaml", StringComparison.Ordinal)))
            Assert.DoesNotMatch(content, File.ReadAllText(file));
        foreach (string file in ProductFiles().Where(file => file.EndsWith(".cs", StringComparison.Ordinal)))
            foreach (string line in File.ReadAllLines(file).Where(line => line.Contains("new HelpButton", StringComparison.Ordinal) || line.Contains("new MeowChan.Desktop.Controls.HelpButton", StringComparison.Ordinal)))
                Assert.DoesNotMatch(new Regex("\\bContent\\s*=", RegexOptions.CultureInvariant), line);
    }

    private static IEnumerable<string> ProductFiles() =>
        Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../..", relative));
}
