using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests.DesignLint;

// UX review 15.11.: the question mark in a circle was ugly, so the help button is a plain "?" on a flat square (class help-round, kept as the
// name). This lint keeps the style file free of round buttons and of a round focus ring, keeps the shared HelpButton the only owner of the class
// (nobody draws a help circle of their own) and keeps circled catalog masks (help-circle ...) out of it and out of the help commands.
public sealed class RoundButtonSingleCircleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    // Catalog masks that draw a ring of their own.
    private static readonly string[] CircledMasks = ["help-circle", "x-circle", "plus-circle", "circle", "info", "check-circle", "alert-circle", "alert-triangle"];

    [Fact]
    public void NoButtonStyleIsFullyRoundAndTheRoundFocusRingIsGone()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        string[] round = styles.Descendants(A + "Style")
            .Where(style => ((string?)style.Attribute("Selector") ?? "").StartsWith("Button.", StringComparison.Ordinal))
            .Where(style => style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "CornerRadius" && (string?)setter.Attribute("Value") == "16"))
            .Select(style => (string)style.Attribute("Selector")!).ToArray();
        // The help button was the one round button; it is a flat square now. A new round button style needs the same review.
        Assert.Empty(round);
        Assert.DoesNotContain("FocusAdorner.Round", File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheSharedHelpButtonAddsTheHelpClassAndItsContentIsNoCircledMask()
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
    public void TheHelpCommandsUseThePlainQuestionMarkMaskNotTheCircledOne()
    {
        // The keyboard-shortcut help commands (gallery toolbar, menus) show the same plain "?" as the help button.
        foreach (string id in new[] { "viewer.shortcut-help", "viewer.shortcut-hints", "gallery.layout.hints" })
            Assert.Equal("help", MeowChan.Desktop.Commands.CommandCatalog.Get(id).Icon);
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/help.png")));
        Assert.True(File.Exists(Root("Assets/icons/feather/help.svg")));
        Assert.DoesNotContain("<circle", File.ReadAllText(Root("Assets/icons/feather/help.svg")), StringComparison.Ordinal);
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
